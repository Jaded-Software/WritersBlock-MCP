using System.Reflection;
using WritersBlock.Mcp.OAuth;

namespace WritersBlock.Mcp;

/// <summary>
/// Runtime configuration for the connector, resolved from environment variables with
/// CLI flags taking precedence.
///
/// Auth resolution order for outbound API calls:
/// (1) an explicit static token (<c>--token</c> / <c>WRITERSBLOCK_MCP_TOKEN</c>) — the
///     escape hatch, skips OAuth entirely;
/// (2) stored OAuth tokens (obtained via the interactive browser login), refreshed silently;
/// (3) interactive login on demand, unless <c>--no-interactive-login</c> is set.
/// </summary>
public sealed class McpConnectorConfig
{
    /// <summary>
    /// Default API base URL — the production WritersBlock deployment. Dev users override this
    /// explicitly with <c>--api-url https://localhost:6001</c>.
    /// </summary>
    public const string DefaultApiUrl = "https://writersblock.jadedsoftware.com";

    /// <summary>
    /// Connector product version, read from the assembly's informational version (set from
    /// <c>&lt;InformationalVersion&gt;</c> / <c>&lt;Version&gt;</c> in the csproj). Feeds the MCP
    /// <c>serverInfo.version</c> handshake and the outbound <c>User-Agent</c> so both track the
    /// build without a hardcoded string.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>Outbound telemetry tag applied to every HTTP request the connector makes.</summary>
    public static string UserAgent { get; } = $"WritersBlock.Mcp/{Version}";

    public string ApiUrl { get; init; } = DefaultApiUrl;

    /// <summary>OIDC authority (issuer / discovery base) for the interactive login flow.</summary>
    public string Authority { get; init; } = OAuthContract.DefaultAuthority;

    /// <summary>Static bearer-token override — the OAuth escape hatch. When set, OAuth is skipped.</summary>
    public string? Token { get; init; }

    /// <summary>Disable TLS certificate validation for API, authority discovery, and token calls (dev self-signed certs).</summary>
    public bool Insecure { get; init; }

    /// <summary>When true, never pop a browser; missing/expired tokens surface as an actionable error instead.</summary>
    public bool NoInteractiveLogin { get; init; }

    public bool HasStaticToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>Base URI with a single trailing slash so relative route templates compose cleanly.</summary>
    public Uri ApiBaseUri => new(ApiUrl.TrimEnd('/') + "/");

    /// <summary>
    /// Host portion of the authority, used to namespace stored tokens so a dev login and a prod
    /// login never collide (e.g. <c>auth.jadedsoftware.com</c> vs <c>localhost</c>).
    /// </summary>
    public string AuthorityHost
    {
        get
        {
            if (Uri.TryCreate(Authority, UriKind.Absolute, out var uri))
                return uri.Port is 80 or 443 or -1 ? uri.Host : $"{uri.Host}_{uri.Port}";
            return Authority;
        }
    }

    public static McpConnectorConfig Parse(string[] args)
    {
        var apiUrl = Environment.GetEnvironmentVariable("WRITERSBLOCK_API_URL");
        var authority = Environment.GetEnvironmentVariable("WRITERSBLOCK_AUTHORITY");
        var token = Environment.GetEnvironmentVariable("WRITERSBLOCK_MCP_TOKEN");
        var insecure = false;
        var noInteractive = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--api-url" when i + 1 < args.Length:
                    apiUrl = args[++i];
                    break;
                case "--authority" when i + 1 < args.Length:
                    authority = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--insecure":
                    insecure = true;
                    break;
                case "--no-interactive-login":
                    noInteractive = true;
                    break;
                default:
                    if (args[i].StartsWith("--api-url=", StringComparison.Ordinal))
                        apiUrl = args[i]["--api-url=".Length..];
                    else if (args[i].StartsWith("--authority=", StringComparison.Ordinal))
                        authority = args[i]["--authority=".Length..];
                    else if (args[i].StartsWith("--token=", StringComparison.Ordinal))
                        token = args[i]["--token=".Length..];
                    break;
            }
        }

        return new McpConnectorConfig
        {
            ApiUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl.Trim(),
            Authority = string.IsNullOrWhiteSpace(authority) ? OAuthContract.DefaultAuthority : authority.Trim(),
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            Insecure = insecure,
            NoInteractiveLogin = noInteractive
        };
    }

    /// <summary>
    /// Reads the assembly's <see cref="AssemblyInformationalVersionAttribute"/> (set from the
    /// csproj), stripping any source-revision suffix the SDK appends (e.g. <c>1.0.0+abc123</c>).
    /// Falls back to the assembly version, then to <c>"0.0.0"</c>.
    /// </summary>
    private static string ResolveVersion()
    {
        var asm = typeof(McpConnectorConfig).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
