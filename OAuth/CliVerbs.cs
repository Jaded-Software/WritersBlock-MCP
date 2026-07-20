using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WritersBlock.Mcp.OAuth;

/// <summary>
/// Human-facing CLI verbs (login / logout / status). These run instead of the MCP stdio server
/// when the first CLI argument is a verb, and are free to write to stdout (they are not the MCP
/// transport). The MCP-server path never invokes these, so stdout stays protocol-clean there.
/// </summary>
public static class CliVerbs
{
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "login", "logout", "status" };

    public static bool IsVerb(string arg) => Names.Contains(arg);

    public static async Task<int> RunAsync(string verb, McpConnectorConfig config, ILogger logger, CancellationToken ct)
    {
        var store = TokenStoreFactory.Create(logger);
        var provider = new OAuthTokenProvider(config, store, logger);

        return verb.ToLowerInvariant() switch
        {
            "login" => await LoginAsync(config, provider, ct),
            "logout" => Logout(config, provider),
            "status" => Status(config, provider),
            _ => Unknown(verb)
        };
    }

    private static async Task<int> LoginAsync(McpConnectorConfig config, OAuthTokenProvider provider, CancellationToken ct)
    {
        Console.WriteLine($"Signing in to WritersBlock via {config.Authority} …");
        Console.WriteLine("A browser window will open. Complete the sign-in, then return here.");

        try
        {
            var tokens = await provider.LoginInteractiveAsync(ct);
            var user = DescribeUser(tokens.IdToken);
            Console.WriteLine();
            Console.WriteLine("Login complete.");
            if (user is not null)
                Console.WriteLine($"  Signed in as : {user}");
            Console.WriteLine($"  Authority    : {config.Authority}");
            Console.WriteLine($"  Token expires: {tokens.AccessTokenExpiration.ToLocalTime():u}");
            Console.WriteLine($"  Stored in    : {provider.Store.BackendDescription}");
            return 0;
        }
        catch (LoginRequiredException ex)
        {
            Console.Error.WriteLine($"Login failed: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Login cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Login failed: {ex.Message}");
            return 1;
        }
    }

    private static int Logout(McpConnectorConfig config, OAuthTokenProvider provider)
    {
        var had = provider.GetStored() is not null;
        provider.Logout();
        Console.WriteLine(had
            ? $"Signed out of {config.Authority}. Stored tokens for this authority were deleted."
            : $"No stored tokens for {config.Authority}; nothing to do.");
        return 0;
    }

    private static int Status(McpConnectorConfig config, OAuthTokenProvider provider)
    {
        Console.WriteLine("WritersBlock MCP connector — auth status");
        Console.WriteLine($"  Version         : {McpConnectorConfig.Version}");
        Console.WriteLine($"  Authority       : {config.Authority}");
        Console.WriteLine($"  API base URL    : {config.ApiUrl}");
        Console.WriteLine($"  Storage backend : {provider.Store.BackendDescription}");

        if (config.HasStaticToken)
        {
            Console.WriteLine("  Auth mode       : static token (--token / WRITERSBLOCK_MCP_TOKEN) — OAuth bypassed");
            return 0;
        }

        var stored = provider.GetStored();
        if (stored is null)
        {
            Console.WriteLine("  Auth mode       : OAuth (browser login)");
            Console.WriteLine("  Signed in       : no — run 'writersblock-mcp login' or trigger a tool call");
            return 0;
        }

        var user = DescribeUser(stored.IdToken);
        var expired = stored.IsExpiredOrExpiring(TimeSpan.Zero);
        Console.WriteLine("  Auth mode       : OAuth (browser login)");
        Console.WriteLine($"  Signed in       : yes{(user is not null ? $" as {user}" : "")}");
        Console.WriteLine($"  Access token    : {(expired ? "EXPIRED" : "valid")} (expires {stored.AccessTokenExpiration.ToLocalTime():u})");
        Console.WriteLine($"  Refresh token   : {(stored.HasRefreshToken ? "present (silent refresh available)" : "absent")}");
        Console.WriteLine($"  Scopes          : {stored.Scope}");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown command '{verb}'. Valid commands: login, logout, status.");
        return 2;
    }

    /// <summary>
    /// Best-effort human label for the signed-in user, read from the id_token payload. Decodes the
    /// JWT payload segment directly (no signature validation — this is display-only) to avoid a
    /// dependency on a JWT-handler package.
    /// </summary>
    private static string? DescribeUser(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2)
                return null;

            using var doc = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            var root = doc.RootElement;

            string? Claim(params string[] names) =>
                names.Select(n => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                     .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var email = Claim("email");
            var name = Claim("name", "preferred_username", "given_name");
            var sub = Claim("sub");

            if (name is not null && email is not null) return $"{name} <{email}>";
            return name ?? email ?? sub;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
