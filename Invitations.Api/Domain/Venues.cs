using System.Security.Cryptography;
using System.Text;

namespace Invitations.Api.Domain;

/// <summary>
/// Salón de eventos con convenio: mientras el convenio está vigente, su personal puede dar de alta
/// eventos con el paquete que vendió. El cobro al salón se arregla fuera de la página.
/// </summary>
public class Venue
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }

    /// <summary>Primer día del convenio (inclusive).</summary>
    public DateOnly AgreementStartsOn { get; set; }
    /// <summary>Último día del convenio (inclusive); null = por tiempo indefinido.</summary>
    public DateOnly? AgreementEndsOn { get; set; }

    /// <summary>Si se terminó antes de tiempo: cuándo y por qué. Desde ese momento ya no puede crear eventos.</summary>
    public DateTimeOffset? TerminatedAt { get; set; }
    public string? TerminationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<VenueMember> Members { get; set; } = [];
    public List<Event> Events { get; set; } = [];
}

/// <summary>Usuario del personal del salón: ve y edita todos los eventos del salón.</summary>
public class VenueMember
{
    public Guid VenueId { get; set; }
    public Venue Venue { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AgreementStatus
{
    /// <summary>Aún no llega la fecha de inicio.</summary>
    Upcoming,
    Active,
    /// <summary>Pasó la fecha de término.</summary>
    Expired,
    /// <summary>Se terminó antes de tiempo.</summary>
    Terminated,
}

public enum AccessLinkKind
{
    /// <summary>El cliente del salón se vuelve dueño del evento.</summary>
    EventOwner,
    /// <summary>Una persona se une al personal de un salón.</summary>
    VenueMember,
}

/// <summary>
/// Link de un solo uso para vincular una cuenta (cliente de un evento o personal de un salón).
/// Solo se guarda el hash del token: quien vea la base no puede usar los links.
/// </summary>
public class AccessLink
{
    public Guid Id { get; set; }
    public AccessLinkKind Kind { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid? EventId { get; set; }
    public Event? Event { get; set; }
    public Guid? VenueId { get; set; }
    public Venue? Venue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedById { get; set; }
}

public static class VenueRules
{
    /// <summary>Zona horaria de México: el convenio se cuenta en días locales, no UTC.</summary>
    private static readonly TimeZoneInfo Mexico = FindMexico();

    public static DateOnly LocalToday(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Mexico).DateTime);

    public static AgreementStatus Status(Venue venue, DateTimeOffset now)
    {
        if (venue.TerminatedAt is not null) return AgreementStatus.Terminated;
        var today = LocalToday(now);
        if (today < venue.AgreementStartsOn) return AgreementStatus.Upcoming;
        if (venue.AgreementEndsOn is { } ends && today > ends) return AgreementStatus.Expired;
        return AgreementStatus.Active;
    }

    public static bool AgreementActive(Venue venue, DateTimeOffset now) => Status(venue, now) == AgreementStatus.Active;

    public static string? ValidateAgreement(DateOnly startsOn, DateOnly? endsOn) =>
        endsOn is { } ends && ends < startsOn ? "El convenio debe terminar después de que empieza." : null;

    /// <summary>Termina el convenio de inmediato (p. ej. el salón lo canceló antes de tiempo).</summary>
    public static string? Terminate(Venue venue, string? reason, DateTimeOffset now)
    {
        if (venue.TerminatedAt is not null) return "El convenio ya estaba terminado.";
        venue.TerminatedAt = now;
        venue.TerminationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return null;
    }

    /// <summary>Deshace la terminación anticipada; vuelven a aplicar las fechas del convenio.</summary>
    public static void Reactivate(Venue venue)
    {
        venue.TerminatedAt = null;
        venue.TerminationReason = null;
    }

    /// <summary>Por qué el salón no puede dar este paquete por convenio, o null si sí puede.</summary>
    public static string? CannotGrantReason(Venue venue, Plan target, Plan? current, int totalPasses,
        EventStatus status, DateTimeOffset now, int currentLimit = 0)
    {
        if (!AgreementActive(venue, now)) return "El convenio del salón no está vigente.";
        return PaymentRules.CannotBuyReason(target, current, totalPasses, status, currentLimit);
    }

    /// <summary>Registra el paquete incluido en el convenio como una orden pagada de $0 y publica el evento.</summary>
    public static Order GrantPlan(Event ev, Plan plan, Venue venue, DateTimeOffset now)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            EventId = ev.Id,
            Event = ev,
            PlanId = plan.Id,
            Plan = plan,
            AmountMxn = 0,
            Source = OrderSource.Agreement,
            VenueId = venue.Id,
            CreatedAt = now,
        };
        PaymentRules.MarkPaid(order, ev, plan, now);
        return order;
    }

    private static TimeZoneInfo FindMexico()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time (Mexico)"); }
    }
}

public static class AccessLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Crea el link y devuelve el token en claro (solo se muestra una vez).</summary>
    public static (AccessLink Link, string Token) New(AccessLinkKind kind, Guid? eventId, Guid? venueId, DateTimeOffset now)
    {
        var token = InvitationTokens.New();
        return (new AccessLink
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            TokenHash = Hash(token),
            EventId = eventId,
            VenueId = venueId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        }, token);
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

    public static string? UnusableReason(AccessLink link, DateTimeOffset now)
    {
        if (link.UsedAt is not null) return "Este link ya se usó.";
        if (now >= link.ExpiresAt) return "Este link ya expiró. Pide uno nuevo.";
        return null;
    }
}
