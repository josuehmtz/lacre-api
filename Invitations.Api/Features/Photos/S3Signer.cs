using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Invitations.Api.Features.Photos;

/// <summary>
/// Firma AWS Signature V4, la que usan S3 y Cloudflare R2. Implementada a mano (≈80 líneas) en vez del SDK de AWS:
/// así controlamos qué encabezados se firman (Content-Length incluido) y no arrastramos una dependencia grande.
/// </summary>
public static class S3Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public static readonly string EmptyPayloadHash = Hex(SHA256.HashData([]));

    public record Credentials(string AccessKeyId, string SecretAccessKey, string Region, string Service = "s3");

    /// <summary>
    /// URL prefirmada (autenticación por query string). Los <paramref name="signedHeaders"/> (además de host)
    /// quedan en la firma: el navegador tiene que mandar exactamente esos valores o R2 rechaza la subida.
    /// </summary>
    public static Uri Presign(Credentials c, string method, Uri url, IReadOnlyDictionary<string, string> signedHeaders,
        TimeSpan expires, DateTimeOffset now)
    {
        var amzDate = AmzDate(now);
        var scope = Scope(c, now);
        var headers = WithHost(url, signedHeaders);
        var query = ParseQuery(url);
        query["X-Amz-Algorithm"] = Algorithm;
        query["X-Amz-Credential"] = $"{c.AccessKeyId}/{scope}";
        query["X-Amz-Date"] = amzDate;
        query["X-Amz-Expires"] = ((int)expires.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        query["X-Amz-SignedHeaders"] = SignedHeaderNames(headers);

        var signature = Signature(c, method, url, query, headers, UnsignedPayload, amzDate, now);
        query["X-Amz-Signature"] = signature;
        return new UriBuilder(url) { Query = CanonicalQuery(query) }.Uri;
    }

    /// <summary>Encabezados de autenticación para una petición hecha por el servidor (listar, borrar).</summary>
    public static Dictionary<string, string> AuthHeaders(Credentials c, string method, Uri url, DateTimeOffset now,
        string payloadHash)
    {
        var amzDate = AmzDate(now);
        var headers = WithHost(url, new Dictionary<string, string>
        {
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        });
        var signature = Signature(c, method, url, ParseQuery(url), headers, payloadHash, amzDate, now);
        var signedNames = SignedHeaderNames(headers);
        // HttpClient pone Host solo; los demás se mandan tal cual.
        var result = headers.Where(h => h.Key != "host").ToDictionary();
        result["Authorization"] =
            $"{Algorithm} Credential={c.AccessKeyId}/{Scope(c, now)}, SignedHeaders={signedNames}, Signature={signature}";
        return result;
    }

    private static string Signature(Credentials c, string method, Uri url, SortedDictionary<string, string> query,
        SortedDictionary<string, string> headers, string payloadHash, string amzDate, DateTimeOffset now)
    {
        var canonicalRequest = string.Join('\n',
            method,
            EncodePath(url.AbsolutePath),
            CanonicalQuery(query),
            string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n")),
            SignedHeaderNames(headers),
            payloadHash);

        var stringToSign = string.Join('\n',
            Algorithm, amzDate, Scope(c, now), Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + c.SecretAccessKey), DateStamp(now));
        key = Hmac(key, c.Region);
        key = Hmac(key, c.Service);
        key = Hmac(key, "aws4_request");
        return Hex(Hmac(key, stringToSign));
    }

    private static SortedDictionary<string, string> WithHost(Uri url, IReadOnlyDictionary<string, string> headers)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}",
        };
        foreach (var (name, value) in headers) result[name.ToLowerInvariant()] = value;
        return result;
    }

    private static SortedDictionary<string, string> ParseQuery(Uri url)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var name = Uri.UnescapeDataString(i < 0 ? part : part[..i]);
            result[name] = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return result;
    }

    private static string CanonicalQuery(SortedDictionary<string, string> query) =>
        string.Join('&', query
            .Select(q => (Name: Encode(q.Key), Value: Encode(q.Value)))
            .OrderBy(q => q.Name, StringComparer.Ordinal)
            .Select(q => $"{q.Name}={q.Value}"));

    private static string SignedHeaderNames(IEnumerable<KeyValuePair<string, string>> headers) =>
        string.Join(';', headers.Select(h => h.Key.ToLowerInvariant()).Order(StringComparer.Ordinal));

    private static string Scope(Credentials c, DateTimeOffset now) => $"{DateStamp(now)}/{c.Region}/{c.Service}/aws4_request";
    private static string AmzDate(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string DateStamp(DateTimeOffset now) => now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Codificación URI de SigV4: solo A-Z a-z 0-9 - _ . ~ quedan sin escapar.</summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var ch = (char)b;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '~') sb.Append(ch);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string EncodePath(string absolutePath) =>
        string.Join('/', absolutePath.Split('/').Select(s => Encode(Uri.UnescapeDataString(s))));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
