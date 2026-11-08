using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Insolvia.Aws.Signing;

/// <summary>A set of AWS credentials, the session token included when they are temporary.</summary>
/// <param name="AccessKeyId">The access key id.</param>
/// <param name="SecretAccessKey">The secret.</param>
/// <param name="SessionToken">The session token, or null for long-lived keys.</param>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null)
{
    /// <summary>Never the secret or the token: these records reach exception messages and logs.</summary>
    /// <returns>The access key id alone.</returns>
    public override string ToString() => $"AwsCredentials {{ AccessKeyId = {AccessKeyId} }}";
}

/// <summary>
/// AWS Signature Version 4 for one HTTP request (what botocore's
/// <c>SigV4Auth.add_auth</c> did for the Python mailer client). Signs
/// <c>host</c>, <c>x-amz-date</c>, every header the request already carries
/// (here, <c>content-type</c>), and <c>x-amz-security-token</c> when the
/// credentials are temporary — botocore's choice of signed headers for an
/// <c>execute-api</c> call. Pure: the clock and the credentials are arguments.
/// </summary>
public static class SigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// Adds <c>X-Amz-Date</c>, <c>X-Amz-Security-Token</c> (when present) and
    /// <c>Authorization</c> to <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request, with its URI, method and any content headers set.</param>
    /// <param name="body">The exact bytes that will be sent.</param>
    /// <param name="credentials">Who signs.</param>
    /// <param name="region">The region, e.g. <c>us-east-1</c>.</param>
    /// <param name="service">The signing service, e.g. <c>execute-api</c>.</param>
    /// <param name="now">The signing instant.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "SigV4 canonical header names are lower-case by specification.")]
    public static void Sign(HttpRequestMessage request, byte[] body, AwsCredentials credentials, string region, string service, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(credentials);
        var uri = request.RequestUri ?? throw new ArgumentException("the request has no URI", nameof(request));
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var day = amzDate[..8];

        request.Headers.Remove("X-Amz-Date");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        if (credentials.SessionToken is not null)
        {
            request.Headers.Remove("X-Amz-Security-Token");
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", credentials.SessionToken);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
        };
        foreach (var (name, values) in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            var lower = name.ToLowerInvariant();
            if (lower is "authorization" or "user-agent" or "expect")
            {
                continue;
            }

            headers[lower] = string.Join(",", values.Select(value => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
        }

        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalRequest = string.Join(
            "\n",
            request.Method.Method,
            CanonicalPath(uri),
            CanonicalQuery(uri),
            string.Concat(headers.Select(pair => $"{pair.Key}:{pair.Value}\n")),
            signedHeaders,
            Hex(SHA256.HashData(body)));

        var scope = $"{day}/{region}/{service}/aws4_request";
        var stringToSign = string.Join("\n", Algorithm, amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), day);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    // Each segment URI-encoded once, as botocore does for every service but S3.
    private static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.Length == 0)
        {
            return "/";
        }

        return string.Join("/", path.Split('/').Select(segment => Encode(Uri.UnescapeDataString(segment))));
    }

    private static string CanonicalQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return "";
        }

        return string.Join(
            "&",
            query.Split('&')
                .Select(pair => pair.Split('=', 2))
                .Select(parts => (Name: Encode(Uri.UnescapeDataString(parts[0])), Value: Encode(Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : ""))))
                .OrderBy(pair => pair.Name, StringComparer.Ordinal)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(pair => $"{pair.Name}={pair.Value}"));
    }

    // RFC 3986 unreserved characters pass; everything else is %XX, upper-case.
    private static string Encode(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return builder.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "SigV4 hashes are lower-case hex by specification.")]
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
