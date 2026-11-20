using Invitations.Api.Domain;

namespace Invitations.Tests;

public class VenueRulesTests
{
    private static Venue VenueFor(string starts, string ends) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Salón Jardín",
        AgreementStartsOn = DateOnly.Parse(starts),
        AgreementEndsOn = DateOnly.Parse(ends),
    };

    private static readonly Plan Basico = new() { Id = 1, Code = "basico", Name = "Básico", MaxPasses = 100, PriceMxn = 499 };
    private static readonly Plan Estandar = new() { Id = 2, Code = "estandar", Name = "Estándar", MaxPasses = 250, PriceMxn = 899 };

    [Theory]
    // Se cuenta en hora de México (UTC-6): las 23:30 del 31 de dic en México ya son 1 de enero en UTC.
    [InlineData("2026-12-31T23:30:00-06:00", true)]
    [InlineData("2027-01-01T00:30:00-06:00", false)]
    [InlineData("2026-01-01T00:00:00-06:00", true)]
    [InlineData("2025-12-31T23:59:00-06:00", false)]
    public void Agreement_is_active_on_its_first_and_last_local_day(string now, bool expected) =>
        Assert.Equal(expected, VenueRules.AgreementActive(VenueFor("2026-01-01", "2026-12-31"), DateTimeOffset.Parse(now)));

    [Fact]
    public void Indefinite_agreement_never_expires()
    {
        var venue = VenueFor("2026-01-01", "2026-12-31");
        venue.AgreementEndsOn = null;
        Assert.Equal(AgreementStatus.Active, VenueRules.Status(venue, DateTimeOffset.Parse("2040-06-01T12:00:00-06:00")));
        Assert.Equal(AgreementStatus.Upcoming, VenueRules.Status(venue, DateTimeOffset.Parse("2025-12-31T12:00:00-06:00")));
        Assert.Null(VenueRules.ValidateAgreement(venue.AgreementStartsOn, null));
    }

    [Fact]
    public void Terminating_early_stops_the_agreement_until_reactivated()
    {
        var venue = VenueFor("2026-01-01", "2026-12-31");
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00-06:00");

        Assert.Null(VenueRules.Terminate(venue, "  Cerró el salón  ", now));
        Assert.Equal(AgreementStatus.Terminated, VenueRules.Status(venue, now));
        Assert.Equal("Cerró el salón", venue.TerminationReason);
        Assert.NotNull(VenueRules.Terminate(venue, null, now));
        Assert.Equal("El convenio del salón no está vigente.",
            VenueRules.CannotGrantReason(venue, Basico, null, 0, EventStatus.Draft, now));

        VenueRules.Reactivate(venue);
        Assert.Equal(AgreementStatus.Active, VenueRules.Status(venue, now));
        Assert.Null(venue.TerminationReason);
    }

    [Fact]
    public void Agreement_cannot_end_before_it_starts()
    {
        Assert.NotNull(VenueRules.ValidateAgreement(DateOnly.Parse("2026-05-01"), DateOnly.Parse("2026-04-30")));
        Assert.Null(VenueRules.ValidateAgreement(DateOnly.Parse("2026-05-01"), DateOnly.Parse("2026-05-01")));
    }

    [Fact]
    public void Expired_agreement_cannot_grant_plans()
    {
        var venue = VenueFor("2025-01-01", "2025-12-31");
        var reason = VenueRules.CannotGrantReason(venue, Basico, null, 0, EventStatus.Draft,
            DateTimeOffset.Parse("2026-03-01T12:00:00-06:00"));
        Assert.Equal("El convenio del salón no está vigente.", reason);
    }

    [Fact]
    public void Granting_a_plan_publishes_the_event_with_a_zero_cost_agreement_order()
    {
        var venue = VenueFor("2026-01-01", "2026-12-31");
        var ev = new Event { Id = Guid.NewGuid(), VenueId = venue.Id };
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

        var order = VenueRules.GrantPlan(ev, Estandar, venue, now);

        Assert.Equal(EventStatus.Published, ev.Status);
        Assert.Equal(250, ev.MaxPasses);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(OrderSource.Agreement, order.Source);
        Assert.Equal(0, order.AmountMxn);
        Assert.Equal(venue.Id, order.VenueId);
    }

    [Fact]
    public void Agreement_upgrade_follows_the_same_rules_as_paid_upgrades()
    {
        var venue = VenueFor("2026-01-01", "2026-12-31");
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        Assert.Equal("Solo puedes cambiar a un paquete más grande.",
            VenueRules.CannotGrantReason(venue, Basico, Estandar, 50, EventStatus.Published, now));
        Assert.Null(VenueRules.CannotGrantReason(venue, Estandar, Basico, 50, EventStatus.Published, now));
    }
}

public class AccessLinkTests
{
    [Fact]
    public void Only_the_hash_of_the_token_is_stored()
    {
        var (link, token) = AccessLinks.New(AccessLinkKind.EventOwner, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        Assert.NotEqual(token, link.TokenHash);
        Assert.Equal(AccessLinks.Hash(token), link.TokenHash);
    }

    [Fact]
    public void Link_is_single_use_and_expires()
    {
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var (link, _) = AccessLinks.New(AccessLinkKind.VenueMember, null, Guid.NewGuid(), now);

        Assert.Null(AccessLinks.UnusableReason(link, now.AddDays(29)));
        Assert.NotNull(AccessLinks.UnusableReason(link, now.AddDays(31)));

        link.UsedAt = now.AddDays(1);
        Assert.Equal("Este link ya se usó.", AccessLinks.UnusableReason(link, now.AddDays(2)));
    }
}
