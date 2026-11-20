using Invitations.Api.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Invitations.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// Llaves con las que ASP.NET cifra las cookies de sesión. Se guardan en la base para que sigan
    /// siendo válidas cuando Cloud Run recrea el contenedor (si no, todos perderían su sesión).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventTable> Tables => Set<EventTable>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<StaffAccess> StaffAccesses => Set<StaffAccess>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<VenueMember> VenueMembers => Set<VenueMember>();
    public DbSet<AccessLink> AccessLinks => Set<AccessLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Búsqueda sin acentos ("Gomez" encuentra "Gómez") en el buscador de las hostess.
        b.HasPostgresExtension("unaccent");

        b.Entity<Plan>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.PriceMxn).HasPrecision(10, 2);
            e.HasData(
                // Precios de prueba; los definitivos se cambian con una migración.
                new Plan { Id = 1, Code = "basico", Name = "Básico", MaxPasses = 100, PriceMxn = 499 },
                new Plan { Id = 2, Code = "estandar", Name = "Estándar", MaxPasses = 250, PriceMxn = 899 },
                new Plan { Id = 3, Code = "premium", Name = "Premium", MaxPasses = 500, PriceMxn = 1499 });
        });

        b.Entity<Event>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.TemplateKey).HasMaxLength(50);
            e.Property(x => x.TemplateConfig).HasColumnType("jsonb");
            e.Property(x => x.LayoutConfig).HasColumnType("jsonb");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Owner).WithMany(u => u.Events).HasForeignKey(x => x.OwnerId);
            // Un salón con eventos no se borra (se conserva el historial del convenio).
            e.HasOne(x => x.Venue).WithMany(v => v.Events).HasForeignKey(x => x.VenueId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Venue>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(1000);
        });

        b.Entity<VenueMember>(e =>
        {
            e.HasKey(x => new { x.VenueId, x.UserId });
            e.HasOne(x => x.Venue).WithMany(v => v.Members).HasForeignKey(x => x.VenueId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<AccessLink>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EventTable>(e =>
        {
            e.HasIndex(x => new { x.EventId, x.Number }).IsUnique();
            e.Property(x => x.Shape).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Invitation>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.Property(x => x.Token).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.RsvpStatus).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Guest>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            // El Id lo genera el dominio (RsvpRules.SyncGuests). Sin esto, EF trataría a una persona
            // nueva agregada a invitation.Guests como existente e intentaría un UPDATE.
            e.Property(x => x.Id).ValueGeneratedNever();
            // Si se borra una mesa, sus invitados quedan sin asignar.
            e.HasOne(x => x.Table).WithMany(t => t.Guests)
                .HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<CheckIn>(e =>
        {
            // Se agregan vía invitation.CheckIns con Id ya asignado (ver nota en Guest).
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.StaffAccess).WithMany()
                .HasForeignKey(x => x.StaffAccessId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Order>(e =>
        {
            e.Property(x => x.AmountMxn).HasPrecision(10, 2);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.ProviderSessionId);
            e.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
