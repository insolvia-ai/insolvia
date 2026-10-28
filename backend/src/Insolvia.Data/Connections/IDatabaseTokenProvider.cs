namespace Insolvia.Data.Connections;

/// <summary>
/// Mints the password for a new physical connection on the IAM path. RDS's
/// implementation is <see cref="RdsIamTokenProvider"/>; a test substitutes
/// one that returns a known password.
/// </summary>
public interface IDatabaseTokenProvider
{
    /// <summary>A password valid for a connection opened now.</summary>
    ValueTask<string> GetTokenAsync(string host, int port, string username, CancellationToken cancellationToken);
}
