using Invitations.Api.Data;
using Invitations.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Common;

public static class QueryExtensions
{
    /// <summary>
    /// Eventos que el usuario puede ver y editar: los suyos como cliente y los de los salones
    /// de cuyo personal forma parte. Evita que alguien vea o edite eventos ajenos.
    /// </summary>
    public static IQueryable<Event> AccessibleBy(this IQueryable<Event> events, Guid userId) =>
        events.Where(e => e.OwnerId == userId ||
            (e.VenueId != null && e.Venue!.Members.Any(m => m.UserId == userId)));

    /// <summary>¿El usuario es parte del personal del salón que administra el evento?</summary>
    public static Task<bool> ManagesAsync(this AppDbContext db, Event ev, Guid userId) =>
        ev.VenueId is { } venueId
            ? db.VenueMembers.AnyAsync(m => m.VenueId == venueId && m.UserId == userId)
            : Task.FromResult(false);

    /// <summary>Límite de pases: el del paquete pagado (0 mientras el pago está pendiente).</summary>
    public static Task<int> PassLimitAsync(this AppDbContext db, Event ev) => Task.FromResult(ev.MaxPasses);
}

public static class EventAccess
{
    /// <summary>
    /// Todo lo del evento (diseño, invitados, mesas, hostess) se desbloquea hasta que el paquete está pagado
    /// (o lo incluyó el convenio de un salón).
    /// </summary>
    public static IResult? RequirePaid(Event ev) => ev.Status == EventStatus.Draft
        ? Results.Problem("Completa el pago de tu paquete para usar esta sección.",
            title: "Pago pendiente", statusCode: StatusCodes.Status402PaymentRequired)
        : null;
}
