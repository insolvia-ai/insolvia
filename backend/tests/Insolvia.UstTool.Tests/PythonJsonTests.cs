using System.Text.Json.Nodes;
using Insolvia.UstTool.Tests.Support;

namespace Insolvia.UstTool.Tests;

// The writer must reproduce json.dumps(indent=2, ensure_ascii=False) + "\n",
// or every re-ingest is a whitespace diff over the whole registry.
public sealed class PythonJsonTests
{
    public static TheoryData<string> CommittedUstFiles { get; } =
        [.. Directory.EnumerateFiles(Path.Combine(Recordings.RegistryRoot, "ust"), "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Recordings.RegistryRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(CommittedUstFiles))]
    public void Every_committed_UST_file_round_trips_byte_for_byte(string path)
    {
        var text = Recordings.Committed(path);

        Assert.Equal(text, PythonJson.Dumps(JsonNode.Parse(text)));
    }

    [Fact]
    public void Containers_nest_by_two_spaces_and_empty_ones_stay_on_one_line()
    {
        var node = JsonNode.Parse("""{"a": [], "b": {}, "c": [1, {"d": null, "e": true, "f": false}]}""");

        Assert.Equal(
            "{\n  \"a\": [],\n  \"b\": {},\n  \"c\": [\n    1,\n    {\n      \"d\": null,\n      \"e\": true,\n      \"f\": false\n    }\n  ]\n}\n",
            PythonJson.Dumps(node));
    }

    [Fact]
    public void Only_quotes_backslashes_and_control_characters_are_escaped()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var startOfHeading = ((char)0x01).ToString();
        var node = new JsonObject { ["s"] = $"§ 707 — \"q\" \\ <tag> é {lineSeparator} \n\t{startOfHeading}" };

        Assert.Equal($"{{\n  \"s\": \"§ 707 — \\\"q\\\" \\\\ <tag> é {lineSeparator} \\n\\t\\u0001\"\n}}\n", PythonJson.Dumps(node));
    }
}
