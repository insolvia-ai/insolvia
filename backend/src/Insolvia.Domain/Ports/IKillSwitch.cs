namespace Insolvia.Core.Ports;

/// <summary>
/// ADR 0024's environment kill switch: one flag the maintainer flips to
/// refuse every submission without a deploy. Read FRESH on every call — a
/// cached "on" would outlive the flip — and FAILS CLOSED: anything but a
/// clear "on" is off.
/// </summary>
public interface IKillSwitch
{
    /// <summary>Whether submissions are on right now.</summary>
    Task<bool> SubmissionsEnabledAsync(CancellationToken cancellationToken = default);
}
