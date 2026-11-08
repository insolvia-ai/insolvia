using System.Text.Json.Nodes;
using Insolvia.Engines.Projections;

namespace Insolvia.Forms.Tests.Support;

/// <summary>FieldValues from (and the fills of) the canonical JSON both languages write — <see cref="FieldEntry.ToJson"/>'s inverse.</summary>
internal static class Values
{
    public static FieldValues FromJson(JsonObject json)
    {
        var values = new FieldValues();
        foreach (var (fieldId, node) in json)
        {
            var entry = node!.AsObject();
            if (entry["rows"] is JsonObject rows)
            {
                var target = values.Rows(fieldId);
                foreach (var (name, fill) in rows)
                {
                    target[name] = Fill(fill!.AsObject());
                }
            }
            else
            {
                values[fieldId] = Fill(entry);
            }
        }

        return values;
    }

    public static FieldFill Fill(JsonObject json)
    {
        if (json["text"] is { } text)
        {
            return new TextFill(text.GetValue<string>());
        }

        if (json["check"] is { } check)
        {
            return new CheckFill(check.GetValue<bool>());
        }

        if (json["option"] is { } option)
        {
            return new OptionFill(option.GetValue<string>());
        }

        return new WidgetStatesFill(
            [.. json["states"]!.AsArray().Select(s => s!.GetValue<string>())],
            [.. json["indexes"]!.AsArray().Select(i => i!.GetValue<int>())]);
    }

    public static FieldValues Of(params (string FieldId, FieldEntry Entry)[] entries)
    {
        var values = new FieldValues();
        foreach (var (fieldId, entry) in entries)
        {
            values[fieldId] = entry;
        }

        return values;
    }
}
