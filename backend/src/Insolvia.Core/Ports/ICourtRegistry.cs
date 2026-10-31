namespace Insolvia.Core.Ports;

/// <summary>
/// The two questions the FIRM side asks of the court registry (Python's
/// <c>courts.district(code)</c> and <c>courts.division(court, division)</c>):
/// a firm's default court is validated exactly as a case's own reference is.
/// The registry itself — <c>courts.py</c> and the release files under
/// <c>regulatory/courts/us-bankruptcy/</c> — belongs to the case half (18.7),
/// which implements this; codes match exactly, as the contract requires. Synchronous:
/// the registry ships in the artifact (ADR 0014) and is read from memory.
/// </summary>
public interface ICourtRegistry
{
    /// <summary>Whether the current release knows the district <paramref name="courtCode"/>.</summary>
    bool HasDistrict(string courtCode);

    /// <summary>Whether <paramref name="divisionCode"/> is one of <paramref name="courtCode"/>'s divisions.</summary>
    bool HasDivision(string courtCode, string divisionCode);
}
