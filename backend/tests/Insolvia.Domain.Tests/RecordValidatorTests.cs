using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Insolvia.Domain.Fields;

namespace Insolvia.Domain.Tests;

public sealed class RecordValidatorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Fact]
    public void Every_failure_is_named_by_its_json_path()
    {
        var errors = RecordValidator.Validate(
            new Sample(new string('x', 11), new SampleAddress("1234567890123"), [new SampleStep("a", 0), new SampleStep("b", 3)]),
            Web);

        Assert.Equal(["address.postalCode", "name", "steps[0].startMonth"], errors.Keys.Order());
    }

    [Fact]
    public void A_rule_across_fields_runs_once_the_fields_pass()
    {
        Assert.Equal(
            "Each step starts in its own month.",
            RecordValidator.Validate(new Sample("ok", null, [new SampleStep("a", 2), new SampleStep("b", 2)]), Web)["steps"]);
        Assert.DoesNotContain("steps", RecordValidator.Validate(new Sample("ok", null, [new SampleStep("a", 0), new SampleStep("b", 0)]), Web).Keys);
    }

    [Fact]
    public void A_valid_record_passes() =>
        RecordValidator.ThrowIfInvalid(new Sample("ok", new SampleAddress("12345"), []), Web);

    [Fact]
    public void Throwing_names_the_fields() =>
        Assert.Contains("name", Assert.Throws<FieldValidationException>(() => RecordValidator.ThrowIfInvalid(new Sample(new string('x', 11), null, null), Web)).Fields.Keys);
}

public sealed record SampleAddress([property: MaxLength(12)] string? PostalCode);

public sealed record SampleStep(string Id, [property: Range(1, 60)] int? StartMonth);

public sealed record Sample([property: MaxLength(10)] string? Name, SampleAddress? Address, IReadOnlyList<SampleStep>? Steps) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Steps is { } steps && steps.Select(step => step.StartMonth).Distinct().Count() != steps.Count)
        {
            yield return new ValidationResult("Each step starts in its own month.", [nameof(Steps)]);
        }
    }
}
