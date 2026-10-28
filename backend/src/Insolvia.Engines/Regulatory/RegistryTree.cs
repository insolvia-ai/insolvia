using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Insolvia.Engines.Regulatory;

/// <summary>
/// A registry root as a file tree — the shape <c>importlib.resources</c> gave
/// the Python loaders. The shipped tree is this assembly's embedded
/// resources (<see cref="Shipped"/>); a test builds one from a dictionary,
/// the way the Python suites wrote release directories into <c>tmp_path</c>.
/// Paths are relative and '/'-separated (<c>ust/irs-local-standards/2026-07-15/dataset.json</c>).
/// </summary>
public sealed class RegistryTree
{
    private const string ResourcePrefix = "Insolvia.Engines.Regulatory/";

    private static readonly Lazy<RegistryTree> ShippedTree = new(() => new RegistryTree(EmbeddedFiles()));

    private readonly Dictionary<string, string> _files;
    private readonly HashSet<string> _directories;

    /// <summary>A tree over these files; <paramref name="emptyDirectories"/> adds directories that hold none.</summary>
    public RegistryTree(IReadOnlyDictionary<string, string> files, IEnumerable<string>? emptyDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files.ToDictionary(pair => Normalise(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        _directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _files.Keys)
        {
            AddParents(path);
        }

        foreach (var directory in emptyDirectories ?? [])
        {
            var normalised = Normalise(directory).TrimEnd('/');
            _directories.Add(normalised);
            AddParents(normalised);
        }
    }

    /// <summary>The registry shipped inside this assembly (ADR 0014).</summary>
    public static RegistryTree Shipped => ShippedTree.Value;

    /// <summary>Every file, by path.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>The directories directly under <paramref name="directory"/>, by name, ordinal order — Python's <c>sorted(iterdir())</c> filtered to directories.</summary>
    public IReadOnlyList<string> ChildDirectories(string directory)
    {
        var prefix = Normalise(directory).TrimEnd('/') + "/";
        return [.. _directories
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && !path[prefix.Length..].Contains('/', StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Whether the directory exists.</summary>
    public bool HasDirectory(string directory) => _directories.Contains(Normalise(directory).TrimEnd('/'));

    /// <summary>The file's text, or null when there is no such file.</summary>
    public string? Read(string path) => _files.TryGetValue(Normalise(path), out var text) ? text : null;

    /// <summary>The embedded regulatory files, path to text.</summary>
    internal static Dictionary<string, string> EmbeddedFiles()
    {
        var assembly = typeof(RegistryTree).Assembly;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[Normalise(name[ResourcePrefix.Length..])] = reader.ReadToEnd();
        }

        return files;
    }

    /// <summary>The embedded regulatory files, path to raw bytes — what the drift test hashes.</summary>
    internal static Dictionary<string, byte[]> EmbeddedBytes()
    {
        var assembly = typeof(RegistryTree).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[Normalise(name[ResourcePrefix.Length..])] = copy.ToArray();
        }

        return files;
    }

    private static string Normalise(string path) => path.Replace('\\', '/');

    private void AddParents(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            _directories.Add(path[..slash]);
        }
    }
}

/// <summary>
/// The validation helpers every registry loader shares — each Python
/// module carried its own private copy (<c>_fail</c>, <c>_str_field</c>,
/// <c>_sources</c>, ...); the messages are theirs, word for word.
/// </summary>
internal static partial class ReleaseParsing
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})(?:\+(\d+))?$")]
    internal static partial Regex DirectoryName();

    [GeneratedRegex(@"^[1-9]\d*\.\d{2}$")]
    internal static partial Regex MoneyPattern();

    internal static MalformedReleaseException Fail(string where, string problem) =>
        new($"malformed release {where}: {problem}");

    internal static string StrField(JsonObject data, string key, string where, Func<string, string, MalformedReleaseException>? fail = null)
    {
        var value = Core.Json.AsString(data[key]);
        if (value is null || value.Trim().Length == 0)
        {
            throw (fail ?? Fail)(where, $"{key} missing or empty");
        }

        return value;
    }

    internal static DateOnly? DateOrNone(JsonNode? value, string where, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        var text = Core.Json.AsString(value)
            ?? throw Fail(where, $"{fieldName} {Py.Repr(value)} is not a date string");
        return Py.ParseIso(text)
            ?? throw Fail(where, $"{fieldName} {Py.Repr(text)}: Invalid isoformat string: {Py.Repr(text)}");
    }

    /// <summary>The exemptions/dollar-amounts/UST flavour of <c>_sources</c>.</summary>
    internal static IReadOnlyList<Source> Sources(JsonNode? value, string where)
    {
        if (value is not JsonArray list || list.Count == 0)
        {
            throw Fail(where, "sources missing or empty");
        }

        var parsed = new List<Source>();
        foreach (var raw in list)
        {
            if (raw is not JsonObject item)
            {
                throw Fail(where, "source is not an object");
            }

            var accessed = DateOrNone(item["accessed"], where, "source.accessed")
                ?? throw Fail(where, "source.accessed missing");
            parsed.Add(new Source(StrField(item, "title", where), StrField(item, "url", where), accessed));
        }

        return parsed;
    }

    /// <summary>The deadline-rules/legal-holidays flavour of <c>_sources</c>.</summary>
    internal static IReadOnlyList<Source> CodeSources(JsonNode? value, string where, string emptyMessage)
    {
        if (value is not JsonArray list || list.Count == 0)
        {
            throw Fail(where, emptyMessage);
        }

        var parsed = new List<Source>();
        foreach (var raw in list)
        {
            if (raw is not JsonObject item)
            {
                throw Fail(where, "a source is not an object");
            }

            var accessedText = StrField(item, "accessed", where);
            var accessed = Py.ParseIso(accessedText)
                ?? throw Fail(where, $"source.accessed: Invalid isoformat string: {Py.Repr(accessedText)}");
            parsed.Add(new Source(StrField(item, "title", where), StrField(item, "url", where), accessed));
        }

        return parsed;
    }

    /// <summary>The exemptions/dollar-amounts/UST flavour of <c>_load_json</c>.</summary>
    internal static JsonObject LoadJson(RegistryTree root, string path, string where)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var text = root.Read(path) ?? throw Fail(where, $"{name}: [Errno 2] No such file or directory: {Py.Repr(name)}");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException error)
        {
            throw Fail(where, $"{name}: {error.Message}");
        }

        return parsed as JsonObject ?? throw Fail(where, $"{name} is not a JSON object");
    }

    /// <summary>The deadline-rules/legal-holidays flavour of <c>_load_json</c>.</summary>
    internal static JsonObject LoadCodeJson(RegistryTree root, string path, string where)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var text = root.Read(path) ?? throw Fail(where, $"{name} missing");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException error)
        {
            throw Fail(where, $"{name} is not valid JSON: {error.Message}");
        }

        return parsed as JsonObject ?? throw Fail(where, $"{name} is not a JSON object");
    }

    /// <summary>The <c>&lt;effective_date&gt;[+&lt;sequence&gt;]</c> directory name, or the loader's refusal.</summary>
    internal static (DateOnly Effective, int Sequence) ParseDirectoryName(string name, string where, Func<string, string, MalformedReleaseException>? fail = null)
    {
        var match = DirectoryName().Match(name);
        var effective = match.Success ? Py.ParseIso(match.Groups[1].Value) : null;
        if (effective is null)
        {
            throw (fail ?? Fail)(where, "directory name is not <effective_date>[+<sequence>]");
        }

        var sequence = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        if (sequence < 1)
        {
            throw (fail ?? Fail)(where, "sequence must be >= 1");
        }

        return (effective.Value, sequence);
    }

    /// <summary>The manifest's identity checks every series repeats.</summary>
    internal static JsonObject ManifestSource(JsonObject manifest, string seriesId, DateOnly effective, int sequence, string where, Func<string, string, MalformedReleaseException>? fail = null)
    {
        fail ??= Fail;
        if (Core.Json.AsString(manifest["series_id"]) != seriesId)
        {
            throw fail(where, $"manifest series_id {Py.Repr(manifest["series_id"])}");
        }

        if (Core.Json.AsString(manifest["effective_date"]) != Py.Iso(effective))
        {
            throw fail(where, "manifest effective_date disagrees with the path");
        }

        if (!Core.Json.TryGetWholeNumber(manifest["sequence"], out var declared) || declared != sequence)
        {
            throw fail(where, "manifest sequence disagrees with the path");
        }

        return manifest["source"] as JsonObject ?? throw fail(where, "manifest source missing");
    }

    /// <summary>A two-decimal money string or the loader's refusal (<c>_money</c>).</summary>
    internal static string Money(JsonNode? value, string where, string fieldName)
    {
        var text = Core.Json.AsString(value);
        if (text is null || !MoneyPattern().IsMatch(text))
        {
            throw Fail(where, $"{fieldName} {Py.Repr(value)} is not a two-decimal money string");
        }

        return text;
    }

    /// <summary>A two-decimal money string, absent allowed.</summary>
    internal static string? MoneyOrNone(JsonNode? value, string where, string fieldName) =>
        value is null ? null : Money(value, where, fieldName);

    /// <summary><c>Verification(raw)</c>, refusing an unknown tier with Python's enum message.</summary>
    internal static Verification ParseVerification(JsonNode? value, string where)
    {
        if (Core.Wire.TryParse<Verification>(Core.Json.AsString(value), out var verification))
        {
            return verification;
        }

        throw Fail(where, $"{Py.Repr(value)} is not a valid Verification");
    }

    /// <summary>A string member, or empty when absent (Python's <c>raw.get("notes", "")</c>).</summary>
    internal static string Notes(JsonObject raw, string where, string message = "notes must be a string")
    {
        if (!raw.TryGetPropertyValue("notes", out var value))
        {
            return string.Empty;
        }

        return Core.Json.AsString(value) ?? throw Fail(where, message);
    }

    /// <summary>The sha256, absent allowed, a string when present.</summary>
    internal static string? Sha256(JsonObject source, string where)
    {
        var value = source["sha256"];
        if (value is null)
        {
            return null;
        }

        return Core.Json.AsString(value) ?? throw Fail(where, "source.sha256 must be a string or null");
    }

    /// <summary>The manifest's notes, required (<c>manifest notes missing</c>).</summary>
    internal static string ManifestNotes(JsonObject manifest, string where)
    {
        var notes = Core.Json.AsString(manifest["notes"]);
        if (notes is null || notes.Trim().Length == 0)
        {
            throw Fail(where, "manifest notes missing — say what this release is");
        }

        return notes;
    }
}
