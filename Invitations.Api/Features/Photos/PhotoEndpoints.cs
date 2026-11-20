using System.Security.Claims;
using System.Text.Json;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Common;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Photos;

public static class PhotoEndpoints
{
    public record UploadRequest(string ContentType, long Size);

    /// <summary><paramref name="Key"/> es lo que se guarda en el diseño; <paramref name="Url"/>, dónde se verá la foto.</summary>
    public record UploadTicketDto(string UploadUrl, string Key, string Url, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder api)
    {
        // El navegador pide permiso aquí y sube el archivo directo a R2: la foto no pasa por Cloud Run
        // (no consume su CPU ni su salida de red) y R2 no cobra por servirla.
        api.MapPost("/events/{eventId:guid}/photos", async (Guid eventId, UploadRequest req, ClaimsPrincipal user,
            AppDbContext db, IPhotoStorage storage, CancellationToken ct) =>
        {
            if (!storage.IsConfigured)
                return Results.Problem("La subida de fotos todavía no está disponible.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var ev = await db.Events.AccessibleBy(user.GetUserId()).FirstOrDefaultAsync(e => e.Id == eventId, ct);
            if (ev is null) return Results.NotFound();
            if (EventAccess.RequirePaid(ev) is { } unpaid) return unpaid;
            if (ev.Status == EventStatus.Archived)
                return Results.Problem("El evento está archivado y ya no se puede editar.", statusCode: 409);

            if (Domain.Photos.ValidateUpload(req.ContentType, req.Size) is { } invalid)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["photo"] = [invalid] });

            var stored = await storage.ListAsync(Domain.Photos.Prefix(eventId), ct);
            if (stored.Count >= Domain.Photos.MaxStoredPerEvent)
                return Results.Problem(
                    "Llegaste al límite de fotos de este evento. Guarda el diseño y espera una hora para que se liberen las que quitaste.",
                    statusCode: StatusCodes.Status409Conflict);

            var key = Domain.Photos.NewKey(eventId, req.ContentType);
            var upload = storage.PresignUpload(key, req.ContentType, req.Size);
            return Results.Ok(new UploadTicketDto(upload.Url.ToString(), key, storage.PublicBaseUrl + key, upload.ExpiresAt));
        }).WithTags("Photos").RequireAuthorization();

        return api;
    }

    /// <summary>
    /// Borra de R2 las fotos del evento que el diseño ya no usa (reemplazadas o subidas y nunca guardadas).
    /// Es de mejor esfuerzo: si R2 falla, el diseño ya quedó guardado y se reintenta en el siguiente guardado.
    /// </summary>
    public static async Task CleanupAsync(IPhotoStorage storage, Guid eventId, JsonElement config, TimeProvider clock,
        ILogger logger, CancellationToken ct = default)
    {
        if (!storage.IsConfigured) return;
        try
        {
            var stored = await storage.ListAsync(Domain.Photos.Prefix(eventId), ct);
            var orphans = Domain.Photos.Orphans(stored.Select(o => (o.Key, o.LastModified)),
                Domain.Photos.ReferencedKeys(config), clock.GetUtcNow());
            foreach (var key in orphans) await storage.DeleteAsync(key, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "No se pudieron limpiar las fotos del evento {EventId}", eventId);
        }
    }
}
