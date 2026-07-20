using System.Text.Json.Nodes;

namespace WritersBlock.Mcp.Contracts;

/// <summary>
/// The MCP tool manifest served by <c>GET api/mcp/manifest</c>: one entry per exposed
/// API action. The catalog is generated from ApiExplorer at API startup, so every
/// routable controller action appears here automatically unless it carries
/// <c>[McpExcluded]</c>. Consumed by the WritersBlock.Mcp desktop connector, which
/// registers one MCP tool per definition and dispatches calls as plain HTTPS requests.
///
/// This is a manually-synced copy of the contract defined in the WritersBlock API's
/// private repo (WritersBlock.Shared/Mcp/McpManifestResponse.cs) — keep the two in sync
/// when the manifest shape changes.
/// </summary>
public class McpManifestResponse
{
    /// <summary>Informational API assembly version the manifest was generated from.</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>UTC timestamp of catalog generation (API process start).</summary>
    public DateTime GeneratedDate { get; set; }

    /// <summary>Number of actions excluded via [McpExcluded] (diagnostic).</summary>
    public int ExcludedCount { get; set; }

    public List<McpToolDefinition> Tools { get; set; } = [];
}

/// <summary>One API action exposed as an MCP tool.</summary>
public class McpToolDefinition
{
    /// <summary>
    /// Unique tool name: the action's EndpointTypes member name snake_cased
    /// (e.g. ProjectGetAll → project_get_all); actions without [ApiEndpoint] fall back
    /// to controller_action snake_case, deduplicated with a verb suffix.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tool description sourced from the action's XML doc &lt;summary&gt;.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>HTTP method: GET, POST, PUT, PATCH, or DELETE.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Relative route template, e.g. "api/project/get/{projectId}".</summary>
    public string RouteTemplate { get; set; } = string.Empty;

    /// <summary>Route, query, and header parameters (excludes the request body).</summary>
    public List<McpToolParameter> Parameters { get; set; } = [];

    /// <summary>JSON Schema for the request body DTO, when the action binds a body.</summary>
    public JsonNode? BodySchema { get; set; }

    /// <summary>Whether the body is required when <see cref="BodySchema"/> is present.</summary>
    public bool BodyRequired { get; set; }

    /// <summary>
    /// True when the body is read from the raw request stream (declared via <c>[McpRawBody]</c>)
    /// rather than a <c>[FromBody]</c> model. The connector sends the <c>body</c> input as the raw
    /// request body using <see cref="BodyContentType"/> instead of a model-bound JSON payload.
    /// </summary>
    public bool RawBody { get; set; }

    /// <summary>
    /// Content type for a raw body (set only when <see cref="RawBody"/> is true). An
    /// <c>application/json</c> type means the <c>body</c> input is sent verbatim as the JSON body;
    /// any other type means the input is a base64 string whose decoded bytes form the body. Null for
    /// <c>[FromBody]</c> tools (which are always sent as <c>application/json</c>).
    /// </summary>
    public string? BodyContentType { get; set; }

    /// <summary>
    /// Names of IFormFile parameters. The connector surfaces each as a base64 string
    /// input and dispatches the call as multipart/form-data.
    /// </summary>
    public List<string> FormFileParameters { get; set; } = [];

    public McpToolAnnotations Annotations { get; set; } = new();

    /// <summary>True when the action (or its controller) requires the AdminOnly policy.</summary>
    public bool AdminOnly { get; set; }

    /// <summary>The EndpointTypes member name backing this tool, when present (diagnostic).</summary>
    public string? EndpointType { get; set; }
}

/// <summary>A non-body input to the tool: where it goes on the HTTP request.</summary>
public class McpToolParameter
{
    public string Name { get; set; } = string.Empty;

    /// <summary>One of: "route", "query", "header".</summary>
    public string Location { get; set; } = string.Empty;

    public bool Required { get; set; }

    /// <summary>JSON Schema for the parameter value.</summary>
    public JsonNode? Schema { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// MCP tool behavior hints derived from the HTTP verb:
/// GET → read-only; DELETE → destructive; PUT/PATCH → idempotent.
/// </summary>
public class McpToolAnnotations
{
    public bool ReadOnlyHint { get; set; }
    public bool DestructiveHint { get; set; }
    public bool IdempotentHint { get; set; }
}
