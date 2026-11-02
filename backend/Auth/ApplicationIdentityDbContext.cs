using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Notes.Api.Auth;

// Storage mapping only: backend/Database/apply.sql owns schema creation and changes.
public sealed class ApplicationIdentityDbContext(DbContextOptions<ApplicationIdentityDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("public");
        builder.Entity<ApplicationUser>(user =>
        {
            user.ToTable("AspNetUsers", "public");
            user.Property(x => x.Id).ValueGeneratedNever();
            user.Property(x => x.UserName).HasMaxLength(256);
            user.Property(x => x.NormalizedUserName).HasMaxLength(256);
            user.Property(x => x.Email).HasMaxLength(256);
            user.Property(x => x.NormalizedEmail).HasMaxLength(256);
            user.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(x => x.IsActive).IsRequired();
            user.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").IsRequired();
            user.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").IsRequired();
            user.Property(x => x.MustChangePassword).IsRequired();
            user.Property(x => x.PasswordChangedAt).HasColumnType("timestamp with time zone");
            user.Property(x => x.ManagementVersion).IsRequired();
            user.Property(x => x.LockoutEnd).HasColumnType("timestamp with time zone");
            user.HasIndex(x => x.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique().HasFilter("\"NormalizedUserName\" IS NOT NULL");
        });
        builder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable("AspNetRoles", "public");
            role.Property(x => x.Id).ValueGeneratedNever();
            role.Property(x => x.Name).HasMaxLength(256);
            role.Property(x => x.NormalizedName).HasMaxLength(256);
            role.HasIndex(x => x.NormalizedName).HasDatabaseName("RoleNameIndex").IsUnique().HasFilter("\"NormalizedName\" IS NOT NULL");
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("AspNetUserClaims", "public");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("AspNetRoleClaims", "public");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("AspNetUserRoles", "public");
        builder.Entity<IdentityUserLogin<Guid>>(login =>
        {
            login.ToTable("AspNetUserLogins", "public");
            login.Property(x => x.LoginProvider).HasMaxLength(128);
            login.Property(x => x.ProviderKey).HasMaxLength(128);
        });
        builder.Entity<IdentityUserToken<Guid>>(token =>
        {
            token.ToTable("AspNetUserTokens", "public");
            token.Property(x => x.LoginProvider).HasMaxLength(128);
            token.Property(x => x.Name).HasMaxLength(128);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>().Where(e => e.State is EntityState.Added or EntityState.Modified))
            entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
        return base.SaveChangesAsync(cancellationToken);
    }
}
