using System.Text.Json;
using System.Text.RegularExpressions;

namespace Invitations.Api.Domain;

/// <summary>
/// Fotos de la invitación (portada y galería). Viven en Cloudflare R2; el diseño (jsonb) solo guarda sus llaves:
/// <c>{"photos": {"cover": "events/&lt;id&gt;/&lt;guid&gt;.webp", "gallery": ["..."]}}</c>.
/// </summary>
public static partial class Photos
{
    /// <summary>
    /// Tamaño máximo por archivo. El navegador las reduce antes de subirlas (≈200–400 KB en WebP),
    /// el límite solo protege por si alguien llama a la API directo.
    /// </summary>
    public const long MaxBytes = 3 * 1024 * 1024;
    public const int MaxGallery = 8;
    /// <summary>Archivos guardados por evento (incluye reemplazadas que aún no se limpian). Tope contra abusos.</summary>
    public const int MaxStoredPerEvent = 30;
    /// <summary>Una foto subida pero aún no guardada en el diseño se conserva este tiempo antes de limpiarla.</summary>
    public static readonly TimeSpan UnsavedGrace = TimeSpan.FromHours(1);

    /// <summary>Solo imágenes rasterizadas: SVG o HTML servidos desde el bucket podrían ejecutar scripts.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtensionByType = new Dictionary<string, string>
    {
        ["image/webp"] = "webp",
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
    };

    public static string Prefix(Guid eventId) => $"events/{eventId:N}/";

    public static string NewKey(Guid eventId, string contentType) =>
        $"{Prefix(eventId)}{Guid.NewGuid():N}.{ExtensionByType[contentType]}";

    public static bool BelongsTo(string key, Guid eventId) =>
        key.StartsWith(Prefix(eventId), StringComparison.Ordinal) && KeyPattern().IsMatch(key);

    /// <summary>Error legible si no se puede subir este archivo, o null.</summary>
    public static string? ValidateUpload(string? contentType, long size)
    {
        if (contentType is null || !ExtensionByType.ContainsKey(contentType))
            return "Solo se permiten fotos JPG, PNG o WebP.";
        if (size <= 0) return "El archivo está vacío.";
        if (size > MaxBytes) return $"La foto pesa más de {MaxBytes / (1024 * 1024)} MB.";
        return null;
    }

    /// <summary>Valida la sección "photos" del diseño. Null si es válida (o si no hay fotos).</summary>
    public static string? Validate(JsonElement config, Guid eventId)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("photos", out var photos)) return null;
        if (photos.ValueKind != JsonValueKind.Object) return "Las fotos deben ser un objeto.";

        if (photos.TryGetProperty("cover", out var cover) && cover.ValueKind != JsonValueKind.Null)
        {
            if (cover.ValueKind != JsonValueKind.String || !BelongsTo(cover.GetString()!, eventId))
                return "La foto de portada no es válida.";
        }

        if (photos.TryGetProperty("gallery", out var gallery))
        {
            if (gallery.ValueKind != JsonValueKind.Array) return "La galería debe ser una lista.";
            if (gallery.GetArrayLength() > MaxGallery) return $"La galería admite hasta {MaxGallery} fotos.";
            foreach (var item in gallery.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || !BelongsTo(item.GetString()!, eventId))
                    return "Una de las fotos de la galería no es válida.";
        }
        return null;
    }

    /// <summary>Llaves que usa el diseño guardado (portada y galería).</summary>
    public static IReadOnlySet<string> ReferencedKeys(JsonElement config)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("photos", out var photos) ||
            photos.ValueKind != JsonValueKind.Object)
            return keys;

        if (photos.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.String)
            keys.Add(cover.GetString()!);
        if (photos.TryGetProperty("gallery", out var gallery) && gallery.ValueKind == JsonValueKind.Array)
            foreach (var item in gallery.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) keys.Add(item.GetString()!);
        return keys;
    }

    /// <summary>
    /// Archivos del evento que ya se pueden borrar: no los usa el diseño y no son subidas recientes
    /// (alguien podría estar a media edición en otra pestaña).
    /// </summary>
    public static IEnumerable<string> Orphans(IEnumerable<(string Key, DateTimeOffset LastModified)> stored,
        IReadOnlySet<string> referenced, DateTimeOffset now) =>
        stored.Where(o => !referenced.Contains(o.Key) && now - o.LastModified > UnsavedGrace).Select(o => o.Key);

    [GeneratedRegex(@"^events/[0-9a-f]{32}/[0-9a-f]{32}\.(webp|jpg|png)$")]
    private static partial Regex KeyPattern();
}
