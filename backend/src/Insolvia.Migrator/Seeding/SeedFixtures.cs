using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Insolvia.Migrator.Seeding;

/// <summary>
/// Reading the language-neutral fixture files: <c>seeds/&lt;env&gt;.json</c>
/// and <c>seeds/fixtures/&lt;version&gt;/{cases,manifest}.json</c> (seed.py's
/// <c>load_fixture</c> and <c>_fixture_version</c>).
/// </summary>
/// <remarks>
/// <b><c>${VAR}</c> is not a convenience.</b> This repo is public, so a
/// fixture carries no credential: passwords are <c>${E2E_TEST_USER_PASSWORD}</c>
/// and arrive from the environment at load time. Expansion FAILS on an unset
/// or empty variable rather than substituting "": a CI secret that is missing
/// renders as the empty string, and an account whose password became "" would
/// be worse than a refusal.
/// </remarks>
public static partial class SeedFixtures
{
    /// <summary>Where fixture versions live, relative to the environment fixture's folder.</summary>
    public const string FixturesDirectory = "fixtures";

    /// <summary>The JSON object at <paramref name="path"/>, every string value <c>${VAR}</c>-expanded from <paramref name="environment"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="environment">Reads one variable; null when unset.</param>
    /// <returns>The object.</returns>
    public static JsonObject Load(string path, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(environment);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SeedRefusedException($"no fixture at {path}", error);
        }

        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = true });
        }
        catch (JsonException error)
        {
            throw new SeedRefusedException($"{path} is not valid JSON: {error.Message}", error);
        }

        return Expanded(raw, environment) as JsonObject ?? throw new SeedRefusedException($"{path} must be a JSON object");
    }

    /// <summary><c>cases.json</c> and <c>manifest.json</c> for one committed fixture version beside <paramref name="environmentFixture"/>.</summary>
    /// <param name="environmentFixture">The <c>seeds/&lt;env&gt;.json</c> path.</param>
    /// <param name="version">The version, e.g. <c>v4</c>.</param>
    /// <returns>The two documents.</returns>
    public static (JsonObject Cases, JsonObject Manifest) Version(string environmentFixture, string version)
    {
        ArgumentNullException.ThrowIfNull(environmentFixture);
        if (!VersionName().IsMatch(version ?? string.Empty))
        {
            throw new SeedRefusedException($"fixture version '{version}' must look like v1");
        }

        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(environmentFixture))!, FixturesDirectory, version!);
        return Folder(folder);
    }

    /// <summary>A version folder's two documents, each refused unless it declares the folder's own name.</summary>
    /// <param name="folder">The version's folder.</param>
    /// <returns>The two documents.</returns>
    public static (JsonObject Cases, JsonObject Manifest) Folder(string folder)
    {
        var version = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        var cases = Load(Path.Combine(folder, "cases.json"), _ => null);
        var manifest = Load(Path.Combine(folder, "manifest.json"), _ => null);
        if (SeedJson.Text(cases["version"]) != version || SeedJson.Text(manifest["version"]) != version)
        {
            throw new SeedRefusedException($"{folder} does not declare itself as version {version}");
        }

        return (cases, manifest);
    }

    private static JsonNode? Expanded(JsonNode? node, Func<string, string?> environment) => node switch
    {
        JsonObject obj => new JsonObject([.. obj.Select(pair => KeyValuePair.Create(pair.Key, Expanded(pair.Value, environment)))]),
        JsonArray array => new JsonArray([.. array.Select(item => Expanded(item, environment))]),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(Expand(value.GetValue<string>(), environment)),
        _ => node?.DeepClone(),
    };

    private static string Expand(string value, Func<string, string?> environment) =>
        Variable().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            var found = environment(name);
            return string.IsNullOrEmpty(found)
                ? throw new SeedRefusedException($"fixture references ${{{name}}}, which is unset or empty. It has no default on purpose — this repo is public.")
                : found;
        });

    [GeneratedRegex(@"\$\{([A-Z_][A-Z0-9_]*)\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^v[0-9]+\z")]
    private static partial Regex VersionName();
}

/// <summary>Reading fixture JSON the way Python's <c>dict.get</c> did.</summary>
internal static class SeedJson
{
    /// <summary>The node as a string when it is one; null otherwise.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Python's <c>str(x or "")</c> for a fixture field: the string, the number's text, or "".</summary>
    public static string Str(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() is JsonValueKind.False or JsonValueKind.Null => string.Empty,
        _ => node.ToJsonString(),
    };

    /// <summary>The node's members when it is an array, empty when absent or null (<c>x or []</c>).</summary>
    public static IReadOnlyList<JsonNode?> List(JsonNode? node) => node is JsonArray array ? [.. array] : [];

    /// <summary>A body for a parser: a deep copy of an object, or an empty object for anything else.</summary>
    public static JsonObject Body(JsonNode? node) => node is JsonObject obj ? (JsonObject)obj.DeepClone() : [];
}
