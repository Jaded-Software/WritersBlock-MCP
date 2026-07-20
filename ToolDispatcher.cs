using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using WritersBlock.Mcp.OAuth;
using WritersBlock.Mcp.Contracts;

namespace WritersBlock.Mcp;

/// <summary>
/// Dispatches an MCP tool call to the corresponding authenticated HTTPS request and maps
/// the response back to an MCP <see cref="CallToolResponse"/>. API-level failures (validation,
/// 4xx/5xx, 401) surface as <c>isError</c> results with model-readable text — never as MCP
/// protocol errors — so an agent can read and react to them.
/// </summary>
public sealed class ToolDispatcher(HttpClient http, McpConnectorConfig config, ToolCatalog catalog, AuthTokenSource auth, ILogger<ToolDispatcher> logger)
{
    private const long MaxBinaryBytes = 10 * 1024 * 1024; // 10 MB cap on binary passthrough

    private static readonly JsonSerializerOptions IndentedWeb = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async ValueTask<CallToolResponse> DispatchAsync(CallToolRequestParams request, CancellationToken ct)
    {
        if (!catalog.TryGet(request.Name, out var def))
            return Error($"Unknown tool '{request.Name}'. It is not present in the current manifest.");

        var args = request.Arguments ?? new Dictionary<string, JsonElement>();

        // Validate arg-to-request mapping once up front (cheap and deterministic) so a bad-args
        // error doesn't waste an interactive login.
        try
        {
            using var probe = BuildRequest(def, args);
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }

        // (1) Resolve a token — this may trigger a browser login on the first call.
        string token;
        try
        {
            token = await auth.GetTokenAsync(ct);
        }
        catch (LoginRequiredException ex)
        {
            return Error(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve an access token for tool {Tool}.", def.Name);
            return Error($"Could not obtain an access token: {ex.Message}");
        }

        try
        {
            var (response, unauthorized) = await SendAsync(def, args, token, ct);

            // (4) 401 handling: refresh → retry once → if still 401, clear + re-login → retry once.
            if (unauthorized)
            {
                response.Dispose();
                var recovered = await auth.RecoverAsync(ct);
                if (recovered is null)
                    return Error(
                        "Request returned 401 Unauthorized and re-authentication was unavailable. " +
                        "Run 'writersblock-mcp login' to sign in (or supply a fresh --token / WRITERSBLOCK_MCP_TOKEN).");

                (response, unauthorized) = await SendAsync(def, args, recovered, ct);
                if (unauthorized)
                {
                    response.Dispose();
                    return Error(
                        "Request still returned 401 Unauthorized after re-authentication. " +
                        "Your account may lack access to this resource, or the token was revoked.");
                }
            }

            using (response)
                return await MapResponseAsync(def, response, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transport failure dispatching tool {Tool}.", def.Name);
            return Error($"Request to the WritersBlock API failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a fresh request (messages can't be reused across sends) with the given bearer token,
    /// dispatches it, and reports whether the response was a 401 so the caller can retry.
    /// </summary>
    private async Task<(HttpResponseMessage Response, bool Unauthorized)> SendAsync(
        McpToolDefinition def, IReadOnlyDictionary<string, JsonElement> args, string token, CancellationToken ct)
    {
        var message = BuildRequest(def, args);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
        message.Dispose();
        return (response, response.StatusCode == HttpStatusCode.Unauthorized);
    }

    private HttpRequestMessage BuildRequest(McpToolDefinition def, IReadOnlyDictionary<string, JsonElement> args)
    {
        var route = SubstituteRoute(def, args);
        var query = new List<string>();
        var headers = new List<(string Name, string Value)>();

        foreach (var p in def.Parameters)
        {
            if (p.Location.Equals("route", StringComparison.OrdinalIgnoreCase))
                continue; // handled during substitution

            if (!args.TryGetValue(p.Name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (p.Required)
                    throw new ArgumentException($"Missing required {p.Location} parameter '{p.Name}'.");
                continue;
            }

            var raw = ScalarToString(value);
            if (p.Location.Equals("query", StringComparison.OrdinalIgnoreCase))
                query.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(raw)}");
            else if (p.Location.Equals("header", StringComparison.OrdinalIgnoreCase))
                headers.Add((p.Name, raw));
        }

        var relative = query.Count > 0 ? $"{route}?{string.Join('&', query)}" : route;
        var uri = new Uri(config.ApiBaseUri, relative);

        var message = new HttpRequestMessage(new HttpMethod(def.Method.ToUpperInvariant()), uri)
        {
            Content = BuildContent(def, args)
        };
        foreach (var (name, val) in headers)
            message.Headers.TryAddWithoutValidation(name, val);

        return message;
    }

    private static string SubstituteRoute(McpToolDefinition def, IReadOnlyDictionary<string, JsonElement> args)
    {
        var routeParams = def.Parameters
            .Where(p => p.Location.Equals("route", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        return TokenPattern.Replace(def.RouteTemplate, match =>
        {
            // Strip any inline route constraint, e.g. {projectId:int} → projectId.
            var token = match.Groups[1].Value;
            var name = token.Split(':', 2)[0].TrimStart('*');

            if (!args.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new ArgumentException($"Missing required route parameter '{name}' for '{def.Name}'.");

            _ = routeParams; // parameters list is advisory; the template is authoritative
            return Uri.EscapeDataString(ScalarToString(value));
        });
    }

    private static HttpContent? BuildContent(McpToolDefinition def, IReadOnlyDictionary<string, JsonElement> args)
    {
        var hasBody = def.BodySchema is not null && args.TryGetValue("body", out var b) &&
                      b.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

        var providedFiles = def.FormFileParameters
            .Where(f => args.TryGetValue(f, out var v) && v.ValueKind == JsonValueKind.String)
            .ToList();

        if (def.BodyRequired && !hasBody && def.BodySchema is not null)
            throw new ArgumentException($"Missing required 'body' argument for '{def.Name}'.");

        // Multipart when any form-file is present; fold body/scalar fields in as form fields too.
        if (def.FormFileParameters.Count > 0 && providedFiles.Count > 0)
        {
            var multipart = new MultipartFormDataContent();
            foreach (var fileParam in providedFiles)
            {
                var base64 = args[fileParam].GetString()!;
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    multipart.Dispose();
                    throw new ArgumentException($"Form-file parameter '{fileParam}' is not valid base64.");
                }

                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                multipart.Add(fileContent, fileParam, fileParam);
            }

            if (hasBody)
                multipart.Add(new StringContent(args["body"].GetRawText(), Encoding.UTF8, "application/json"), "body");

            return multipart;
        }

        if (hasBody)
            return BuildBodyContent(def, args["body"]);

        return null;
    }

    /// <summary>
    /// Builds the request body content for a single-body tool. A [FromBody] tool (no
    /// <see cref="McpToolDefinition.BodyContentType"/>) or a raw JSON body is sent verbatim as JSON;
    /// a raw body with a non-JSON content type (declared via [McpRawBody]) treats the "body" argument
    /// as a base64 string and sends the decoded bytes with that content type.
    /// </summary>
    private static HttpContent BuildBodyContent(McpToolDefinition def, JsonElement body)
    {
        var contentType = def.BodyContentType;
        var isJson = string.IsNullOrWhiteSpace(contentType)
                     || contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        if (isJson)
            return new StringContent(body.GetRawText(), Encoding.UTF8, contentType ?? "application/json");

        // Raw binary body: the tool input is a base64 string whose decoded bytes are the request body.
        if (body.ValueKind != JsonValueKind.String)
            throw new ArgumentException(
                $"Tool '{def.Name}' expects 'body' to be a base64-encoded string for content type '{contentType}'.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(body.GetString()!);
        }
        catch (FormatException)
        {
            throw new ArgumentException($"The 'body' argument for '{def.Name}' is not valid base64.");
        }

        if (bytes.LongLength > MaxBinaryBytes)
            throw new ArgumentException(
                $"The 'body' for '{def.Name}' is {bytes.LongLength} bytes, exceeding the {MaxBinaryBytes}-byte (10 MB) cap.");

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
        return content;
    }

    private async ValueTask<CallToolResponse> MapResponseAsync(McpToolDefinition def, HttpResponseMessage response, CancellationToken ct)
    {
        // 401 is handled with refresh/re-login retry in DispatchAsync; this is a defensive fallback.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return Error(
                "Request returned 401 Unauthorized and could not be re-authenticated. Run 'writersblock-mcp login' to sign in.");

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var isJson = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

        if (!isJson && mediaType.Length > 0)
            return await MapBinaryAsync(response, mediaType, ct);

        var text = await response.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            return response.IsSuccessStatusCode
                ? Ok($"{def.Method} {def.RouteTemplate} succeeded ({(int)response.StatusCode}) with an empty response.")
                : Error($"Request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}) and an empty body.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return response.IsSuccessStatusCode ? Ok(text) : Error(text);
        }

        // Recognize the WritersBlock envelope by its shape.
        if (root is JsonObject obj && obj.ContainsKey("isSuccess"))
        {
            var isSuccess = obj["isSuccess"]?.GetValue<bool>() ?? false;
            if (isSuccess && response.IsSuccessStatusCode)
            {
                var data = obj["data"];
                if (data is null)
                    return Ok(obj["message"]?.GetValue<string>() ?? "Success.");
                return Ok(data.ToJsonString(IndentedWeb));
            }

            var message = obj["message"]?.GetValue<string>() ?? $"Request failed ({(int)response.StatusCode}).";
            var errors = (obj["errors"] as JsonArray)?
                .Select(e => e?.GetValue<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!)
                .ToList();
            var detail = errors is { Count: > 0 } ? $"{message}\n{string.Join('\n', errors)}" : message;
            return Error(detail);
        }

        // Non-enveloped JSON (or a bare JSON null): pass through on success, treat as error otherwise.
        var rendered = root?.ToJsonString(IndentedWeb) ?? "null";
        return response.IsSuccessStatusCode
            ? Ok(rendered)
            : Error($"Request failed ({(int)response.StatusCode}): {rendered}");
    }

    private static async ValueTask<CallToolResponse> MapBinaryAsync(HttpResponseMessage response, string mediaType, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            return Error($"Binary request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).");

        var declared = response.Content.Headers.ContentLength;
        if (declared is > MaxBinaryBytes)
            return Error($"Response is {declared} bytes, exceeding the {MaxBinaryBytes}-byte (10 MB) binary cap. Use a narrower request or download outside the connector.");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength > MaxBinaryBytes)
            return Error($"Response is {bytes.LongLength} bytes, exceeding the {MaxBinaryBytes}-byte (10 MB) binary cap.");

        var body = Convert.ToBase64String(bytes);
        return Ok($"[binary content-type={mediaType}, {bytes.Length} bytes, base64]\n{body}");
    }

    private static string ScalarToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    private static CallToolResponse Ok(string text) => new()
    {
        IsError = false,
        Content = [new Content { Type = "text", Text = text }]
    };

    private static CallToolResponse Error(string text) => new()
    {
        IsError = true,
        Content = [new Content { Type = "text", Text = text }]
    };

    private static readonly System.Text.RegularExpressions.Regex TokenPattern =
        new(@"\{([^}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);
}
