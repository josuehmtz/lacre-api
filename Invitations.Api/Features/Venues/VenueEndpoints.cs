using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Invitations.Api.Data;
using Invitations.Api.Domain;
using Invitations.Api.Features.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Features.Venues;

/// <summary>
/// Administradores de Lacre: sus correos van en la configuración Admin:Emails (separados por coma),
/// fuera del repositorio (user-secrets en desarrollo, variable de entorno Admin__Emails en Cloud Run).
/// </summary>
public static class AdminAuth
{
    public const string Policy = "Admin";

    public static void Configure(AuthorizationPolicyBuilder p, IConfiguration config) =>
        p.RequireAuthenticatedUser().RequireAssertion(ctx => IsAdmin(ctx.User, config));

    public static bool IsAdmin(ClaimsPrincipal user, IConfiguration config)
    {
        var email = user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue(ClaimTypes.Name);
        return email is not null && AdminEmails(config).Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> AdminEmails(IConfiguration config) =>
        (config["Admin:Emails"] ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class VenueEndpoints
{
    public record MyVenueDto(Guid Id, string Name, DateOnly AgreementStartsOn, DateOnly? AgreementEndsOn,
        AgreementStatus Status, DateTimeOffset? TerminatedAt, PartnerKind Kind);

    public record MeDto(string Email, bool IsAdmin, IReadOnlyList<MyVenueDto> Venues);

    /// <param name="PartnerKind">Tipo del salón u organizador detrás del link (para nombrarlo bien).</param>
    public record AccessLinkInfoDto(AccessLinkKind Kind, string Name, string? Problem, PartnerKind? PartnerKind);

    public record AcceptedDto(AccessLinkKind Kind, Guid? EventId);

    public record VenueMemberDto(Guid UserId, string Email, DateTimeOffset AddedAt);

    public record VenueDto(Guid Id, string Name, string? ContactName, string? Phone, string? Notes,
        DateOnly AgreementStartsOn, DateOnly? AgreementEndsOn, AgreementStatus Status,
        DateTimeOffset? TerminatedAt, string? TerminationReason,
        IReadOnlyList<VenueMemberDto> Members, int EventCount, PartnerKind Kind);

    public record TerminateRequest([property: StringLength(500)] string? Reason);

    public record VenueEventDto(Guid Id, string Title, DateTimeOffset StartsAt, EventStatus Status, string? PlanName,
        DateTimeOffset CreatedAt, bool HasClient);

    public record SaveVenueRequest(
        [property: Required, StringLength(200, MinimumLength = 1)] string Name,
        [property: StringLength(200)] string? ContactName,
        [property: StringLength(20)] string? Phone,
        [property: StringLength(1000)] string? Notes,
        DateOnly AgreementStartsOn,
        /// <summary>null = por tiempo indefinido.</summary>
        DateOnly? AgreementEndsOn,
        PartnerKind Kind = PartnerKind.Venue);

    public record InviteLinkDto(string Token, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapVenueEndpoints(this IEndpointRouteBuilder api)
    {
        // Quién soy: el front decide si mostrar el panel de administración y la opción de crear eventos por convenio.
        api.MapGet("/me", async (ClaimsPrincipal user, AppDbContext db, IConfiguration config, TimeProvider clock) =>
        {
            var userId = user.GetUserId();
            var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
            if (email is null) return Results.Unauthorized();

            var now = clock.GetUtcNow();
            var venues = await db.VenueMembers.Where(m => m.UserId == userId).Select(m => m.Venue)
                .OrderBy(v => v.Name).ToListAsync();
            return Results.Ok(new MeDto(email, AdminAuth.IsAdmin(user, config),
                venues.Select(v => new MyVenueDto(v.Id, v.Name, v.AgreementStartsOn, v.AgreementEndsOn,
                    VenueRules.Status(v, now), v.TerminatedAt, v.Kind)).ToList()));
        }).WithTags("Me").RequireAuthorization();

        MapAccessLinks(api);
        MapAdmin(api);
        return api;
    }

    private static void MapAccessLinks(IEndpointRouteBuilder api)
    {
        var links = api.MapGroup("/access-links/{token}").WithTags("Access links");

        // Sin sesión: la página del link muestra a qué te estás uniendo antes de pedir que inicies sesión.
        links.MapGet("/", async (string token, AppDbContext db, TimeProvider clock) =>
        {
            var link = await FindAsync(db, token);
            if (link is null) return Results.NotFound();
            var name = link.Kind == AccessLinkKind.EventOwner ? link.Event!.Title : link.Venue!.Name;
            var partnerId = link.Kind == AccessLinkKind.EventOwner ? link.Event!.VenueId : link.VenueId;
            PartnerKind? partnerKind = partnerId is { } pid
                ? await db.Venues.Where(v => v.Id == pid).Select(v => v.Kind).FirstOrDefaultAsync()
                : null;
            return Results.Ok(new AccessLinkInfoDto(link.Kind, name, AccessLinks.UnusableReason(link, clock.GetUtcNow()), partnerKind));
        });

        links.MapPost("/accept", async (string token, ClaimsPrincipal user, AppDbContext db, TimeProvider clock) =>
        {
            var link = await FindAsync(db, token);
            if (link is null) return Results.NotFound();

            var now = clock.GetUtcNow();
            if (AccessLinks.UnusableReason(link, now) is { } reason)
                return Results.Problem(reason, statusCode: StatusCodes.Status410Gone);

            var userId = user.GetUserId();
            switch (link.Kind)
            {
                case AccessLinkKind.EventOwner:
                    var ev = link.Event!;
                    if (ev.OwnerId is not null && ev.OwnerId != userId)
                        return Results.Problem("Este evento ya tiene un cliente vinculado.", statusCode: StatusCodes.Status409Conflict);
                    ev.OwnerId = userId;
                    break;

                case AccessLinkKind.VenueMember:
                    if (!await db.VenueMembers.AnyAsync(m => m.VenueId == link.VenueId && m.UserId == userId))
                        db.VenueMembers.Add(new VenueMember { VenueId = link.VenueId!.Value, UserId = userId, AddedAt = now });
                    break;
            }

            link.UsedAt = now;
            link.UsedById = userId;
            await db.SaveChangesAsync();
            return Results.Ok(new AcceptedDto(link.Kind, link.EventId));
        }).RequireAuthorization();
    }

    private static Task<AccessLink?> FindAsync(AppDbContext db, string token)
    {
        var hash = AccessLinks.Hash(token);
        return db.AccessLinks.Include(l => l.Event).Include(l => l.Venue).FirstOrDefaultAsync(l => l.TokenHash == hash);
    }

    private static void MapAdmin(IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin/venues").WithTags("Admin").RequireAuthorization(AdminAuth.Policy);

        admin.MapGet("/", async (AppDbContext db, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow();
            var venues = await db.Venues.AsNoTracking()
                .Include(v => v.Members).ThenInclude(m => m.User)
                .OrderBy(v => v.Name)
                .Select(v => new { Venue = v, EventCount = v.Events.Count })
                .ToListAsync();
            return Results.Ok(venues.Select(x => ToDto(x.Venue, x.EventCount, now)));
        });

        admin.MapPost("/", async (SaveVenueRequest req, AppDbContext db, TimeProvider clock) =>
        {
            if (Validate(req) is { } problem) return problem;
            var venue = new Venue { Id = Guid.CreateVersion7(), CreatedAt = clock.GetUtcNow() };
            Apply(venue, req);
            db.Venues.Add(venue);
            await db.SaveChangesAsync();
            return Results.Created($"/api/admin/venues/{venue.Id}", ToDto(venue, 0, clock.GetUtcNow()));
        });

        admin.MapPut("/{id:guid}", async (Guid id, SaveVenueRequest req, AppDbContext db, TimeProvider clock) =>
        {
            if (Validate(req) is { } problem) return problem;
            var venue = await db.Venues.Include(v => v.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(v => v.Id == id);
            if (venue is null) return Results.NotFound();
            Apply(venue, req);
            await db.SaveChangesAsync();
            var count = await db.Events.CountAsync(e => e.VenueId == id);
            return Results.Ok(ToDto(venue, count, clock.GetUtcNow()));
        });

        // Terminar el convenio antes de tiempo: el salón ya no puede crear eventos; los existentes siguen funcionando.
        admin.MapPost("/{id:guid}/terminate", async (Guid id, TerminateRequest req, AppDbContext db, TimeProvider clock) =>
        {
            var venue = await db.Venues.Include(v => v.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(v => v.Id == id);
            if (venue is null) return Results.NotFound();
            var now = clock.GetUtcNow();
            if (VenueRules.Terminate(venue, req.Reason, now) is { } problem)
                return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(venue, await db.Events.CountAsync(e => e.VenueId == id), now));
        });

        admin.MapPost("/{id:guid}/reactivate", async (Guid id, AppDbContext db, TimeProvider clock) =>
        {
            var venue = await db.Venues.Include(v => v.Members).ThenInclude(m => m.User).FirstOrDefaultAsync(v => v.Id == id);
            if (venue is null) return Results.NotFound();
            VenueRules.Reactivate(venue);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(venue, await db.Events.CountAsync(e => e.VenueId == id), clock.GetUtcNow()));
        });

        // Solo se borran salones sin eventos: con eventos se conserva el historial (se termina el convenio).
        admin.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var venue = await db.Venues.FirstOrDefaultAsync(v => v.Id == id);
            if (venue is null) return Results.NotFound();
            if (await db.Events.AnyAsync(e => e.VenueId == id) || await db.Orders.AnyAsync(o => o.VenueId == id))
                return Results.Problem(
                    "Este salón ya tiene eventos, así que no se puede eliminar. Termina su convenio para que ya no pueda crear eventos.",
                    statusCode: StatusCodes.Status409Conflict);

            // Personal y links se borran en cascada.
            db.Venues.Remove(venue);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Link para que una persona del salón se una a su personal. Cada link sirve para una persona.
        admin.MapPost("/{id:guid}/invite-link", async (Guid id, AppDbContext db, TimeProvider clock) =>
        {
            if (!await db.Venues.AnyAsync(v => v.Id == id)) return Results.NotFound();
            var (link, token) = AccessLinks.New(AccessLinkKind.VenueMember, null, id, clock.GetUtcNow());
            db.AccessLinks.Add(link);
            await db.SaveChangesAsync();
            return Results.Ok(new InviteLinkDto(token, link.ExpiresAt));
        });

        admin.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, AppDbContext db) =>
        {
            var deleted = await db.VenueMembers.Where(m => m.VenueId == id && m.UserId == userId).ExecuteDeleteAsync();
            return deleted == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Eventos dados de alta por el salón: sirve para conciliar lo que el salón te debe por el convenio.
        admin.MapGet("/{id:guid}/events", async (Guid id, AppDbContext db) =>
            Results.Ok(await db.Events.AsNoTracking().Where(e => e.VenueId == id)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => new VenueEventDto(e.Id, e.Title, e.StartsAt, e.Status,
                    e.Plan != null ? e.Plan.Name : null, e.CreatedAt, e.OwnerId != null))
                .ToListAsync()));
    }

    private static IResult? Validate(SaveVenueRequest req) =>
        VenueRules.ValidateAgreement(req.AgreementStartsOn, req.AgreementEndsOn) is { } error
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["agreementEndsOn"] = [error] })
            : null;

    private static void Apply(Venue venue, SaveVenueRequest req)
    {
        venue.Kind = req.Kind;
        venue.Name = req.Name.Trim();
        venue.ContactName = Clean(req.ContactName);
        venue.Phone = Clean(req.Phone);
        venue.Notes = Clean(req.Notes);
        venue.AgreementStartsOn = req.AgreementStartsOn;
        venue.AgreementEndsOn = req.AgreementEndsOn;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static VenueDto ToDto(Venue v, int eventCount, DateTimeOffset now) => new(
        v.Id, v.Name, v.ContactName, v.Phone, v.Notes, v.AgreementStartsOn, v.AgreementEndsOn,
        VenueRules.Status(v, now), v.TerminatedAt, v.TerminationReason,
        v.Members.OrderBy(m => m.AddedAt).Select(m => new VenueMemberDto(m.UserId, m.User?.Email ?? "", m.AddedAt)).ToList(),
        eventCount, v.Kind);
}
