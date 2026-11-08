using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Insolvia.Data.Conventions;

/// <summary>
/// Refuses a <see cref="DateTime"/> property anywhere in the model. An instant
/// is a <see cref="DateTimeOffset"/> (<c>timestamptz</c>); a court date is a
/// <see cref="DateOnly"/> (<c>date</c>) and is never converted to UTC. A
/// <see cref="DateTime"/> says neither, and Npgsql would store it by its
/// <see cref="DateTimeKind"/> — a bug that only shows on a machine whose
/// clock is not UTC.
/// </summary>
internal sealed class NoDateTimeConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entity in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entity.GetDeclaredProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type == typeof(DateTime))
                {
                    throw new InvalidOperationException(
                        $"{entity.DisplayName()}.{property.Name} is a DateTime. Use DateTimeOffset (timestamptz) for an instant, or DateOnly (date) for a court date.");
                }
            }
        }
    }
}
