using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// The single seam the manifest client and tool dispatcher use to obtain a bearer token,
/// implementing the resolution order:
/// (1) explicit static token (<c>--token</c> / <c>WRITERSBLOCK_MCP_TOKEN</c>) — returned as-is,
///     no OAuth, no refresh, no 401 recovery beyond a plain error;
/// (2) otherwise the OAuth provider's stored/refreshed/interactively-obtained token.
/// </summary>
public sealed class AuthTokenSource(McpConnectorConfig config, OAuthTokenProvider oauth, ILogger logger)
{
    public bool UsesStaticToken => config.HasStaticToken;

    /// <summary>
    /// Resolves a bearer token for an outbound API call. Throws <see cref="LoginRequiredException"/>
    /// when OAuth is required but unavailable (no stored token + interactive login disabled/failed).
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (config.HasStaticToken)
            return config.Token!;

        return await oauth.GetAccessTokenAsync(ct);
    }

    /// <summary>
    /// Resolves a token WITHOUT ever launching an interactive browser login: the static token if
    /// set, else a cached/silently-refreshed OAuth token, else null. Used at startup so the
    /// manifest fetch defers login to the first tool call.
    /// </summary>
    public async Task<string?> TryGetSilentTokenAsync(CancellationToken ct)
    {
        if (config.HasStaticToken)
            return config.Token;

        return await oauth.TryGetSilentAccessTokenAsync(ct);
    }

    /// <summary>
    /// Called after a 401. For the static-token path there's nothing to recover — returns null.
    /// For OAuth, attempts refresh → clear+relogin and returns a fresh token, or null on failure.
    /// </summary>
    public async Task<string?> RecoverAsync(CancellationToken ct)
    {
        if (config.HasStaticToken)
        {
            logger.LogWarning("A static token (--token / WRITERSBLOCK_MCP_TOKEN) returned 401; it is expired or invalid. " +
                              "Static tokens are not auto-refreshed — supply a fresh one or drop the flag to use browser login.");
            return null;
        }

        return await oauth.RecoverFromUnauthorizedAsync(ct);
    }
}
