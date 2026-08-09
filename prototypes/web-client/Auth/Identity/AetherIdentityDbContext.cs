using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AetherSDR.Web.Auth.Identity;

internal sealed class AetherIdentityDbContext(
    DbContextOptions<AetherIdentityDbContext> options)
    : IdentityDbContext<
        AetherIdentityUser,
        IdentityRole<Guid>,
        Guid,
        IdentityUserClaim<Guid>,
        IdentityUserRole<Guid>,
        IdentityUserLogin<Guid>,
        IdentityRoleClaim<Guid>,
        IdentityUserToken<Guid>,
        IdentityUserPasskey<Guid>>(options)
{
    internal const int CurrentSchemaVersion = 1;

    internal DbSet<AetherIdentitySchemaVersion> IdentitySchemaVersions =>
        Set<AetherIdentitySchemaVersion>();

    internal DbSet<AetherExternalIdentity> ExternalIdentities =>
        Set<AetherExternalIdentity>();

    internal DbSet<AetherAuthenticationSession> AuthenticationSessions =>
        Set<AetherAuthenticationSession>();

    internal DbSet<AetherIdentityAuditRecord> IdentityAuditRecords =>
        Set<AetherIdentityAuditRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureSchemaVersion(builder);
        ConfigureUsers(builder);
        ConfigureExternalIdentities(builder);
        ConfigureAuthenticationSessions(builder);
        ConfigureAuditRecords(builder);
    }

    private static void ConfigureSchemaVersion(ModelBuilder builder)
    {
        builder.Entity<AetherIdentitySchemaVersion>(entity =>
        {
            entity.ToTable("IdentitySchemaVersions");
            entity.HasKey(version => version.Id);
            entity.HasData(new AetherIdentitySchemaVersion
            {
                Id = 1,
                Version = CurrentSchemaVersion
            });
            entity.ToTable(table =>
                table.HasCheckConstraint(
                    "CK_IdentitySchemaVersions_SingleRow",
                    "[Id] = 1 AND [Version] > 0"));
        });
    }

    private static void ConfigureUsers(ModelBuilder builder)
    {
        builder.Entity<AetherIdentityUser>(entity =>
        {
            entity.ToTable("IdentityUsers");
            entity.Property(user => user.DisplayName)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(user => user.Enabled)
                .HasDefaultValue(true);
            entity.Property(user => user.AuthorityVersion)
                .HasDefaultValue(1L);
            entity.ToTable(table =>
                table.HasCheckConstraint(
                    "CK_IdentityUsers_AuthorityVersion",
                    "[AuthorityVersion] > 0"));
        });

        builder.Entity<IdentityRole<Guid>>(entity =>
        {
            entity.ToTable(
                "IdentityRoles",
                table => table.HasCheckConstraint(
                    "CK_IdentityRoles_Name",
                    "[Name] IN ('Aether.Observe', 'Aether.Control', " +
                    "'Aether.Transmit', 'Aether.Admin')"));
            entity.HasData(
                Role("c5da41b7-1f0b-4ee6-95c7-b116880ee4e1", AetherRoles.Observe),
                Role("060735e0-9915-4d3a-b714-c4011d72bdee", AetherRoles.Control),
                Role("387f304a-c996-4b33-982a-9749487057ac", AetherRoles.Transmit),
                Role("cd8c814e-910b-410e-a96f-c42ec63b2cf8", AetherRoles.Admin));
        });
        builder.Entity<IdentityUserRole<Guid>>()
            .ToTable("IdentityUserRoles");
        builder.Entity<IdentityUserClaim<Guid>>()
            .ToTable("IdentityUserClaims");
        builder.Entity<IdentityUserLogin<Guid>>()
            .ToTable("IdentityUserLogins");
        builder.Entity<IdentityRoleClaim<Guid>>()
            .ToTable("IdentityRoleClaims");
        builder.Entity<IdentityUserToken<Guid>>()
            .ToTable("IdentityUserTokens");
        builder.Entity<IdentityUserPasskey<Guid>>()
            .ToTable("IdentityUserPasskeys");
    }

    private static IdentityRole<Guid> Role(string id, string name) =>
        new()
        {
            Id = Guid.Parse(id),
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            ConcurrencyStamp = id
        };

    private static void ConfigureExternalIdentities(ModelBuilder builder)
    {
        builder.Entity<AetherExternalIdentity>(entity =>
        {
            entity.ToTable("ExternalIdentities");
            entity.HasKey(identity => identity.Id);
            entity.Property(identity => identity.ProviderId)
                .HasMaxLength(100)
                .IsRequired();
            entity.Property(identity => identity.Issuer)
                .HasMaxLength(500)
                .IsRequired();
            entity.Property(identity => identity.Subject)
                .HasMaxLength(500)
                .IsRequired();
            entity.HasIndex(identity => new
            {
                identity.ProviderId,
                identity.Issuer,
                identity.Subject
            })
                .IsUnique();
            entity.HasIndex(identity => new
            {
                identity.UserId,
                identity.ProviderId
            })
                .IsUnique();
            entity.HasOne(identity => identity.User)
                .WithMany()
                .HasForeignKey(identity => identity.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAuthenticationSessions(ModelBuilder builder)
    {
        builder.Entity<AetherAuthenticationSession>(entity =>
        {
            entity.ToTable("AuthenticationSessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.ProviderId)
                .HasMaxLength(100);
            entity.Property(session => session.RevocationReason)
                .HasMaxLength(200);
            entity.HasIndex(session => new
            {
                session.UserId,
                session.RevokedAtUtc,
                session.AbsoluteExpiresAtUtc
            });
            entity.HasOne(session => session.User)
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table =>
                table.HasCheckConstraint(
                    "CK_AuthenticationSessions_AuthorityVersion",
                    "[AuthorityVersion] > 0"));
        });
    }

    private static void ConfigureAuditRecords(ModelBuilder builder)
    {
        builder.Entity<AetherIdentityAuditRecord>(entity =>
        {
            entity.ToTable("IdentityAuditRecords");
            entity.HasKey(record => record.Id);
            entity.Property(record => record.Id)
                .ValueGeneratedOnAdd();
            entity.Property(record => record.Action)
                .HasMaxLength(100)
                .IsRequired();
            entity.Property(record => record.CorrelationId)
                .HasMaxLength(100)
                .IsRequired();
            entity.Property(record => record.DetailJson)
                .IsRequired();
            entity.HasIndex(record => record.OccurredAtUtc);
            entity.HasIndex(record => record.ActorUserId);
            entity.HasIndex(record => record.SubjectUserId);
        });
    }
}
