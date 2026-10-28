using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Insolvia.Forms.Pdf;

/// <summary>
/// Reading PDFsharp's object model the way pypdf's dictionary accessors read
/// it: references resolved, numbers and strings unwrapped, inherited keys
/// walked up the <c>/Parent</c> chain.
/// </summary>
internal static class PdfObjects
{
    /// <summary>The object an item stands for (a reference resolved).</summary>
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>The item as a dictionary, or null.</summary>
    public static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;

    /// <summary>The item as an array, or null.</summary>
    public static PdfArray? Array(PdfItem? item) => Resolve(item) as PdfArray;

    /// <summary>Whether the item is a PDF number.</summary>
    public static bool IsNumber(PdfItem? item) => Resolve(item) is PdfInteger or PdfReal or PdfLongInteger or PdfIntegerObject or PdfRealObject or PdfLongIntegerObject;

    /// <summary>Whether the item is a PDF integer (pypdf's <c>NumberObject</c>, an <c>int</c>).</summary>
    public static bool IsInteger(PdfItem? item) => Resolve(item) is PdfInteger or PdfLongInteger or PdfIntegerObject or PdfLongIntegerObject;

    /// <summary>The item as a number; throws when it is not one.</summary>
    public static double Num(PdfItem? item) => Resolve(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfLongInteger l => l.Value,
        PdfIntegerObject i => i.Value,
        PdfRealObject r => r.Value,
        PdfLongIntegerObject l => l.Value,
        _ => throw new InvalidOperationException($"not a number: {item}"),
    };

    /// <summary>The item as a Python-typed number.</summary>
    public static PyNum PyNumber(PdfItem? item) => IsInteger(item) ? PyNum.Int((long)Num(item)) : PyNum.Float(Num(item));

    /// <summary>A name or string value of a dictionary key, or null.</summary>
    public static string? Str(PdfDictionary dict, string key) => Str(dict.Elements[key]);

    /// <summary>A name or string item's value, or null.</summary>
    public static string? Str(PdfItem? item) => Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfStringObject s => s.Value,
        PdfName n => n.Value,
        PdfNameObject n => n.Value,
        _ => null,
    };

    /// <summary>The widget annotations of a page, in /Annots order.</summary>
    public static IEnumerable<PdfDictionary> Widgets(PdfPage page)
    {
        if (Array(page.Elements["/Annots"]) is not { } annots)
        {
            yield break;
        }

        foreach (var item in annots.Elements)
        {
            if (Dict(item) is { } annotation && Str(annotation, "/Subtype") == "/Widget")
            {
                yield return annotation;
            }
        }
    }

    /// <summary>The fully qualified field name: every /T up the /Parent chain, joined with dots.</summary>
    public static string? QualifiedName(PdfDictionary node)
    {
        var parts = new List<string>();
        for (var current = node; current is not null; current = Dict(current.Elements["/Parent"]))
        {
            if (Str(current, "/T") is { } title)
            {
                parts.Add(title);
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>The field node owning a widget: the widget itself when it carries /FT and /T, its parent otherwise (pypdf's resolution).</summary>
    public static PdfDictionary? FieldOf(PdfDictionary widget) =>
        widget.Elements.ContainsKey("/FT") && widget.Elements.ContainsKey("/T")
            ? widget
            : Dict(widget.Elements["/Parent"]);

    /// <summary>A key's value on the node or its nearest ancestor (pypdf's <c>get_inherited</c>).</summary>
    public static PdfItem? Inherited(PdfDictionary node, string key)
    {
        for (var current = node; current is not null; current = Dict(current.Elements["/Parent"]))
        {
            if (current.Elements.ContainsKey(key))
            {
                return current.Elements[key];
            }
        }

        return null;
    }

    /// <summary>The page's (inherited) media box width and height, right minus left and top minus bottom (pypdf's <c>mediabox.width/height</c>).</summary>
    public static (double Width, double Height) MediaBoxSize(PdfPage page)
    {
        var box = page.MediaBox;
        return (box.X2 - box.X1, box.Y2 - box.Y1);
    }

    /// <summary>A stream's decoded bytes.</summary>
    public static byte[] Decoded(PdfDictionary stream) => stream.Stream?.UnfilteredValue ?? [];
}
