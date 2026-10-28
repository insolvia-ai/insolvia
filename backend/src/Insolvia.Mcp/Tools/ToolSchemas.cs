using System.Text.Json.Nodes;

namespace Insolvia.Mcp.Tools;

/// <summary>
/// The JSON Schemas the listing publishes, in the exact shape the Python SDK
/// derived them (pydantic over each tool's signature for the input; over the
/// result TypedDict for the output) — so a harness that cached the Python
/// listing sees the same eight tools. <c>ToolListingTests</c> compares every
/// one with the listing captured from the Python server.
/// </summary>
public static class ToolSchemas
{
    /// <summary>The input schema pydantic derives from a signature.</summary>
    /// <param name="tool">The tool name (the model is <c>&lt;tool&gt;_toolArguments</c>).</param>
    /// <param name="parameters">The parameters, in signature order.</param>
    /// <returns>The schema.</returns>
    public static JsonObject Input(string tool, IReadOnlyList<ToolParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var properties = new JsonObject();
        foreach (var parameter in parameters)
        {
            properties[parameter.Name] = parameter.Kind switch
            {
                ArgumentKind.Text => new JsonObject { ["title"] = parameter.Title, ["type"] = "string" },
                ArgumentKind.OptionalText => Nullable(new JsonObject { ["type"] = "string" }, parameter.Title),
                ArgumentKind.OptionalInteger => Nullable(new JsonObject { ["type"] = "integer" }, parameter.Title),
                ArgumentKind.OptionalTextList => Nullable(new JsonObject { ["items"] = new JsonObject { ["type"] = "string" }, ["type"] = "array" }, parameter.Title),
                _ => new JsonObject { ["items"] = OpenObject(), ["title"] = parameter.Title, ["type"] = "array" },
            };
        }

        var schema = new JsonObject { ["properties"] = properties };
        var required = parameters.Where(parameter => parameter.Required).Select(parameter => (JsonNode)parameter.Name).ToArray();
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required);
        }

        schema["title"] = $"{tool}_toolArguments";
        schema["type"] = "object";
        return schema;
    }

    /// <summary><c>WhoamiResult</c>.</summary>
    public static JsonObject WhoamiResult => Parse("""
        {"$defs": {"FirmSummary": {"properties": {"id": {"title": "Id", "type": "string"}, "name": {"title": "Name", "type": "string"}}, "required": ["id", "name"], "title": "FirmSummary", "type": "object"}},
         "properties": {"firm": {"anyOf": [{"$ref": "#/$defs/FirmSummary"}, {"type": "null"}]}, "displayName": {"title": "Displayname", "type": "string"}, "isAdmin": {"title": "Isadmin", "type": "boolean"}, "accessAllCases": {"title": "Accessallcases", "type": "boolean"}, "permissions": {"additionalProperties": {"type": "string"}, "title": "Permissions", "type": "object"}},
         "required": ["firm", "displayName", "isAdmin", "accessAllCases", "permissions"], "title": "WhoamiResult", "type": "object"}
        """);

    /// <summary><c>ListCasesResult</c>.</summary>
    public static JsonObject ListCasesResult => Page("cases", "Cases", "ListCasesResult");

    /// <summary><c>GetCaseResult</c>.</summary>
    public static JsonObject GetCaseResult => Parse("""
        {"properties": {"case": {"additionalProperties": true, "title": "Case", "type": "object"}, "recordCounts": {"additionalProperties": {"type": "integer"}, "title": "Recordcounts", "type": "object"}},
         "required": ["case", "recordCounts"], "title": "GetCaseResult", "type": "object"}
        """);

    /// <summary><c>ListCaseRecordsResult</c>.</summary>
    public static JsonObject ListCaseRecordsResult => Page("records", "Records", "ListCaseRecordsResult");

    /// <summary><c>GetCaseRecordResult</c>.</summary>
    public static JsonObject GetCaseRecordResult => Parse("""
        {"properties": {"record": {"additionalProperties": true, "title": "Record", "type": "object"}}, "required": ["record"], "title": "GetCaseRecordResult", "type": "object"}
        """);

    /// <summary><c>ProposeCaseRecordsResult</c>.</summary>
    public static JsonObject ProposeCaseRecordsResult => Parse("""
        {"$defs": {"ProposedCandidate": {"properties": {"candidateId": {"title": "Candidateid", "type": "string"}, "entityType": {"title": "Entitytype", "type": "string"}, "status": {"title": "Status", "type": "string"}}, "required": ["candidateId", "entityType", "status"], "title": "ProposedCandidate", "type": "object"}},
         "properties": {"candidates": {"items": {"$ref": "#/$defs/ProposedCandidate"}, "title": "Candidates", "type": "array"}}, "required": ["candidates"], "title": "ProposeCaseRecordsResult", "type": "object"}
        """);

    /// <summary><c>CheckProposalsResult</c>.</summary>
    public static JsonObject CheckProposalsResult => Page("candidates", "Candidates", "CheckProposalsResult");

    /// <summary><c>WithdrawProposalResult</c>.</summary>
    public static JsonObject WithdrawProposalResult => Parse("""
        {"properties": {"candidateId": {"title": "Candidateid", "type": "string"}, "status": {"title": "Status", "type": "string"}}, "required": ["candidateId", "status"], "title": "WithdrawProposalResult", "type": "object"}
        """);

    // `{<items>: list[dict[str, Any]], nextCursor: NotRequired[str]}`.
    private static JsonObject Page(string items, string itemsTitle, string title) => new()
    {
        ["properties"] = new JsonObject
        {
            [items] = new JsonObject { ["items"] = OpenObject(), ["title"] = itemsTitle, ["type"] = "array" },
            ["nextCursor"] = new JsonObject { ["title"] = "Nextcursor", ["type"] = "string" },
        },
        ["required"] = new JsonArray(items),
        ["title"] = title,
        ["type"] = "object",
    };

    private static JsonObject OpenObject() => new() { ["additionalProperties"] = true, ["type"] = "object" };

    private static JsonObject Nullable(JsonObject type, string title) => new()
    {
        ["anyOf"] = new JsonArray(type, new JsonObject { ["type"] = "null" }),
        ["default"] = null,
        ["title"] = title,
    };

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
