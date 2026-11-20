using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace Invitations.Api.Features.Photos;

/// <summary>
/// Cloudflare R2 (compatible con S3). Plan gratis: 10 GB, 1M escrituras y 10M lecturas al mes, sin costo de salida.
/// Las llaves vienen de user-secrets o variables de entorno (R2__AccessKeyId, R2__SecretAccessKey), nunca del repo.
/// </summary>
public class R2Options
{
    private string? _accessKeyId;
    private string? _secretAccessKey;

    /// <summary>Id de la cuenta de Cloudflare (parte del endpoint https://&lt;id&gt;.r2.cloudflarestorage.com).</summary>
    public string? AccountId { get; set; }
    public string? Bucket { get; set; }
    // Trim: los secretos pegados en Secret Manager a veces traen un salto de línea al final.
    public string? AccessKeyId { get => _accessKeyId; set => _accessKeyId = value?.Trim(); }
    public string? SecretAccessKey { get => _secretAccessKey; set => _secretAccessKey = value?.Trim(); }
    /// <summary>URL pública del bucket (https://pub-xxxx.r2.dev o un dominio propio), con o sin "/" al final.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Solo para pruebas locales con otro servicio S3 (p. ej. MinIO). Por defecto, el endpoint de R2.</summary>
    public string? Endpoint { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) && !string.IsNullOrWhiteSpace(PublicBaseUrl) &&
        (!string.IsNullOrWhiteSpace(AccountId) || !string.IsNullOrWhiteSpace(Endpoint));

    /// <summary>Base pública siempre terminada en "/", o null si las fotos no están configuradas.</summary>
    public string? NormalizedPublicBaseUrl => IsConfigured ? PublicBaseUrl!.Trim().TrimEnd('/') + "/" : null;
}

public record StoredObject(string Key, DateTimeOffset LastModified);

public record PresignedUpload(Uri Url, DateTimeOffset ExpiresAt);

public interface IPhotoStorage
{
    bool IsConfigured { get; }
    string? PublicBaseUrl { get; }
    /// <summary>URL para que el navegador suba el archivo directo a R2, sin pasar por la API.</summary>
    PresignedUpload PresignUpload(string key, string contentType, long size);
    Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public class R2PhotoStorage(HttpClient http, IOptions<R2Options> options, TimeProvider clock) : IPhotoStorage
{
    /// <summary>Tiempo que vale la URL de subida: suficiente para subir una foto con mala señal.</summary>
    public static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(10);

    private readonly R2Options _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;
    public string? PublicBaseUrl => _o.NormalizedPublicBaseUrl;

    private S3Signer.Credentials Credentials => new(_o.AccessKeyId!, _o.SecretAccessKey!, Region: "auto");

    private Uri Endpoint => new((_o.Endpoint ?? $"https://{_o.AccountId!.Trim()}.r2.cloudflarestorage.com").TrimEnd('/') + "/");

    /// <summary>Estilo "path" (endpoint/bucket/llave): funciona con R2 sin configurar DNS por bucket.</summary>
    private Uri ObjectUrl(string key) =>
        new(Endpoint, $"{S3Signer.Encode(_o.Bucket!)}/{string.Join('/', key.Split('/').Select(S3Signer.Encode))}");

    public PresignedUpload PresignUpload(string key, string contentType, long size)
    {
        var now = clock.GetUtcNow();
        var url = S3Signer.Presign(Credentials, "PUT", ObjectUrl(key),
            new Dictionary<string, string>
            {
                // Firmados: R2 rechaza la subida si el navegador manda otro tipo o un archivo de otro tamaño.
                ["content-type"] = contentType,
                ["content-length"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            UploadWindow, now);
        return new PresignedUpload(url, now + UploadWindow);
    }

    public async Task<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var url = new Uri(Endpoint, $"{S3Signer.Encode(_o.Bucket!)}?list-type=2&prefix={S3Signer.Encode(prefix)}");
        using var res = await SendAsync(HttpMethod.Get, url, ct);
        var xml = XDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        XNamespace ns = xml.Root!.Name.Namespace;
        return xml.Root.Elements(ns + "Contents")
            .Select(c => new StoredObject(
                c.Element(ns + "Key")!.Value,
                DateTimeOffset.Parse(c.Element(ns + "LastModified")!.Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, ObjectUrl(key), ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        foreach (var (name, value) in S3Signer.AuthHeaders(Credentials, method.Method, url, clock.GetUtcNow(), S3Signer.EmptyPayloadHash))
            req.Headers.TryAddWithoutValidation(name, value);

        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            throw new HttpRequestException($"R2 respondió {(int)res.StatusCode} a {method} {url.AbsolutePath}: {body}");
        }
        return res;
    }
}
