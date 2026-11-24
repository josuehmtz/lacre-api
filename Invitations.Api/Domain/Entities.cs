using Microsoft.AspNetCore.Identity;

namespace Invitations.Api.Domain;

/// <summary>Cliente (novios, papás de la quinceañera) o administrador.</summary>
public class AppUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Event> Events { get; set; } = [];
}

/// <summary>Paquete que se vende por evento, limitado por número total de pases (personas).</summary>
public class Plan
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int MaxPasses { get; set; }
    public decimal PriceMxn { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Event
{
    public Guid Id { get; set; }
    /// <summary>Cliente dueño del evento. Null mientras un evento creado por un salón no tiene cliente vinculado.</summary>
    public Guid? OwnerId { get; set; }
    public AppUser? Owner { get; set; }

    /// <summary>Salón con convenio que administra el evento (null si el cliente lo compró directo).</summary>
    public Guid? VenueId { get; set; }
    public Venue? Venue { get; set; }

    public EventType Type { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Draft;
    public string Title { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public string? VenueName { get; set; }
    public string? VenueAddress { get; set; }
    public string? MapsUrl { get; set; }

    public DateTimeOffset? RsvpDeadline { get; set; }

    /// <summary>Clave de la plantilla de React (p. ej. "boda-clasica").</summary>
    public string TemplateKey { get; set; } = "";
    /// <summary>Personalización de la plantilla (colores, textos, fotos, secciones). Columna jsonb.</summary>
    public string TemplateConfig { get; set; } = "{}";
    /// <summary>Plano del salón: tamaño y elementos fijos (pista, escenario). Columna jsonb.</summary>
    public string LayoutConfig { get; set; } = "{}";

    public int? PlanId { get; set; }
    public Plan? Plan { get; set; }
    /// <summary>Límite de pases según el paquete pagado (0 mientras es borrador).</summary>
    public int MaxPasses { get; set; }
    /// <summary>
    /// Paquete a la medida (más invitados que el paquete más grande): lo pone un administrador con lo acordado
    /// y pagado fuera de la app, p. ej. "800 invitados · $3,500 por transferencia". Null si no aplica.
    /// </summary>
    public string? CustomLimitNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Invitation> Invitations { get; set; } = [];
    public List<EventTable> Tables { get; set; } = [];
    public List<StaffAccess> StaffAccesses { get; set; } = [];
    public List<Order> Orders { get; set; } = [];
}

/// <summary>Mesa del salón con su posición en el plano (drag &amp; drop).</summary>
public class EventTable
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int Number { get; set; }
    public string? Label { get; set; }
    public TableShape Shape { get; set; } = TableShape.Round;
    public int Capacity { get; set; } = 10;
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }

    public List<Guest> Guests { get; set; } = [];
}

/// <summary>Una invitación = un link/QR, con N pases (p. ej. "Familia Pérez", 4 pases).</summary>
public class Invitation
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string DisplayName { get; set; } = "";
    public int Passes { get; set; } = 1;
    public string? Phone { get; set; }

    /// <summary>Token aleatorio e impredecible; es lo único que va en el link y en el QR.</summary>
    public string Token { get; set; } = "";

    public RsvpStatus RsvpStatus { get; set; } = RsvpStatus.Pending;
    public int ConfirmedPasses { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public string? Message { get; set; }

    /// <summary>Cuándo se mandó la invitación por WhatsApp (lo marca el cliente; null = sin enviar).</summary>
    public DateTimeOffset? SentAt { get; set; }

    public List<Guest> Guests { get; set; } = [];
    public List<CheckIn> CheckIns { get; set; } = [];
}

/// <summary>Persona dentro de una invitación; se asigna individualmente a una mesa.</summary>
public class Guest
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public Invitation Invitation { get; set; } = null!;

    public string Name { get; set; } = "";
    public Guid? TableId { get; set; }
    public EventTable? Table { get; set; }

    /// <summary>Entrada en la que llegó esta persona (null = no ha llegado). Al deshacer la entrada vuelve a null.</summary>
    public Guid? CheckInId { get; set; }
    public CheckIn? CheckIn { get; set; }
}

/// <summary>Registro de entrada en la puerta (permite llegadas parciales: 3 de 4).</summary>
public class CheckIn
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public Invitation Invitation { get; set; } = null!;

    public int Count { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? StaffAccessId { get; set; }
    public StaffAccess? StaffAccess { get; set; }
}

/// <summary>Acceso de hostess a un solo evento mediante PIN, con expiración.</summary>
public class StaffAccess
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Label { get; set; } = "";
    public string PinHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Intentos de PIN fallidos seguidos; al llegar al máximo se bloquea un rato.</summary>
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public class Order
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int PlanId { get; set; }
    public Plan Plan { get; set; } = null!;
    public decimal AmountMxn { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    /// <summary>Pago con Stripe o paquete incluido en el convenio de un salón.</summary>
    public OrderSource Source { get; set; } = OrderSource.Stripe;
    public Guid? VenueId { get; set; }
    /// <summary>Id de la sesión de checkout del proveedor de pagos (Stripe).</summary>
    public string? ProviderSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}
