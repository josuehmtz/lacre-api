using System.Security.Claims;
using System.Text.Json;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Common;
using Invitations.Api.Features.Photos;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Templates;

public static class TemplateEndpoints
{
    /// <summary><paramref name="PhotoBaseUrl"/>: base pública para armar la URL de las fotos (null si no hay fotos configuradas).</summary>
    public record TemplateDto(string TemplateKey, JsonElement Config, string? PhotoBaseUrl);

    public record SaveTemplateRequest(string TemplateKey, JsonElement Config);

    public static IEndpointRouteBuilder MapTemplateEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/events/{eventId:guid}/template")
            .WithTags("Templates")
            .RequireAuthorization();

        group.MapGet("/", async (Guid eventId, ClaimsPrincipal user, AppDbContext db, IPhotoStorage photos) =>
        {
            var ev = await db.Events.AccessibleBy(user.GetUserId()).FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev is null) return Results.NotFound();
            // El diseño es gratis: se puede elegir y personalizar la plantilla antes de pagar.
            return Results.Ok(ToDto(ev, photos.PublicBaseUrl));
        });

        group.MapPut("/", async (Guid eventId, SaveTemplateRequest req, ClaimsPrincipal user, AppDbContext db,
            IPhotoStorage photos, TimeProvider clock, ILoggerFactory loggers) =>
        {
            var ev = await db.Events.AccessibleBy(user.GetUserId()).FirstOrDefaultAsync(e => e.Id == eventId);
            if (ev is null) return Results.NotFound();
            if (ev.Status == EventStatus.Archived)
                return Results.Problem("El evento está archivado y ya no se puede editar.", statusCode: 409);

            if (Domain.Templates.Validate(req.TemplateKey, req.Config, ev.Id) is { } error)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = [error] });

            ev.TemplateKey = req.TemplateKey;
            ev.TemplateConfig = req.Config.GetRawText();
            await db.SaveChangesAsync();

            await PhotoEndpoints.CleanupAsync(photos, ev.Id, req.Config, clock, loggers.CreateLogger("Photos"));
            return Results.Ok(ToDto(ev, photos.PublicBaseUrl));
        });

        return api;
    }

    public static TemplateDto ToDto(Event ev, string? photoBaseUrl) => new(
        string.IsNullOrEmpty(ev.TemplateKey) ? Domain.Templates.DefaultFor(ev.Type) : ev.TemplateKey,
        Domain.Templates.Parse(ev.TemplateConfig),
        photoBaseUrl);
}
