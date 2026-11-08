using Insolvia.Core.Filings;
using Insolvia.Forms.Pdf;
using PdfSharp.Pdf;

namespace Insolvia.Forms;

/// <summary>
/// The one-page "Court filing receipt" the filing worker keeps with the case
/// once the court confirmed a filing (from services/filing
/// <c>core/receipt.py</c>'s <c>render_receipt</c>): drawn from the captured
/// facts with <see cref="PdfDraw"/>'s primitives — the amendment cover
/// sheet's — so no second PDF library joins the worker's image. Insolvia's
/// record of the court's answer, and it says so on the page.
/// </summary>
public static class FilingReceipt
{
    private const double PageWidth = 612.0;
    private const double PageHeight = 792.0;
    private const double Margin = 72.0;

    /// <summary>The receipt's bytes. Deterministic for the same record.</summary>
    public static byte[] Render(Filing filing, Confirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(filing);
        ArgumentNullException.ThrowIfNull(confirmation);
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(PageWidth, PageHeight));
        var font = PdfDraw.AddHelvetica(doc);
        var ops = new List<byte>();
        var y = PageHeight - Margin;

        void Line(string text, double size = 11.0, double gray = 0.0)
        {
            var parts = TextWrap.Wrap(text, (int)((PageWidth - (2 * Margin)) / (size * 0.55)));
            foreach (var part in parts.Count > 0 ? parts : [string.Empty])
            {
                ops.AddRange(PdfDraw.TextOps(part, Margin, y, size, 0.0, gray));
                y -= Math.Max(16.0, size * 1.3);
            }
        }

        Line("COURT FILING RECEIPT", size: 16.0);
        y -= 8;
        Line("Recorded by Insolvia from the court's confirmation screen. The court's own notices remain the evidence of what was filed.", size: 9.0, gray: 0.35);
        y -= 8;
        Line($"Court: {filing.Court.ToUpperInvariant()}   Division: {filing.Division}");
        Line($"Case number: {confirmation.CaseNumber}");
        Line($"Filed at: {confirmation.FiledAt}");
        if (!string.IsNullOrEmpty(confirmation.ReceiptNumber))
        {
            Line($"Court receipt: {confirmation.ReceiptNumber}");
        }

        if (!string.IsNullOrEmpty(confirmation.FeeDue))
        {
            Line($"Filing fee due: {confirmation.FeeDue} - pay in your own CM/ECF session");
        }

        y -= 8;
        Line("Docket entries:");
        foreach (var entry in confirmation.DocketEntries)
        {
            Line($"  {entry}", size: 10.0);
        }

        y -= 8;
        Line($"Filing {filing.FilingId}, approval {filing.ApprovalId}", size: 9.0, gray: 0.35);
        PdfDraw.DrawOnPage(doc, page, font, [.. ops]);
        return Determinism.Save(doc, [.. ops], created: true);
    }
}
