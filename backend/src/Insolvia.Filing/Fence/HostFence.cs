using System.Collections.Frozen;

namespace Insolvia.Filing.Fence;

/// <summary>
/// A request, or a driver, named a host outside this environment's
/// allowlist. Raised before any connection exists. The message carries the
/// host only — never a path, query or credential.
/// </summary>
public sealed class HostNotAllowedException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public HostNotAllowedException()
    {
        Host = string.Empty;
        Environment = string.Empty;
    }

    /// <summary>Creates the exception with a message.</summary>
    public HostNotAllowedException(string message)
        : base(message)
    {
        Host = string.Empty;
        Environment = string.Empty;
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public HostNotAllowedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Host = string.Empty;
        Environment = string.Empty;
    }

    /// <summary>Creates the exception naming the host and the environment.</summary>
    public HostNotAllowedException(string host, string environment, bool _ = true)
        : base($"'{host}' is not an allowed court host in {environment}")
    {
        Host = host;
        Environment = environment;
    }

    /// <summary>The refused host.</summary>
    public string Host { get; }

    /// <summary>The environment whose fence refused it.</summary>
    public string Environment { get; }
}

/// <summary>A checked request target: what the HTTP client may connect to.</summary>
/// <param name="Scheme">http or https.</param>
/// <param name="Host">The host, lower-case.</param>
/// <param name="Port">The port.</param>
/// <param name="PathAndQuery">The path with its query.</param>
public sealed record FenceTarget(string Scheme, string Host, int Port, string PathAndQuery);

/// <summary>
/// The environment host fence (ADR 0024, "Three environments"; from
/// services/filing <c>core/fence.py</c>). THE ALLOWLIST IS THIS CLASS'S
/// CONSTANT, AND NOTHING ELSE: no environment variable, parameter, message,
/// registry record or page can add a host — a court joins only through a
/// reviewed diff here (ADR 0024 PR 10), after its driver opened the fixture
/// case on that court's training database.
/// </summary>
/// <remarks>
/// Enforced twice, each an ordinary code path with tests: the worker checks
/// every origin a driver will use BEFORE the credential is opened, and
/// <see cref="Http.FencedHttpClient"/> checks every request's URL — each
/// redirect hop included, which it never follows itself — before a
/// connection exists (and again in its connect callback).
/// <list type="bullet">
/// <item><c>local</c> — the fake CM/ECF on loopback, by literal address only
/// (127.0.0.1, ::1). Not <c>localhost</c>: a name is resolved through files
/// and resolvers this class does not control.</item>
/// <item><c>staging</c>, <c>production</c> — nothing: no court is verified yet.</item>
/// </list>
/// Only https reaches a deployed allowlist; http is accepted for loopback alone.
/// </remarks>
public sealed class HostFence
{
    /// <summary>The loopback literals.</summary>
    public static readonly FrozenSet<string> Loopback = new[] { "127.0.0.1", "::1" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The allowlist, per environment. EMPTY outside local, on purpose.</summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> AllowedHosts = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        ["local"] = Loopback,
        ["staging"] = FrozenSet<string>.Empty,
        ["production"] = FrozenSet<string>.Empty,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private HostFence(string environment, FrozenSet<string> allowed)
    {
        Environment = environment;
        Allowed = allowed;
    }

    /// <summary>The environment.</summary>
    public string Environment { get; }

    /// <summary>The hosts it lets through.</summary>
    public FrozenSet<string> Allowed { get; }

    /// <summary>The fence for an environment. An unknown one gets an EMPTY allowlist — never a default that reaches somewhere.</summary>
    public static HostFence For(string environment) =>
        new(environment, AllowedHosts.TryGetValue(environment ?? string.Empty, out var allowed) ? allowed : FrozenSet<string>.Empty);

    /// <summary>
    /// The target <paramref name="url"/> names, or <see cref="HostNotAllowedException"/>.
    /// Refuses anything that is not plain http(s) to an allowlisted host:
    /// userinfo, a missing host, another scheme, http to a non-loopback host.
    /// </summary>
    public FenceTarget Check(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc)
        {
            throw new HostNotAllowedException("<none>", Environment, true);
        }

        var host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0
            || uri.UserInfo.Length > 0
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !Allowed.Contains(host)
            || (uri.Scheme == Uri.UriSchemeHttp && !Loopback.Contains(host)))
        {
            throw new HostNotAllowedException(host.Length == 0 ? "<none>" : host, Environment, true);
        }

        return new FenceTarget(uri.Scheme, host, uri.Port, uri.PathAndQuery);
    }

    /// <summary>Whether <paramref name="url"/> passes.</summary>
    public bool Permits(string url)
    {
        try
        {
            Check(url);
            return true;
        }
        catch (HostNotAllowedException)
        {
            return false;
        }
    }
}
