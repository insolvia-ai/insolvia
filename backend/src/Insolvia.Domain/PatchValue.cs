namespace Insolvia.Core;

/// <summary>
/// A PATCH member that may be ABSENT (leave unchanged), present with null
/// (clear it), or present with a value — Python's <c>UNSET</c> sentinel.
/// <c>default</c> is absent.
/// </summary>
/// <typeparam name="T">The member's type, nullable when it may be cleared.</typeparam>
public readonly record struct PatchValue<T>
{
    private PatchValue(T value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>Whether the body named the member.</summary>
    public bool IsSet { get; }

    /// <summary>The value it named (null to clear); meaningless unless <see cref="IsSet"/>.</summary>
    public T Value { get; }

    /// <summary>A member the body named.</summary>
    internal static PatchValue<T> Set(T value) => new(value);

    /// <inheritdoc />
    public override string ToString() => IsSet ? $"Set({Value})" : "Unset";
}

/// <summary>Building <see cref="PatchValue{T}"/> values.</summary>
public static class PatchValue
{
    /// <summary>A member the body named, with <paramref name="value"/>.</summary>
    public static PatchValue<T> Of<T>(T value) => PatchValue<T>.Set(value);
}
