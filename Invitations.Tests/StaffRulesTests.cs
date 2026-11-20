using Invitations.Api.Domain;

namespace Invitations.Tests;

public class StaffRulesTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 12, 18, 0, 0, TimeSpan.Zero);

    private static StaffAccess NewAccess() => new() { Id = Guid.NewGuid(), ExpiresAt = Now.AddDays(1) };

    [Fact]
    public void NewPin_is_six_digits()
    {
        for (var i = 0; i < 200; i++)
            Assert.Matches("^[0-9]{6}$", StaffRules.NewPin());
    }

    [Fact]
    public void Access_expires_one_day_after_event_starts() =>
        Assert.Equal(Now.AddDays(1), StaffRules.ExpirationFor(new Event { StartsAt = Now }));

    [Fact]
    public void Expired_access_cannot_login()
    {
        var access = NewAccess();
        access.ExpiresAt = Now.AddMinutes(-1);
        Assert.Equal(StaffRules.LoginBlock.Expired, StaffRules.CheckCanLogin(access, Now));
    }

    [Fact]
    public void Locks_after_max_failed_attempts_and_unlocks_after_duration()
    {
        var access = NewAccess();
        for (var i = 0; i < StaffRules.MaxFailedAttempts - 1; i++)
            StaffRules.RegisterFailure(access, Now);
        Assert.Equal(StaffRules.LoginBlock.None, StaffRules.CheckCanLogin(access, Now));

        StaffRules.RegisterFailure(access, Now);
        Assert.Equal(StaffRules.LoginBlock.Locked, StaffRules.CheckCanLogin(access, Now));
        Assert.Equal(StaffRules.LoginBlock.None, StaffRules.CheckCanLogin(access, Now + StaffRules.LockoutDuration));
    }

    [Fact]
    public void Failure_after_lockout_ended_starts_counting_again()
    {
        var access = NewAccess();
        for (var i = 0; i < StaffRules.MaxFailedAttempts; i++)
            StaffRules.RegisterFailure(access, Now);

        StaffRules.RegisterFailure(access, Now + StaffRules.LockoutDuration + TimeSpan.FromSeconds(1));
        Assert.Equal(1, access.FailedAttempts);
        Assert.Null(access.LockedUntil);
    }

    [Fact]
    public void Success_resets_failures()
    {
        var access = NewAccess();
        StaffRules.RegisterFailure(access, Now);
        StaffRules.RegisterSuccess(access);
        Assert.Equal(0, access.FailedAttempts);
    }
}

public class CheckInRulesTests
{
    private static Invitation NewInvitation(int passes, RsvpStatus status = RsvpStatus.Confirmed, int confirmed = 0, params int[] checkIns)
    {
        var inv = new Invitation { Id = Guid.NewGuid(), Passes = passes, RsvpStatus = status, ConfirmedPasses = confirmed };
        foreach (var c in checkIns) inv.CheckIns.Add(new CheckIn { Id = Guid.NewGuid(), Count = c });
        return inv;
    }

    [Fact]
    public void Remaining_subtracts_previous_check_ins() =>
        Assert.Equal(1, CheckInRules.Remaining(NewInvitation(4, RsvpStatus.Confirmed, 4, 2, 1)));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Validate_allows_up_to_remaining_passes(int count, bool valid) =>
        Assert.Equal(valid, CheckInRules.Validate(NewInvitation(4, RsvpStatus.Confirmed, 4, 2), count) is null);

    [Fact]
    public void Validate_rejects_when_all_passes_used() =>
        Assert.Contains("Ya entraron", CheckInRules.Validate(NewInvitation(2, RsvpStatus.Confirmed, 2, 2), 1));

    [Fact]
    public void Warns_when_not_confirmed_or_declined()
    {
        Assert.Contains(CheckInRules.Warnings(NewInvitation(3, RsvpStatus.Pending)), w => w.Contains("no confirmó"));
        Assert.Contains(CheckInRules.Warnings(NewInvitation(3, RsvpStatus.Declined)), w => w.Contains("no asistiría"));
    }

    [Fact]
    public void Warns_when_confirmed_passes_already_in()
    {
        Assert.Empty(CheckInRules.Warnings(NewInvitation(4, RsvpStatus.Confirmed, 2, 1)));
        Assert.NotEmpty(CheckInRules.Warnings(NewInvitation(4, RsvpStatus.Confirmed, 2, 2)));
    }

    [Theory]
    [InlineData("https://misinvitaciones.mx/i/Py27Qce-B-I-N7ix8WFUNQ", "Py27Qce-B-I-N7ix8WFUNQ")]
    [InlineData("http://localhost:5173/i/QMR53ovt3q6Z29KE_gUcPw", "QMR53ovt3q6Z29KE_gUcPw")]
    [InlineData("  QMR53ovt3q6Z29KE_gUcPw ", "QMR53ovt3q6Z29KE_gUcPw")]
    [InlineData("https://otro-sitio.com/promo/abc", null)]
    [InlineData("hola", null)]
    [InlineData("", null)]
    public void TokenFromQr_extracts_token_from_invitation_link(string content, string? expected) =>
        Assert.Equal(expected, CheckInRules.TokenFromQr(content));

    [Theory]
    [InlineData(4, new[] { 1 }, 3, CheckInRules.OfflineOutcome.Applied)]
    [InlineData(4, new int[0], 4, CheckInRules.OfflineOutcome.Applied)]
    // Otra puerta ya registró a 3 de 4 sin conexión: las 2 que llegan aquí se guardan pero se marcan.
    [InlineData(4, new[] { 3 }, 2, CheckInRules.OfflineOutcome.Exceeded)]
    [InlineData(4, new[] { 4 }, 1, CheckInRules.OfflineOutcome.Exceeded)]
    [InlineData(4, new int[0], 0, CheckInRules.OfflineOutcome.Invalid)]
    [InlineData(4, new int[0], 5, CheckInRules.OfflineOutcome.Invalid)]
    public void ClassifyOffline_keeps_people_who_already_entered(int passes, int[] previous, int count, CheckInRules.OfflineOutcome expected) =>
        Assert.Equal(expected, CheckInRules.ClassifyOffline(NewInvitation(passes, RsvpStatus.Confirmed, passes, previous), count));

    [Fact]
    public void OfflineTime_keeps_reasonable_phone_time_and_clamps_the_rest()
    {
        var now = new DateTimeOffset(2027, 6, 12, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMinutes(-40), CheckInRules.OfflineTime(now.AddMinutes(-40), now));
        Assert.Equal(now, CheckInRules.OfflineTime(now.AddHours(3), now));
        Assert.Equal(now, CheckInRules.OfflineTime(now.AddDays(-5), now));
    }

    [Fact]
    public void TokenHash_is_lowercase_sha256_hex()
    {
        // Mismo resultado que crypto.subtle.digest('SHA-256') en el navegador.
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", CheckInRules.TokenHash("hello"));
    }

    private static Invitation WithGuests(int passes, int named, params int[] checkIns)
    {
        var inv = NewInvitation(passes, RsvpStatus.Confirmed, named, checkIns);
        for (var i = 0; i < named; i++)
            inv.Guests.Add(new Guest { Id = Guid.NewGuid(), InvitationId = inv.Id, Name = $"Persona {i + 1}" });
        return inv;
    }

    [Fact]
    public void Partial_arrival_by_name_then_the_rest()
    {
        // Familia de 5: a las 6:00 llegan 3, a las 6:30 los otros 2.
        var inv = WithGuests(5, 5);
        var first = inv.Guests.Take(3).Select(g => g.Id).ToList();
        Assert.Null(CheckInRules.ValidateGuests(inv, 3, first));
        Assert.Equal(3, CheckInRules.MarkArrived(inv, Guid.NewGuid(), first));

        var rest = inv.Guests.Where(g => g.CheckInId is null).Select(g => g.Id).ToList();
        Assert.Equal(2, rest.Count);
        Assert.Null(CheckInRules.ValidateGuests(inv, 2, rest));
        // Intentar marcar otra vez a alguien que ya entró.
        Assert.NotNull(CheckInRules.ValidateGuests(inv, 1, [first[0]]));
    }

    [Fact]
    public void ValidateGuests_allows_unnamed_companions_but_not_more_names_than_people()
    {
        var inv = WithGuests(5, 3);
        var named = inv.Guests.Select(g => g.Id).ToList();
        Assert.Null(CheckInRules.ValidateGuests(inv, 5, named));      // 3 con nombre + 2 acompañantes
        Assert.Null(CheckInRules.ValidateGuests(inv, 2, []));         // solo cantidad, como antes
        Assert.NotNull(CheckInRules.ValidateGuests(inv, 2, named));   // 3 marcados pero entran 2
        Assert.NotNull(CheckInRules.ValidateGuests(inv, 2, [named[0], named[0]]));
        Assert.NotNull(CheckInRules.ValidateGuests(inv, 1, [Guid.NewGuid()]));
    }

    [Fact]
    public void MarkArrived_offline_skips_people_another_door_already_marked()
    {
        var inv = WithGuests(4, 4);
        var otherDoor = Guid.NewGuid();
        inv.Guests[0].CheckInId = otherDoor;

        var marked = CheckInRules.MarkArrived(inv, Guid.NewGuid(), [inv.Guests[0].Id, inv.Guests[1].Id, Guid.NewGuid()]);

        Assert.Equal(1, marked);
        Assert.Equal(otherDoor, inv.Guests[0].CheckInId);
        Assert.NotNull(inv.Guests[1].CheckInId);
    }
}
