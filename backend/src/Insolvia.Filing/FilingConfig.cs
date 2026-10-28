using Insolvia.Core;

namespace Insolvia.Filing;

/// <summary>
/// The filing worker's configuration, read once at composition (from
/// <c>core/config.py</c>). Two things are deliberately NOT configuration:
/// which court hosts the worker may reach (<see cref="Fence.HostFence"/>'s
/// constant) and whether submissions are on (the kill switch, read at run
/// time from its SSM parameter — locally <c>FILING_SUBMISSIONS_ENABLED</c>
/// stands in, and is refused anywhere deployed).
/// </summary>
public sealed record FilingConfig
{
    /// <summary>The service name.</summary>
    public const string ServiceName = "insolvia-filing";

    /// <summary>
    /// How long one attempt may hold a claim before a redelivery may call it
    /// interrupted — equal to the Lambda's timeout, so a live attempt is never older.
    /// </summary>
    public const int DefaultLeaseSeconds = 600;

    /// <summary>One court request's limit: generous (CM/ECF is slow), an order of magnitude inside the lease.</summary>
    public const double DefaultRequestTimeoutSeconds = 60.0;

    /// <summary>The environments.</summary>
    public static readonly IReadOnlyList<string> Environments = ["local", "staging", "production"];

    /// <summary><c>local</c>, <c>staging</c> or <c>production</c>.</summary>
    public string Environment { get; init; } = "local";

    /// <summary>The case-document bucket.</summary>
    public string? CaseDocumentBucket { get; init; }

    /// <summary>The filing queue (the local poller's).</summary>
    public string? FilingQueueUrl { get; init; }

    /// <summary>The SSM parameter holding the kill switch. Required deployed; absent locally.</summary>
    public string? KillSwitchParameter { get; init; }

    /// <summary>Local only: the kill switch's stand-in. Off unless said otherwise.</summary>
    public bool LocalSubmissionsEnabled { get; init; }

    /// <summary>Local only: where the fake CM/ECF listens. Refused anywhere else.</summary>
    public string? FakeCmEcfUrl { get; init; }

    /// <summary>The lease.</summary>
    public int LeaseSeconds { get; init; } = DefaultLeaseSeconds;

    /// <summary>The court request timeout.</summary>
    public double RequestTimeoutSeconds { get; init; } = DefaultRequestTimeoutSeconds;

    /// <summary>Parse the environment. Refuses every combination a deployed worker must never run with.</summary>
    public static FilingConfig Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? Optional(string name) => read(name) is { Length: > 0 } value ? value : null;
        var environment = Optional("INSOLVIA_ENV") ?? "local";
        if (!Environments.Contains(environment))
        {
            throw new ValidationException($"INSOLVIA_ENV must be one of {string.Join(", ", Environments)}, got '{environment}'");
        }

        var deployed = environment != "local";
        var fake = Optional("FAKE_CMECF_URL");
        if (fake is not null)
        {
            if (deployed)
            {
                throw new ValidationException("FAKE_CMECF_URL is a local-only setting: the fake CM/ECF is never deployed, and a deployed worker must not be pointed at one");
            }

            if (!Uri.TryCreate(fake, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || !Fence.HostFence.Loopback.Contains(uri.IdnHost.Trim('[', ']')))
            {
                throw new ValidationException("FAKE_CMECF_URL must be an http:// loopback address (127.0.0.1)");
            }
        }

        var localEnabled = read("FILING_SUBMISSIONS_ENABLED");
        if (deployed && localEnabled is not null)
        {
            throw new ValidationException("FILING_SUBMISSIONS_ENABLED is the local stand-in for the kill switch; deployed environments read the SSM parameter only");
        }

        var killSwitch = Optional("FILING_KILL_SWITCH_PARAMETER");
        if (deployed && killSwitch is null)
        {
            throw new ValidationException("FILING_KILL_SWITCH_PARAMETER is required outside local: a deployed worker with no kill switch to read must not exist");
        }

        return new FilingConfig
        {
            Environment = environment,
            CaseDocumentBucket = Optional("CASE_DOCUMENT_BUCKET"),
            FilingQueueUrl = Optional("FILING_QUEUE_URL"),
            KillSwitchParameter = killSwitch,
            LocalSubmissionsEnabled = string.Equals((localEnabled ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase),
            FakeCmEcfUrl = fake,
        };
    }
}

/// <summary>The local kill switch: a fixed answer (<c>FILING_SUBMISSIONS_ENABLED</c>), never composed deployed.</summary>
/// <param name="enabled">Whether submissions are on.</param>
public sealed class StaticKillSwitch(bool enabled) : Core.Ports.IKillSwitch
{
    /// <summary>The answer — tests flip it mid-run.</summary>
    public bool Enabled { get; set; } = enabled;

    /// <inheritdoc />
    public Task<bool> SubmissionsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);
}
