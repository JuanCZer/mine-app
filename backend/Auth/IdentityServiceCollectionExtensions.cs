using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Notes.Api.Database;
using Notes.Api.IdentityAdministration;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Notes.Api.Auth;

public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddPersistedIdentity(this IServiceCollection services)
    {
        services.AddDbContext<ApplicationIdentityDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<PostgresDataSources>().Primary));
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            // The existing SQL is the seven-table Identity model (no passkeys).
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
            options.Stores.MaxLengthForKeys = 128;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Password.RequiredLength = 15;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<ApplicationIdentityDbContext>();
        // Both administrative and runtime paths use Identity's versioned hasher.
        services.Configure<PasswordHasherOptions>(options =>
        {
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = 210_000;
        });
        services.AddScoped<RuntimeIdentityStore>();
        services.AddScoped<AuthenticationService>();
        services.AddSingleton<IAuthenticationAuditStore, PostgresAuthenticationAuditStore>();
        services.AddScoped<ProvisionUserCommand>();
        services.AddSingleton(provider => IdentitySecurityOptions.BindAndValidate(
            provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<IdentityAdminConnection>();
        services.AddSingleton<IIdentityRateLimitKeyStateReader, PostgresIdentityRateLimitKeyStateReader>();
        services.AddSingleton<IIdentityReadiness, IdentityReadiness>();
        services.AddSingleton<IIdentityRateLimiter, IdentityRateLimiter>();
        services.AddHostedService<IdentityRateLimitCleanupService>();
        services.AddSingleton<IIdentitySecurityAuditSink, PostgresIdentitySecurityAuditSink>();
        services.AddSingleton<IIdentitySecurityAudit>(provider => new IdentitySecurityAudit(
            () => provider.GetRequiredService<IIdentitySecurityAuditSink>(),
            provider.GetRequiredService<ILogger<IdentitySecurityAudit>>()));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AdminStepUpService>();
        services.AddScoped<AdminStepUpFilter>();
        services.AddHostedService<IdentityIdempotencyReceiptCleanupService>();
        return services.AddIdentityAdministrationDirectory();
    }

    public static IServiceCollection AddIdentityAdministrationDirectory(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuthorizationMiddlewareResultHandler, IdentityAuthorizationResultHandler>();
        services.AddSingleton<IIdentityAdministrationStore, IdentityAdministrationStore>();
        services.AddAuthorization(options => options.AddPolicy("AdministerUsers", policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireRole("administrador");
            policy.RequireAssertion(context =>
                context.User.IsInRole("administrador")
                && SessionAuthentication.Public(context.User).Permissions.Contains(
                    "users:provision", StringComparer.Ordinal));
        }));
        return services;
    }
}
