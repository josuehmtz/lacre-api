using System.ComponentModel.DataAnnotations;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Venues;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Events;

/// <summary>
/// Paquetes a la medida (más invitados que el paquete más grande). Solo administradores: el cliente pide
/// la cotización, paga fuera de la app y el administrador le da a su evento el límite acordado.
/// </summary>
public static class AdminEventEndpoints
{
    public record AdminEventDto(Guid Id, string Title, DateTimeOffset StartsAt, EventStatus Status, string? OwnerEmail,
        string? VenueName, string? PlanName, int MaxPasses, int TotalPasses, string? CustomLimitNote);

    public record CustomLimitRequest(
        int MaxPasses,
        [property: Required, StringLength(500, MinimumLength = 1)] string Note);

    public static IEndpointRouteBuilder MapAdminEventEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin/events").WithTags("Admin").RequireAuthorization(AdminAuth.Policy);

        // Buscar los eventos de un cliente por su correo (o parte de él).
        admin.MapGet("/", async (string? email, AppDbContext db) =>
        {
            var term = email?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length < 3)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Escribe al menos 3 letras del correo."] });

            var pattern = $"%{term.Replace("%", "").Replace("_", "")}%";
            var events = await db.Events.AsNoTracking()
                .Where(e => e.Owner != null && EF.Functions.ILike(e.Owner.Email!, pattern))
                .OrderByDescending(e => e.CreatedAt)
                .Take(50)
                .Select(e => new AdminEventDto(e.Id, e.Title, e.StartsAt, e.Status, e.Owner!.Email,
                    e.Venue != null ? e.Venue.Name : null, e.Plan != null ? e.Plan.Name : null, e.MaxPasses,
                    e.Invitations.Sum(i => i.Passes), e.CustomLimitNote))
                .ToListAsync();
            return Results.Ok(events);
        });

        admin.MapPost("/{id:guid}/custom-limit", async (Guid id, CustomLimitRequest req, AppDbContext db) =>
        {
            var ev = await db.Events.Include(e => e.Owner).Include(e => e.Venue).Include(e => e.Plan)
                .FirstOrDefaultAsync(e => e.Id == id);
            if (ev is null) return Results.NotFound();

            var totalPasses = await db.Invitations.Where(i => i.EventId == id).SumAsync(i => i.Passes);
            if (PaymentRules.ApplyCustomLimit(ev, req.MaxPasses, totalPasses, req.Note) is { } problem)
                return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);

            await db.SaveChangesAsync();
            return Results.Ok(new AdminEventDto(ev.Id, ev.Title, ev.StartsAt, ev.Status, ev.Owner?.Email, ev.Venue?.Name,
                ev.Plan?.Name, ev.MaxPasses, totalPasses, ev.CustomLimitNote));
        });

        return api;
    }
}
