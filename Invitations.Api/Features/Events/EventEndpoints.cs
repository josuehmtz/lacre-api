using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Common;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Events;

public static class EventEndpoints
{
    public record EventSummaryDto(
        Guid Id, string Title, EventType Type, EventStatus Status, DateTimeOffset StartsAt,
        string? VenueName, int InvitationCount, int TotalPasses, string? ManagedBy, bool HasClient);

    /// <summary>
    /// <paramref name="ManagedBy"/>: salón con convenio que administra el evento.
    /// <paramref name="IsVenueStaff"/>: quien consulta es personal de ese salón (puede vincular al cliente).
    /// </summary>
    public record EventDetailDto(
        Guid Id, string Title, EventType Type, EventStatus Status, DateTimeOffset StartsAt,
        string? VenueName, string? VenueAddress, string? MapsUrl, DateTimeOffset? RsvpDeadline,
        int InvitationCount, int TotalPasses, int PassLimit, RsvpSummaryDto Rsvp,
        string? ManagedBy, bool IsVenueStaff, bool HasClient, string? ClientEmail);

    /// <summary>Evento que da de alta el salón con el paquete que vendió.</summary>
    public record CreateVenueEventRequest(
        [property: Required] SaveEventRequest Event,
        [property: Required] string PlanCode);

    public record ClientLinkDto(string Token, DateTimeOffset ExpiresAt);

    /// <summary>Resumen de confirmaciones: invitaciones por estado y personas confirmadas.</summary>
    public record RsvpSummaryDto(int Confirmed, int Declined, int Pending, int ConfirmedPasses, int PendingPasses,
        int CheckedIn);

    public record SaveEventRequest(
        [property: Required, StringLength(200, MinimumLength = 1)] string Title,
        EventType Type,
        DateTimeOffset StartsAt,
        [property: StringLength(200)] string? VenueName,
        [property: StringLength(300)] string? VenueAddress,
        [property: Url, StringLength(500)] string? MapsUrl,
        DateTimeOffset? RsvpDeadline);

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder api)
    {
        var events = api.MapGroup("/events").WithTags("Events").RequireAuthorization();

        events.MapGet("/", async (ClaimsPrincipal user, AppDbContext db) =>
            await db.Events.AccessibleBy(user.GetUserId())
                .OrderBy(e => e.StartsAt)
                .Select(e => new EventSummaryDto(
                    e.Id, e.Title, e.Type, e.Status, e.StartsAt, e.VenueName,
                    e.Invitations.Count, e.Invitations.Sum(i => i.Passes),
                    e.Venue != null ? e.Venue.Name : null, e.OwnerId != null))
                .ToListAsync());

        events.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var ev = await db.Events.AccessibleBy(user.GetUserId()).FirstOrDefaultAsync(e => e.Id == id);
            return ev is null ? Results.NotFound() : Results.Ok(await ToDetailAsync(db, ev, user.GetUserId()));
        });

        // El cliente crea su evento con pago pendiente; todo se desbloquea al pagar el paquete.
        events.MapPost("/", async (SaveEventRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            if (Validate(req) is { } problem) return problem;

            var userId = user.GetUserId();
            // Un solo evento sin pagar a la vez: evita acumular eventos "de prueba".
            if (await db.Events.AnyAsync(e => e.OwnerId == userId && e.VenueId == null && e.Status == EventStatus.Draft))
                return Results.Problem(
                    "Ya tienes un evento con pago pendiente. Complétalo o elimínalo antes de crear otro.",
                    statusCode: StatusCodes.Status409Conflict);

            var ev = new Event { Id = Guid.CreateVersion7(), OwnerId = userId };
            Apply(ev, req);
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            return Results.Created($"/api/events/{ev.Id}", await ToDetailAsync(db, ev, userId));
        });

        // El personal del salón da de alta el evento con el paquete incluido en el convenio (sin pago en línea).
        api.MapPost("/venues/{venueId:guid}/events", async (Guid venueId, CreateVenueEventRequest req,
            ClaimsPrincipal user, AppDbContext db, TimeProvider clock) =>
        {
            if (Validate(req.Event) is { } problem) return problem;

            var userId = user.GetUserId();
            var venue = await db.Venues.FirstOrDefaultAsync(v => v.Id == venueId && v.Members.Any(m => m.UserId == userId));
            if (venue is null) return Results.NotFound();

            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == req.PlanCode);
            if (plan is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            if (VenueRules.CannotGrantReason(venue, plan, null, 0, EventStatus.Draft, now) is { } reason)
                return Results.Problem(reason, statusCode: StatusCodes.Status409Conflict);

            var ev = new Event { Id = Guid.CreateVersion7(), VenueId = venue.Id };
            Apply(ev, req.Event);
            ev.VenueName ??= venue.Name;
            db.Events.Add(ev);
            db.Orders.Add(VenueRules.GrantPlan(ev, plan, venue, now));
            await db.SaveChangesAsync();
            return Results.Created($"/api/events/{ev.Id}", await ToDetailAsync(db, ev, userId));
        }).WithTags("Venues").RequireAuthorization();

        // Link para que el cliente del salón vincule su cuenta y quede como dueño del evento.
        // Generar uno nuevo invalida los anteriores.
        events.MapPost("/{id:guid}/client-link", async (Guid id, ClaimsPrincipal user, AppDbContext db, TimeProvider clock) =>
        {
            var userId = user.GetUserId();
            var ev = await db.Events.AccessibleBy(userId).FirstOrDefaultAsync(e => e.Id == id);
            if (ev is null) return Results.NotFound();
            if (!await db.ManagesAsync(ev, userId))
                return Results.Problem("Solo el personal del salón puede generar este link.", statusCode: StatusCodes.Status403Forbidden);
            if (ev.OwnerId is not null)
                return Results.Problem("Este evento ya tiene un cliente vinculado.", statusCode: StatusCodes.Status409Conflict);

            await db.AccessLinks.Where(l => l.EventId == ev.Id && l.Kind == AccessLinkKind.EventOwner && l.UsedAt == null)
                .ExecuteDeleteAsync();
            var (link, token) = AccessLinks.New(AccessLinkKind.EventOwner, ev.Id, null, clock.GetUtcNow());
            db.AccessLinks.Add(link);
            await db.SaveChangesAsync();
            return Results.Ok(new ClientLinkDto(token, link.ExpiresAt));
        });

        events.MapPut("/{id:guid}", async (Guid id, SaveEventRequest req, ClaimsPrincipal user, AppDbContext db) =>
        {
            if (Validate(req) is { } problem) return problem;

            var ev = await db.Events.AccessibleBy(user.GetUserId()).FirstOrDefaultAsync(e => e.Id == id);
            if (ev is null) return Results.NotFound();
            if (ev.Status == EventStatus.Archived)
                return Results.Problem("El evento está archivado y ya no se puede editar.", statusCode: 409);

            Apply(ev, req);
            await db.SaveChangesAsync();
            return Results.Ok(await ToDetailAsync(db, ev, user.GetUserId()));
        });

        events.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            // Solo el cliente borra su propio evento sin pagar.
            var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == id && e.OwnerId == user.GetUserId());
            if (ev is null) return Results.NotFound();
            // Un evento pagado no se borra desde el panel (hay pagos asociados).
            if (ev.Status != EventStatus.Draft)
                return Results.Problem("Solo se pueden eliminar eventos en borrador.", statusCode: 409);

            db.Events.Remove(ev);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    private static IResult? Validate(SaveEventRequest req)
    {
        if (req.RsvpDeadline is { } deadline && deadline > req.StartsAt)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["rsvpDeadline"] = ["La fecha límite de confirmación debe ser antes del evento."],
            });
        return null;
    }

    private static void Apply(Event ev, SaveEventRequest req)
    {
        ev.Title = req.Title.Trim();
        ev.Type = req.Type;
        // Postgres (timestamptz) guarda en UTC; el front muestra en la hora local del usuario.
        ev.StartsAt = req.StartsAt.ToUniversalTime();
        ev.VenueName = Clean(req.VenueName);
        ev.VenueAddress = Clean(req.VenueAddress);
        ev.MapsUrl = Clean(req.MapsUrl);
        ev.RsvpDeadline = req.RsvpDeadline?.ToUniversalTime();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task<EventDetailDto> ToDetailAsync(AppDbContext db, Event ev, Guid userId)
    {
        var byStatus = await db.Invitations.Where(i => i.EventId == ev.Id)
            .GroupBy(i => i.RsvpStatus)
            .Select(g => new { Status = g.Key, Count = g.Count(), Passes = g.Sum(i => i.Passes), Confirmed = g.Sum(i => i.ConfirmedPasses) })
            .ToListAsync();

        int CountOf(RsvpStatus s) => byStatus.Where(x => x.Status == s).Sum(x => x.Count);
        var rsvp = new RsvpSummaryDto(
            CountOf(RsvpStatus.Confirmed), CountOf(RsvpStatus.Declined), CountOf(RsvpStatus.Pending),
            ConfirmedPasses: byStatus.Sum(x => x.Confirmed),
            PendingPasses: byStatus.Where(x => x.Status == RsvpStatus.Pending).Sum(x => x.Passes),
            CheckedIn: await db.CheckIns.Where(c => c.Invitation.EventId == ev.Id).SumAsync(c => c.Count));

        return new EventDetailDto(
            ev.Id, ev.Title, ev.Type, ev.Status, ev.StartsAt, ev.VenueName, ev.VenueAddress,
            ev.MapsUrl, ev.RsvpDeadline, byStatus.Sum(x => x.Count), byStatus.Sum(x => x.Passes),
            await db.PassLimitAsync(ev), rsvp,
            ManagedBy: ev.VenueId is { } venueId
                ? await db.Venues.Where(v => v.Id == venueId).Select(v => v.Name).FirstAsync()
                : null,
            IsVenueStaff: await db.ManagesAsync(ev, userId),
            HasClient: ev.OwnerId is not null,
            // El salón ve qué cuenta quedó vinculada como cliente.
            ClientEmail: ev.OwnerId is { } ownerId && ev.VenueId is not null
                ? await db.Users.Where(u => u.Id == ownerId).Select(u => u.Email).FirstOrDefaultAsync()
                : null);
    }
}
