using Insolvia.Core;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Insolvia.Data.Storage;

/// <summary>
/// Stores a domain enum as its wire string (<see cref="Wire"/>) — the value
/// the contract carries, so a row reads the same in <c>psql</c> as on the wire. A
/// stored string this version does not know fails the read loudly: an enum
/// column is not a place a newer version may add values silently (the
/// permission map, which is, is read defensively in <see cref="StoredJson"/>).
/// </summary>
internal sealed class WireEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(value => value.ToWire(), stored => WireEnums.Parse<TEnum>(stored))
    where TEnum : struct, Enum;

/// <summary>Wire-string parsing for stored enum values.</summary>
internal static class WireEnums
{
    /// <summary>The member <paramref name="stored"/> names; throws on a value this version does not know.</summary>
    public static TEnum Parse<TEnum>(string stored)
        where TEnum : struct, Enum => Wire.TryParse<TEnum>(stored, out var value)
            ? value
            : throw new InvalidOperationException($"stored {typeof(TEnum).Name} '{stored}' is not one this version knows");

    /// <summary>A <c>CHECK</c> body restricting <paramref name="column"/> to <typeparamref name="TEnum"/>'s wire strings.</summary>
    public static string CheckIn<TEnum>(string column)
        where TEnum : struct, Enum => $"{column} IN ({string.Join(", ", Wire.Names<TEnum>().Select(name => $"'{name}'"))})";
}
