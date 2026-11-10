using System.Text.Json.Serialization;
using Insolvia.Domain.Engines.CreditorMatrix;

namespace Insolvia.Api.Analysis;

/// <summary>
/// The court's creditor mailing list in the court's own format, or every
/// reason it cannot be produced yet — never both.
/// </summary>
/// <param name="FileName">The name to save the file under.</param>
/// <param name="CreditorCount">How many address blocks the file holds.</param>
/// <param name="DuplicatesOmitted">Blocks dropped because they would print identically.</param>
/// <param name="Problems">Why the file cannot be produced; empty when it was.</param>
/// <param name="Content">The file's exact text (CRLF line ends); absent when there are problems.</param>
public sealed record CreditorMatrixView(
    string FileName,
    int CreditorCount,
    int DuplicatesOmitted,
    IReadOnlyList<CreditorMatrixProblem> Problems,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content)
{
    /// <summary>The view of <paramref name="matrix"/>.</summary>
    /// <param name="matrix">The generation outcome.</param>
    /// <returns>The view.</returns>
    public static CreditorMatrixView Of(GeneratedMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return new(
            CreditorMatrices.MatrixFileName,
            matrix.CreditorCount,
            matrix.DuplicatesOmitted,
            [.. matrix.Problems.Select(problem => new CreditorMatrixProblem(problem.CreditorId, problem.Field, problem.Message))],
            matrix.Content);
    }
}

/// <summary>One reason the matrix cannot be produced.</summary>
/// <param name="CreditorId">The creditor record at fault; absent for a case-wide problem.</param>
/// <param name="Field">The field at fault.</param>
/// <param name="Message">What is wrong, for a person.</param>
public sealed record CreditorMatrixProblem(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CreditorId,
    string Field,
    string Message);
