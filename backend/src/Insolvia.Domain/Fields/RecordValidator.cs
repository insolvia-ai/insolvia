using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

namespace Insolvia.Domain.Fields;

/// <summary>
/// Checks a record against the DataAnnotations attributes on its properties
/// (<c>[MaxLength]</c>, <c>[Range]</c>, …), and the records and lists of
/// records inside it, then its own <see cref="IValidatableObject"/> rule when
/// its fields passed. Every failure is keyed by its JSON path under
/// the serializer's names (<c>body.address.postalCode</c>,
/// <c>steps[0].startMonth</c>). The one validator for a request (the hosts'
/// endpoint filter) and for anything else that arrives as a record (an
/// extraction candidate, a seed).
/// </summary>
public static class RecordValidator
{
    /// <summary>The failures, by path; empty when <paramref name="value"/> is valid.</summary>
    /// <param name="value">The record.</param>
    /// <param name="options">The serializer settings whose names the paths use.</param>
    /// <returns>Path to message.</returns>
    public static IReadOnlyDictionary<string, string> Validate(object value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(value, string.Empty, options, errors);
        return errors;
    }

    /// <summary>Refuses <paramref name="value"/> with every failure named.</summary>
    /// <param name="value">The record.</param>
    /// <param name="options">The serializer settings whose names the paths use.</param>
    /// <exception cref="FieldValidationException">A field failed.</exception>
    public static void ThrowIfInvalid(object value, JsonSerializerOptions options)
    {
        var errors = Validate(value, options);
        if (errors.Count > 0)
        {
            throw new FieldValidationException(errors);
        }
    }

    private static void Walk(object value, string path, JsonSerializerOptions options, Dictionary<string, string> errors)
    {
        var before = errors.Count;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in options.GetTypeInfo(value.GetType()).Properties)
        {
            if (member.AttributeProvider is not PropertyInfo property || member.Get is null)
            {
                continue;
            }

            names[property.Name] = member.Name;
            var memberValue = member.Get(value);
            var memberPath = Join(path, member.Name);
            var context = new ValidationContext(value) { MemberName = property.Name, DisplayName = member.Name };
            var failure = property.GetCustomAttributes<ValidationAttribute>()
                .Select(attribute => attribute.GetValidationResult(memberValue, context))
                .FirstOrDefault(result => result != ValidationResult.Success);
            if (failure is not null)
            {
                // A list rule may name the row it refused ("[1].id").
                var row = failure.MemberNames.FirstOrDefault(name => name.StartsWith('['));
                errors[memberPath + row] = failure.ErrorMessage ?? "Invalid.";
                continue;
            }

            switch (memberValue)
            {
                case null or string or IDictionary:
                    break;
                case IEnumerable items:
                    var index = 0;
                    foreach (var item in items)
                    {
                        if (item is not null && IsRecord(item.GetType()))
                        {
                            Walk(item, $"{memberPath}[{index}]", options, errors);
                        }

                        index++;
                    }

                    break;
                default:
                    if (IsRecord(memberValue.GetType()))
                    {
                        Walk(memberValue, memberPath, options, errors);
                    }

                    break;
            }
        }

        // A rule across fields reads them, so it runs only once they passed.
        if (errors.Count == before && value is IValidatableObject self)
        {
            foreach (var result in self.Validate(new ValidationContext(value)))
            {
                foreach (var member in result.MemberNames.DefaultIfEmpty(string.Empty))
                {
                    errors[Join(path, names.GetValueOrDefault(member, member))] = result.ErrorMessage ?? "Invalid.";
                }
            }
        }
    }

    private static bool IsRecord(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("Insolvia", StringComparison.Ordinal) == true;

    private static string Join(string path, string name) =>
        path.Length == 0 ? name : name.Length == 0 ? path : $"{path}.{name}";
}
