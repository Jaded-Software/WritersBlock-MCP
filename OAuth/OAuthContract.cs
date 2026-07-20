namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// The fixed native-app OAuth contract for the <c>writersblock-mcp</c> public client.
///
/// EVERY value here is an exact-match contract with the auth server's OpenIddict client
/// registration (<c>OpenIddict:Clients:writersblock-mcp</c>). Do NOT change any of these
/// in isolation — they must be updated in lockstep with the auth-server config, or login
/// fails with an <c>invalid_client</c> / <c>redirect_uri_mismatch</c> / <c>invalid_scope</c>.
/// </summary>
public static class OAuthContract
{
    /// <summary>Public PKCE client id registered on the auth server. No secret.</summary>
    public const string ClientId = "writersblock-mcp";

    /// <summary>
    /// Space-delimited scopes, mirroring the BFF confidential client:
    /// <c>openid profile email roles</c> (identity), <c>offline_access</c> (refresh token),
    /// <c>api</c> (WritersBlock API access). Must match the scopes granted to the client
    /// on the auth server.
    /// </summary>
    public const string Scope = "openid profile email roles offline_access api";

    /// <summary>
    /// Loopback redirect URIs, in the order we try to bind. The server registers these four
    /// EXACT URIs (scheme, host <c>127.0.0.1</c>, port, and <c>/callback</c> path) as an
    /// exact-match allow-list — no other port or host will be accepted. At login time we bind
    /// the first free port from this ordered list and use its URI as the redirect_uri.
    ///
    /// KEEP IN SYNC with the auth server's OpenIddict:Clients:writersblock-mcp RedirectUris.
    /// </summary>
    public static readonly IReadOnlyList<string> RedirectUris =
    [
        "http://127.0.0.1:8171/callback",
        "http://127.0.0.1:8172/callback",
        "http://127.0.0.1:8173/callback",
        "http://127.0.0.1:8174/callback"
    ];

    /// <summary>Production authority (OIDC issuer / discovery base).</summary>
    public const string DefaultAuthority = "https://auth.jadedsoftware.com";

    /// <summary>Local dev authority (self-signed TLS; pair with <c>--insecure</c>).</summary>
    public const string DevAuthority = "https://localhost:6010";
}
