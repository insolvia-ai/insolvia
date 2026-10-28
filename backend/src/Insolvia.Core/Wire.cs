using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Insolvia.Core;

/// <summary>
/// The wire string of every domain enum. Python spelled these as string
/// literal sets (<c>("attorney", "paralegal", "staff")</c>); here each is an
/// enum whose members carry <see cref="JsonStringEnumMemberNameAttribute"/>,
/// so System.Text.Json's string-enum converter writes exactly the Python
/// value and this class reads the same attribute — one spelling, two readers.
/// Declaration order is the Python tuple's order, and the "Must be one of …"
/// messages list members in it.
/// </summary>
public static class Wire
{
    /// <summary>The wire string for <paramref name="value"/>.</summary>
    public static string ToWire<TEnum>(this TEnum value)
        where TEnum : struct, Enum => Cache<TEnum>.ToName.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(value), value, "not a member of " + typeof(TEnum).Name);

    /// <summary>The member whose wire string is exactly <paramref name="wire"/> (ordinal, case-sensitive).</summary>
    public static bool TryParse<TEnum>(string? wire, out TEnum value)
        where TEnum : struct, Enum
    {
        if (wire is not null && Cache<TEnum>.FromName.TryGetValue(wire, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Every member's wire string, in declaration order.</summary>
    public static IReadOnlyList<string> Names<TEnum>()
        where TEnum : struct, Enum => Cache<TEnum>.Ordered;

    /// <summary>Every member, in declaration order.</summary>
    public static IReadOnlyList<TEnum> Members<TEnum>()
        where TEnum : struct, Enum => Cache<TEnum>.Values;

    private static class Cache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly IReadOnlyList<TEnum> Values = Enum.GetValues<TEnum>();

        public static readonly FrozenDictionary<TEnum, string> ToName = Values.ToFrozenDictionary(
            member => member,
            member => typeof(TEnum).GetField(member.ToString(), BindingFlags.Public | BindingFlags.Static)!
                .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{member} has no wire name"));

        public static readonly FrozenDictionary<string, TEnum> FromName = ToName.ToFrozenDictionary(
            pair => pair.Value,
            pair => pair.Key,
            StringComparer.Ordinal);

        public static readonly IReadOnlyList<string> Ordered = [.. Values.Select(member => ToName[member])];
    }
}
