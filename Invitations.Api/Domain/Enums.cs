namespace Invitations.Api.Domain;

public enum EventType
{
    Wedding,
    Quinceanera,
    Other,
}

public enum EventStatus
{
    /// <summary>Pago pendiente: aún no se puede usar nada del evento.</summary>
    Draft,
    /// <summary>Pagado y publicado: los links de invitación están activos.</summary>
    Published,
    /// <summary>El evento ya pasó; solo lectura.</summary>
    Archived,
}

public enum RsvpStatus
{
    Pending,
    Confirmed,
    Declined,
}

public enum OrderStatus
{
    Pending,
    Paid,
    Failed,
    Refunded,
}

public enum TableShape
{
    Round,
    Rectangle,
    Square,
}

public enum OrderSource
{
    Stripe,
    /// <summary>Paquete incluido en el convenio de un salón (se cobra fuera de la página).</summary>
    Agreement,
}
