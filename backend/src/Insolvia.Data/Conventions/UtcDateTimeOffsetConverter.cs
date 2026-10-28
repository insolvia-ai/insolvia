using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Insolvia.Data.Conventions;

/// <summary>
/// Writes every <see cref="DateTimeOffset"/> as its UTC equivalent — the same
/// instant — because <c>timestamptz</c> keeps the instant and drops the offset,
/// and Npgsql refuses a non-zero offset rather than convert silently.
/// </summary>
internal sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    public UtcDateTimeOffsetConverter()
        : base(value => value.ToUniversalTime(), value => value)
    {
    }
}
