using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WritersBlock.Mcp.OAuth;
using WritersBlock.Mcp.Contracts;

namespace WritersBlock.Mcp;

/// <summary>
/// Fetches the tool manifest from <c>GET api/mcp/manifest</c>, unwraps the WritersBlock
/// response envelope loosely (no dependency on the envelope type), and persists the raw
/// JSON + ETag under LocalApplicationData so a subsequent launch can send If-None-Match
/// and fall back to the cached copy when the API is unreachable.
///
/// The manifest endpoint requires auth, so the fetch resolves a bearer token via the same
/// resolution order as tool calls (static token → stored OAuth → interactive login). To keep
/// the MCP server startup quiet and non-blocking, the manifest load never triggers an
/// interactive browser login on its own: when no token is available without interaction it
/// serves the cached manifest (or an empty one) and defers login to the first tool call.
/// </summary>
public sealed class McpManifestClient(HttpClient http, McpConnectorConfig config, AuthTokenSource auth, ILogger<McpManifestClient> logger)
{
    private const string ManifestPath = "api/mcp/manifest";

    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WritersBlock.Mcp");

    private string CacheJsonPath => Path.Combine(_cacheDir, "manifest.json");
    private string CacheETagPath => Path.Combine(_cacheDir, "manifest.etag");

    /// <summary>
    /// Resolves the manifest for this session. Network first (honoring the cached ETag),
    /// then the cached copy on any failure. Returns an empty manifest (zero tools) rather
    /// than throwing so the MCP server always starts and surfaces an actionable error.
    /// </summary>
    public async Task<McpManifestResponse> LoadAsync(CancellationToken ct)
    {
        // Resolve a token without forcing a browser at startup: a login is deferred to the first
        // tool call so the connector always boots fast and stdout stays protocol-clean.
        var token = await auth.TryGetSilentTokenAsync(ct);
        if (token is null)
        {
            var cachedNoAuth = TryReadCachedManifest();
            if (cachedNoAuth is not null)
            {
                logger.LogWarning(
                    "Not signed in yet. Using the cached manifest ({Count} tools); the first tool call will trigger browser login.",
                    cachedNoAuth.Tools.Count);
                return cachedNoAuth;
            }

            logger.LogWarning(
                "Not signed in and no cached manifest. Starting with zero tools; the first tool call will trigger browser login, " +
                "after which the tool catalog loads on the next launch. Or run 'writersblock-mcp login' now.");
            return new McpManifestResponse();
        }

        try
        {
            return await FetchAsync(token, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var cached = TryReadCachedManifest();
            if (cached is not null)
            {
                logger.LogWarning(ex,
                    "Manifest fetch from {Url} failed; using the cached copy ({Count} tools).",
                    new Uri(config.ApiBaseUri, ManifestPath), cached.Tools.Count);
                return cached;
            }

            logger.LogError(ex,
                "Manifest fetch from {Url} failed and no cache is available. Starting with zero tools. " +
                "Verify the API is running and reachable and that you are signed in.",
                new Uri(config.ApiBaseUri, ManifestPath));
            return new McpManifestResponse();
        }
    }

    private async Task<McpManifestResponse> FetchAsync(string token, CancellationToken ct)
    {
        var response = await SendAsync(token, ct);

        // One 401 recovery attempt (refresh / re-login) then a single retry.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            var recovered = await auth.RecoverAsync(ct);
            if (recovered is null)
                throw new InvalidOperationException(
                    "Manifest request returned 401 Unauthorized and the token could not be refreshed. " +
                    "Run 'writersblock-mcp login' to sign in.");
            response = await SendAsync(recovered, ct);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var cached = TryReadCachedManifest()
                             ?? throw new InvalidOperationException("API returned 304 Not Modified but no cached manifest exists.");
                logger.LogInformation("Manifest unchanged (304); using cache ({Count} tools).", cached.Tools.Count);
                return cached;
            }

            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(
                    "Manifest request returned 401 Unauthorized even after re-authentication.");

            response.EnsureSuccessStatusCode();

            var manifest = ParseEnvelope(content);
            var responseETag = response.Headers.ETag?.ToString();
            TryWriteCache(content, responseETag);

            logger.LogInformation("Loaded manifest: {Count} tools (apiVersion={Version}, excluded={Excluded}).",
                manifest.Tools.Count, manifest.ApiVersion, manifest.ExcludedCount);
            return manifest;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(config.ApiBaseUri, ManifestPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var cachedETag = TryReadCachedETag();
        if (!string.IsNullOrEmpty(cachedETag) && File.Exists(CacheJsonPath) &&
            EntityTagHeaderValue.TryParse(cachedETag, out var etag))
        {
            request.Headers.IfNoneMatch.Add(etag);
        }

        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>
    /// Loosely unwraps <c>{ isSuccess, message, data, errors, statusCode }</c> and deserializes
    /// <c>data</c> into <see cref="McpManifestResponse"/>. JsonNode schema properties round-trip verbatim.
    /// </summary>
    private static McpManifestResponse ParseEnvelope(string json)
    {
        var root = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException("Manifest response body was empty or not valid JSON.");

        var isSuccess = root["isSuccess"]?.GetValue<bool>() ?? false;
        if (!isSuccess)
        {
            var message = root["message"]?.GetValue<string>() ?? "Manifest request was not successful.";
            throw new InvalidOperationException($"Manifest endpoint returned isSuccess=false: {message}");
        }

        var data = root["data"];
        if (data is null)
            throw new InvalidOperationException("Manifest envelope contained no 'data'.");

        return data.Deserialize<McpManifestResponse>(DeserializeOptions)
               ?? throw new InvalidOperationException("Manifest 'data' could not be deserialized.");
    }

    private McpManifestResponse? TryReadCachedManifest()
    {
        try
        {
            if (!File.Exists(CacheJsonPath)) return null;
            return ParseEnvelope(File.ReadAllText(CacheJsonPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read cached manifest at {Path}.", CacheJsonPath);
            return null;
        }
    }

    private string? TryReadCachedETag()
    {
        try
        {
            return File.Exists(CacheETagPath) ? File.ReadAllText(CacheETagPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private void TryWriteCache(string json, string? etag)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(CacheJsonPath, json);
            if (!string.IsNullOrEmpty(etag))
                File.WriteAllText(CacheETagPath, etag);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist manifest cache under {Dir}.", _cacheDir);
        }
    }
}
