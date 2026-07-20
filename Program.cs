using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WritersBlock.Mcp;
using WritersBlock.Mcp.OAuth;

var config = McpConnectorConfig.Parse(args);

// CLI verbs (login / logout / status) run instead of the MCP server and are free to use stdout.
// The MCP-server path (no verb) keeps stdout protocol-clean; all its logging goes to stderr.
if (args.Length > 0 && CliVerbs.IsVerb(args[0]))
{
    using var verbLoggerFactory = LoggerFactory.Create(b =>
    {
        b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        b.SetMinimumLevel(LogLevel.Information);
    });
    var verbLogger = verbLoggerFactory.CreateLogger("WritersBlock.Mcp");

    using var verbCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; verbCts.Cancel(); };

    return await CliVerbs.RunAsync(args[0], config, verbLogger, verbCts.Token);
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport — every log line must go to stderr, or it corrupts JSON-RPC.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(config);

// OAuth: OS-native token storage + native-app login provider + the token-resolution seam.
builder.Services.AddSingleton<ITokenStore>(sp =>
    TokenStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>().CreateLogger("WritersBlock.Mcp.TokenStore")));
builder.Services.AddSingleton<OAuthTokenProvider>(sp => new OAuthTokenProvider(
    sp.GetRequiredService<McpConnectorConfig>(),
    sp.GetRequiredService<ITokenStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("WritersBlock.Mcp.OAuth")));
builder.Services.AddSingleton<AuthTokenSource>(sp => new AuthTokenSource(
    sp.GetRequiredService<McpConnectorConfig>(),
    sp.GetRequiredService<OAuthTokenProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("WritersBlock.Mcp.Auth")));

builder.Services.AddHttpClient<McpManifestClient>(ConfigureHttp).ConfigurePrimaryHttpMessageHandler(() => CreateHandler(config));
builder.Services.AddHttpClient<ToolDispatcher>(ConfigureHttp).ConfigurePrimaryHttpMessageHandler(() => CreateHandler(config));

builder.Services.AddSingleton<ToolCatalog>(sp =>
{
    var client = sp.GetRequiredService<McpManifestClient>();
    var manifest = client.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
    return new ToolCatalog(manifest);
});

builder.Services
    .AddMcpServer(mcp =>
    {
        mcp.ServerInfo = new Implementation { Name = "writersblock", Version = McpConnectorConfig.Version };
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((_, _) =>
    {
        var catalog = SharedServices.Provider!.GetRequiredService<ToolCatalog>();
        return ValueTask.FromResult(new ListToolsResult { Tools = [.. catalog.Tools] });
    })
    .WithCallToolHandler((ctx, ct) =>
    {
        if (ctx.Params is null)
            return ValueTask.FromResult(new CallToolResponse
            {
                IsError = true,
                Content = [new Content { Type = "text", Text = "Missing tool call parameters." }]
            });

        var dispatcher = SharedServices.Provider!.GetRequiredService<ToolDispatcher>();
        return dispatcher.DispatchAsync(ctx.Params, ct);
    });

var host = builder.Build();
SharedServices.Provider = host.Services;

// Force the catalog to resolve now so manifest load + startup diagnostics happen before the transport spins up.
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WritersBlock.Mcp");
var toolCount = host.Services.GetRequiredService<ToolCatalog>().Tools.Count;
var authMode = config.HasStaticToken ? "static-token" : "oauth";
startupLogger.LogInformation(
    "WritersBlock MCP connector ready: {Count} tools, api={Api}, authority={Authority}, authMode={AuthMode}, insecure={Insecure}, interactiveLogin={Interactive}.",
    toolCount, config.ApiUrl, config.Authority, authMode, config.Insecure, !config.NoInteractiveLogin);

await host.RunAsync();
return 0;

static void ConfigureHttp(HttpClient client)
{
    client.Timeout = TimeSpan.FromSeconds(100);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(McpConnectorConfig.UserAgent);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
}

static HttpMessageHandler CreateHandler(McpConnectorConfig config)
{
    var handler = new HttpClientHandler();
    if (config.Insecure)
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    return handler;
}

// Bridge for the SDK's static handler delegates to reach the built service provider.
internal static class SharedServices
{
    public static IServiceProvider? Provider { get; set; }
}
