using System.Net;
using System.Net.Sockets;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Owns access-token acquisition for the connector: proactive refresh, on-demand interactive
/// login (system browser + PKCE + loopback), and post-401 recovery. The protocol work — PKCE,
/// discovery, the code exchange, refresh — is delegated to <see cref="OidcClient"/>; this type
/// only wires the loopback browser, picks the redirect port, and persists tokens.
///
/// Thrown <see cref="LoginRequiredException"/> means "no valid token and interactive login is
/// not available" — callers turn it into an MCP isError with guidance.
/// </summary>
public sealed class OAuthTokenProvider(McpConnectorConfig config, ITokenStore store, ILogger logger)
{
    /// <summary>Refresh when the access token is within this window of expiry.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    /// <summary>Overall interactive-login budget (browser + user + callback).</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(180);

    // Serializes token acquisition so N parallel tool calls trigger exactly one login/refresh.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ITokenStore Store => store;

    /// <summary>
    /// Returns a valid bearer token: cached-and-fresh, proactively refreshed, or freshly obtained
    /// via interactive login. Honors <c>--no-interactive-login</c> by throwing
    /// <see cref="LoginRequiredException"/> instead of popping a browser.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stored = LoadValidStored();

            if (stored is not null && !stored.IsExpiredOrExpiring(RefreshSkew))
                return stored.AccessToken;

            // Expiring/expired but refreshable → refresh silently.
            if (stored is { HasRefreshToken: true })
            {
                var refreshed = await TryRefreshAsync(stored.RefreshToken!, ct);
                if (refreshed is not null)
                    return refreshed.AccessToken;

                logger.LogWarning("Silent token refresh failed; falling back to interactive login.");
                store.Delete(config.AuthorityHost);
            }

            // No usable stored token → interactive login (unless disabled).
            return (await LoginCoreAsync(ct)).AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Post-401 recovery: refresh and return the new token; if refresh fails, clear tokens and
    /// (when interactive is allowed) log in again. Returns null when no token could be obtained
    /// without interaction and interaction is disabled.
    /// </summary>
    public async Task<string?> RecoverFromUnauthorizedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stored = LoadValidStored();

            if (stored is { HasRefreshToken: true })
            {
                var refreshed = await TryRefreshAsync(stored.RefreshToken!, ct);
                if (refreshed is not null)
                    return refreshed.AccessToken;
            }

            // Refresh unavailable or failed: the stored access token is dead — clear it.
            store.Delete(config.AuthorityHost);

            if (config.NoInteractiveLogin)
                return null;

            return (await LoginCoreAsync(ct)).AccessToken;
        }
        catch (LoginRequiredException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a valid token if one can be obtained WITHOUT interaction (cached-and-fresh or
    /// silently refreshed), otherwise null. Never pops a browser. Used at startup so the manifest
    /// fetch never blocks on login.
    /// </summary>
    public async Task<string?> TryGetSilentAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stored = LoadValidStored();
            if (stored is null)
                return null;

            if (!stored.IsExpiredOrExpiring(RefreshSkew))
                return stored.AccessToken;

            if (stored.HasRefreshToken)
            {
                var refreshed = await TryRefreshAsync(stored.RefreshToken!, ct);
                return refreshed?.AccessToken;
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces an interactive login now (the <c>login</c> verb). Overwrites any stored tokens.</summary>
    public async Task<StoredTokens> LoginInteractiveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoginCoreAsync(ct, forceInteractive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes stored tokens for the configured authority (the <c>logout</c> verb).</summary>
    public void Logout() => store.Delete(config.AuthorityHost);

    /// <summary>Returns the stored bundle for the configured authority, or null.</summary>
    public StoredTokens? GetStored() => store.Load(config.AuthorityHost);

    /// <summary>Reads stored tokens, discarding any bundle that belongs to a different authority/scope.</summary>
    private StoredTokens? LoadValidStored()
    {
        var stored = store.Load(config.AuthorityHost);
        if (stored is null) return null;

        // Guard against a stale bundle from an old authority/scope set living under this host key.
        if (!string.Equals(stored.Authority, config.Authority, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Stored tokens were issued for a different authority ({Stored} vs {Current}); ignoring.",
                stored.Authority, config.Authority);
            return null;
        }

        return stored;
    }

    private async Task<StoredTokens?> TryRefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            // Refresh needs no browser/port; use the first contract redirect URI as a placeholder.
            var oidc = new OidcClient(BuildOptions(OAuthContract.RedirectUris[0], browser: null));
            var result = await oidc.RefreshTokenAsync(refreshToken, cancellationToken: ct);

            if (result.IsError)
            {
                logger.LogWarning("Token refresh returned an error: {Error} {Description}", result.Error, result.ErrorDescription);
                return null;
            }

            var tokens = new StoredTokens
            {
                Authority = config.Authority,
                Scope = OAuthContract.Scope,
                AccessToken = result.AccessToken,
                // OpenIddict rotates refresh tokens; keep the new one, else retain the old.
                RefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? refreshToken : result.RefreshToken,
                IdToken = string.IsNullOrEmpty(result.IdentityToken) ? GetStored()?.IdToken : result.IdentityToken,
                AccessTokenExpiration = result.AccessTokenExpiration
            };

            store.Save(config.AuthorityHost, tokens);
            logger.LogInformation("Refreshed access token (expires {Expiry:u}).", tokens.AccessTokenExpiration);
            return tokens;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token refresh failed.");
            return null;
        }
    }

    private async Task<StoredTokens> LoginCoreAsync(CancellationToken ct, bool forceInteractive = false)
    {
        if (config.NoInteractiveLogin && !forceInteractive)
            throw new LoginRequiredException(
                "Interactive login is disabled (--no-interactive-login) and no valid tokens are stored. " +
                "Run 'writersblock-mcp login' to sign in, or provide a token via --token / WRITERSBLOCK_MCP_TOKEN.");

        var (httpListener, redirectUri) = BindLoopback();
        using var browser = new LoopbackBrowser(httpListener, redirectUri, logger);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LoginTimeout);

        var oidc = new OidcClient(BuildOptions(redirectUri, browser));

        logger.LogInformation("Starting interactive login against {Authority} (redirect {Redirect}).",
            config.Authority, redirectUri);

        LoginResult result;
        try
        {
            result = await oidc.LoginAsync(new LoginRequest(), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LoginRequiredException(
                $"Login timed out after {LoginTimeout.TotalSeconds:0} seconds. Run 'writersblock-mcp login' and complete the browser sign-in.");
        }

        if (result.IsError)
            throw new LoginRequiredException($"Login failed: {result.Error} {result.ErrorDescription}".Trim());

        var tokens = new StoredTokens
        {
            Authority = config.Authority,
            Scope = OAuthContract.Scope,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            IdToken = result.IdentityToken,
            AccessTokenExpiration = result.AccessTokenExpiration
        };

        store.Save(config.AuthorityHost, tokens);
        logger.LogInformation("Login complete. Tokens stored in {Backend}. Access token expires {Expiry:u}.",
            store.BackendDescription, tokens.AccessTokenExpiration);
        return tokens;
    }

    /// <summary>Builds OidcClient options; <paramref name="browser"/> is null for refresh-only clients.</summary>
    private OidcClientOptions BuildOptions(string redirectUri, IBrowser? browser)
    {
        // Tag every OAuth backchannel call (discovery / token / refresh) with the connector's
        // User-Agent for server-side observability, matching the API dispatch traffic.
        var inner = new HttpClientHandler();
        if (config.Insecure)
            inner.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var options = new OidcClientOptions
        {
            Authority = config.Authority,
            ClientId = OAuthContract.ClientId,
            Scope = OAuthContract.Scope,
            RedirectUri = redirectUri,
            LoadProfile = false,
            Browser = browser,
            BackchannelHandler = new UserAgentHandler(McpConnectorConfig.UserAgent) { InnerHandler = inner }
        };

        // Dev authority uses a self-signed cert on localhost. Relax discovery issuer/endpoint
        // checks so a localhost issuer mismatch doesn't block login (cert validation itself is
        // disabled on the inner handler above).
        if (config.Insecure)
        {
            options.Policy.Discovery.RequireHttps = false;
            options.Policy.Discovery.ValidateIssuerName = false;
            options.Policy.Discovery.ValidateEndpoints = false;
            options.Policy.RequireIdentityTokenSignature = false;
        }

        return options;
    }

    /// <summary>
    /// Binds an <see cref="HttpListener"/> to the first free contract port. The auth server only
    /// accepts these exact redirect URIs, so if all four are taken we cannot log in.
    /// </summary>
    private (HttpListener Listener, string RedirectUri) BindLoopback()
    {
        foreach (var redirect in OAuthContract.RedirectUris)
        {
            var uri = new Uri(redirect);
            if (!IsPortFree(uri.Port))
                continue;

            var listener = new HttpListener();
            // HttpListener prefixes need a trailing slash on the path segment.
            listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/callback/");
            try
            {
                listener.Start();
                return (listener, redirect);
            }
            catch (HttpListenerException ex)
            {
                logger.LogDebug(ex, "Could not bind loopback listener on port {Port}; trying the next.", uri.Port);
                listener.Close();
            }
        }

        throw new LoginRequiredException(
            "All registered loopback callback ports (8171-8174) are in use. Free one of these ports and retry — " +
            "the auth server only accepts these exact redirect URIs.");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// Signals that a valid token could not be obtained without interactive login (either because
/// interaction is disabled, or login failed/timed out). Callers surface the message to the user.
/// </summary>
public sealed class LoginRequiredException(string message) : Exception(message);

/// <summary>
/// Delegating handler that stamps the connector's <c>User-Agent</c> on every OAuth backchannel
/// request (discovery, token, refresh) so this traffic is attributable server-side alongside the
/// API dispatch calls. Only sets the header when the request hasn't already supplied one.
/// </summary>
internal sealed class UserAgentHandler(string userAgent) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.UserAgent.Count == 0)
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return base.SendAsync(request, cancellationToken);
    }
}
