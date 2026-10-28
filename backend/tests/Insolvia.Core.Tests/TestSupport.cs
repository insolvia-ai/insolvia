using System.Text.Json.Nodes;
using Insolvia.Core.Ports;

namespace Insolvia.Core.Tests;

/// <summary>Helpers shared by the translated suites. Every identifier is obviously fake: this repo is public.</summary>
internal static class Ids
{
    public const string Firm = "00000000-0000-4000-8000-00000000f18a";
    public const string OtherFirm = "00000000-0000-4000-8000-00000000f18b";
    public const string Alice = "00000000-0000-4000-8000-00000000a11c";
    public const string Bob = "00000000-0000-4000-8000-00000000b0b0";
}

internal static class Body
{
    /// <summary>A request body from a JSON literal — the C# spelling of the Python tests' dict literals.</summary>
    public static JsonObject Of(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Any JSON value from a literal.</summary>
    public static JsonNode? Value(string json) => JsonNode.Parse(json);
}

/// <summary>
/// A court registry holding just what the firm tests name: the Middle
/// District of Florida with its divisions. The real registry is 18.7's.
/// </summary>
internal sealed class FakeCourtRegistry : ICourtRegistry
{
    private static readonly Dictionary<string, string[]> Districts = new(StringComparer.Ordinal)
    {
        ["flmb"] = ["fort_myers", "jacksonville", "orlando", "tampa"],
    };

    public static FakeCourtRegistry Instance { get; } = new();

    public bool HasDistrict(string courtCode) => Districts.ContainsKey(courtCode);

    public bool HasDivision(string courtCode, string divisionCode) =>
        Districts.TryGetValue(courtCode, out var divisions) && divisions.Contains(divisionCode, StringComparer.Ordinal);
}

/// <summary>A clock that stands still unless told to move.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
