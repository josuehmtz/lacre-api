namespace Invitations.Api.Domain;

/// <summary>Reglas de cobro: qué paquete se puede comprar, cuánto cuesta y qué pasa al pagarse.</summary>
public static class PaymentRules
{
    /// <summary>
    /// Motivo por el que no se puede comprar el paquete, o null si sí se puede.
    /// <paramref name="current"/> es el paquete ya pagado (null si el evento es borrador).
    /// </summary>
    /// <paramref name="currentLimit"/> es el límite de pases que ya tiene el evento (puede venir de un paquete a la medida).
    public static string? CannotBuyReason(Plan target, Plan? current, int totalPasses, EventStatus status, int currentLimit = 0)
    {
        if (status == EventStatus.Archived) return "El evento está archivado.";
        if (!target.IsActive) return "Este paquete ya no está disponible.";
        if (target.MaxPasses < totalPasses)
            return $"Tienes {totalPasses} invitados registrados y este paquete es para máximo {target.MaxPasses}.";
        if (current is not null && target.MaxPasses <= current.MaxPasses)
            return current.Id == target.Id ? "Ya tienes este paquete." : "Solo puedes cambiar a un paquete más grande.";
        // Con un paquete a la medida no se vende un paquete que no le da más lugares.
        if (status != EventStatus.Draft && target.MaxPasses <= currentLimit)
            return $"Tu evento ya tiene un paquete a la medida para {currentLimit} invitados.";
        return null;
    }

    /// <summary>Límite máximo que se acepta para un paquete a la medida.</summary>
    public const int MaxCustomPasses = 10_000;

    /// <summary>
    /// Aplica un paquete a la medida acordado y pagado fuera de la app (solo administradores).
    /// Devuelve el motivo si no se puede, o null si quedó aplicado. Publica el evento si era borrador.
    /// </summary>
    public static string? ApplyCustomLimit(Event ev, int maxPasses, int totalPasses, string note)
    {
        if (ev.Status == EventStatus.Archived) return "El evento está archivado.";
        if (string.IsNullOrWhiteSpace(note)) return "Anota lo que se acordó (invitados, precio y cómo se pagó).";
        if (maxPasses < 1 || maxPasses > MaxCustomPasses)
            return $"El límite debe estar entre 1 y {MaxCustomPasses:N0} invitados.";
        if (maxPasses < totalPasses)
            return $"El evento ya tiene {totalPasses} invitados registrados; el límite no puede ser menor.";

        ev.MaxPasses = maxPasses;
        ev.CustomLimitNote = note.Trim();
        if (ev.Status == EventStatus.Draft) ev.Status = EventStatus.Published;
        return null;
    }

    /// <summary>Precio a cobrar: completo la primera vez, o la diferencia al subir de paquete.</summary>
    public static decimal AmountToPay(Plan target, Plan? current) =>
        Math.Max(0, target.PriceMxn - (current?.PriceMxn ?? 0));

    /// <summary>Stripe cobra en centavos.</summary>
    public static long ToCentavos(decimal mxn) => (long)Math.Round(mxn * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Marca la orden como pagada y aplica el paquete al evento. Es idempotente: Stripe puede avisar
    /// del mismo pago varias veces (webhook + regreso del cliente) y solo se aplica una vez.
    /// Devuelve false si la orden ya estaba pagada.
    /// </summary>
    public static bool MarkPaid(Order order, Event ev, Plan plan, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Paid) return false;

        order.Status = OrderStatus.Paid;
        order.PaidAt = now;

        // Nunca bajar de paquete aunque se procesen pagos fuera de orden.
        if (ev.MaxPasses < plan.MaxPasses)
        {
            ev.PlanId = plan.Id;
            ev.MaxPasses = plan.MaxPasses;
        }
        if (ev.Status == EventStatus.Draft) ev.Status = EventStatus.Published;
        return true;
    }

    public static void MarkFailed(Order order)
    {
        if (order.Status == OrderStatus.Pending) order.Status = OrderStatus.Failed;
    }
}
