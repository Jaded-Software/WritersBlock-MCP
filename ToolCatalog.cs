using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using WritersBlock.Mcp.Contracts;

namespace WritersBlock.Mcp;

/// <summary>
/// Turns the fetched manifest into the MCP tool list once, and keeps a name → definition
/// lookup so the call handler can dispatch each invocation to the right HTTP request.
/// </summary>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, McpToolDefinition> _byName;

    public ToolCatalog(McpManifestResponse manifest)
    {
        _byName = new Dictionary<string, McpToolDefinition>(StringComparer.Ordinal);
        var tools = new List<Tool>(manifest.Tools.Count);

        foreach (var def in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(def.Name) || !_byName.TryAdd(def.Name, def))
                continue; // skip nameless/duplicate entries defensively

            tools.Add(BuildTool(def));
        }

        Tools = tools;
    }

    public IReadOnlyList<Tool> Tools { get; }

    public bool TryGet(string name, out McpToolDefinition definition) => _byName.TryGetValue(name, out definition!);

    /// <summary>
    /// Normalizes a manifest schema node to a JSON Schema object. JsonSchemaExporter legally
    /// emits the boolean forms (<c>true</c> = accept anything, e.g. for object-typed members),
    /// which MCP clients don't handle as property schemas — coerce those (and anything else
    /// non-object) to the permissive empty object schema instead of crashing.
    /// </summary>
    private static JsonObject AsObjectSchema(JsonNode? schema, Func<JsonObject> fallback) =>
        schema?.DeepClone() is JsonObject obj ? obj : fallback();

    private static Tool BuildTool(McpToolDefinition def)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in def.Parameters)
        {
            var schema = AsObjectSchema(p.Schema, () => new JsonObject { ["type"] = "string" });

            if (!string.IsNullOrWhiteSpace(p.Description) && schema["description"] is null)
                schema["description"] = p.Description;

            properties[p.Name] = schema;
            if (p.Required)
                required.Add(p.Name);
        }

        if (def.BodySchema is not null)
        {
            properties["body"] = AsObjectSchema(def.BodySchema, () => new JsonObject());
            if (def.BodyRequired)
                required.Add("body");
        }

        foreach (var fileParam in def.FormFileParameters)
        {
            properties[fileParam] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "base64-encoded file content"
            };
            required.Add(fileParam);
        }

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            inputSchema["required"] = required;

        return new Tool
        {
            Name = def.Name,
            Description = string.IsNullOrWhiteSpace(def.Description)
                ? $"{def.Method} {def.RouteTemplate}"
                : def.Description,
            InputSchema = JsonSerializer.SerializeToElement(inputSchema),
            Annotations = new ToolAnnotations
            {
                Title = def.EndpointType,
                ReadOnlyHint = def.Annotations.ReadOnlyHint,
                DestructiveHint = def.Annotations.DestructiveHint,
                IdempotentHint = def.Annotations.IdempotentHint
            }
        };
    }
}
