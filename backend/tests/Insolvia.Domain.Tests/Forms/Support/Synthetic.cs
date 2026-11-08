using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests.Support;

/// <summary>
/// The goldens' full-coverage synthetic values (test_form_fill.py's
/// <c>synthetic_values</c>): one deterministic, deliberately varied value
/// for every logical field of a release. Synthetic, no real data.
/// </summary>
internal static class Synthetic
{
    public static readonly string[] FormSeries =
    [
        "form/b101", "form/b106ab", "form/b106c", "form/b106d", "form/b106dec", "form/b106ef", "form/b106g",
        "form/b106h", "form/b106i", "form/b106j", "form/b106j2", "form/b106sum", "form/b107", "form/b108",
        "form/b113", "form/b121", "form/b122a1", "form/b122a2", "form/b122c1", "form/b122c2", "form/b2010", "form/b2030",
    ];

    public static FieldValues ValuesFor(FormRelease release)
    {
        var values = new FieldValues();
        for (var i = 0; i < release.Fields.Count; i++)
        {
            var field = release.Fields[i];
            if (field.PdfNames.Count == 1)
            {
                values[field.Id] = One(release, field, field.PdfNames[0], i, 0);
                continue;
            }

            var rows = values.Rows(field.Id);
            for (var j = 0; j < field.PdfNames.Count; j++)
            {
                rows[field.PdfNames[j]] = One(release, field, field.PdfNames[j], i, j);
            }
        }

        return values;
    }

    private static FieldFill One(FormRelease release, FieldSpec field, string name, int i, int j)
    {
        if (release.Boxes.TryGetValue(name, out var box))
        {
            if (field.Type == "checkbox")
            {
                return new CheckFill();
            }

            var value = TextValue(field, null, i, j);
            while (value.Length > 0 && FormFill.HelveticaWidth(value) > box.W)
            {
                value = value[..^1];
            }

            return new TextFill(value);
        }

        var widget = release.Widgets[name];
        return field.Type switch
        {
            "checkbox" => new CheckFill(),
            "radio" => new OptionFill(widget.States[(i + j) % widget.States.Count]),
            _ => new TextFill(TextValue(field, widget.MaxLen, i, j)),
        };
    }

    private static string TextValue(FieldSpec field, int? maxLen, int i, int j)
    {
        string value;
        switch (field.Type)
        {
            case "money":
                return $"1,2{(i + j) % 10}4.56";
            case "integer":
                return $"{((i + j) % 9) + 1}";
            case "date":
                return $"01/{((i + j) % 28) + 1:00}/2026";
            case "year":
                return "2026";
            case "state":
                return "FL";
            case "district":
                return "Middle District of Florida";
            case "ssn_itin":
                value = $"{1234 + j}";
                break;
            case "ein":
                value = $"12345678{j}";
                break;
            case "phone":
                value = $"(305) 555-01{(i + j) % 100:00}";
                break;
            case "email":
                value = $"sample{i}.{j}@example.com";
                break;
            case "signature":
                value = $"Ada Q Lovelace {j + 1}";
                break;
            default:
                value = $"S{i}.{j} {field.Id}";
                value = value.Length > 80 ? value[..80] : value;
                break;
        }

        if (maxLen is { } cap && value.Length > cap)
        {
            value = value[..cap];
        }

        return value;
    }
}
