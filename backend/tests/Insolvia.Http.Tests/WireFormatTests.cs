namespace Insolvia.Http.Tests;

/// <summary><see cref="WireFormat"/>'s timestamps: ISO 8601 UTC with a Z.</summary>
public sealed class WireFormatTests
{
    [Fact]
    public void Timestamps_are_iso_8601_with_a_z()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);

        Assert.Equal("2026-01-02T03:04:05.123Z", WireFormat.Millis(at));
        Assert.Equal("2026-01-02T03:04:05.123456Z", WireFormat.Micros(at));
        Assert.Equal("2026-01-02T03:04:05.000000Z", WireFormat.Micros(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)));
    }
}
