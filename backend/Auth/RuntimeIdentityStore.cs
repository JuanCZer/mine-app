using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Notes.Api.Auth;

// Runtime writes bypass UserStore.UpdateAsync, which marks every account column
// modified. Administrative provisioning keeps the full UserManager/EF store.
public sealed class RuntimeIdentityStore(
    ApplicationIdentityDbContext database,
    IOptions<IdentityOptions> options)
{
    public Task<ApplicationUser?> FindByNameAsync(string normalizedName) =>
        database.Users.AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedUserName == normalizedName);

    public async Task<bool> RecordFailedAccessAsync(ApplicationUser user)
    {
        var maximumFailures = options.Value.Lockout.MaxFailedAccessAttempts;
        var now = DateTimeOffset.UtcNow;
        if (!user.IsActive || (user.LockoutEnabled && user.LockoutEnd >= now)) return false;
        var lockoutEnd = now.Add(options.Value.Lockout.DefaultLockoutTimeSpan);
        await using var transaction = await database.Database.BeginTransactionAsync();
        var updated = await database.Users
            .Where(x => x.Id == user.Id && x.IsActive &&
                x.LockoutEnabled == user.LockoutEnabled && x.LockoutEnd == user.LockoutEnd)
            .ExecuteUpdateAsync(set => set
            .SetProperty(x => x.AccessFailedCount, x => x.AccessFailedCount + 1)
            .SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid().ToString())
            .SetProperty(x => x.UpdatedAt, now));
        if (updated != 1)
        {
            await transaction.RollbackAsync();
            return false;
        }
        if (user.LockoutEnabled)
        {
            await database.Users
                .Where(x => x.Id == user.Id && x.IsActive && x.LockoutEnabled &&
                    x.LockoutEnd == user.LockoutEnd && x.AccessFailedCount >= maximumFailures)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.AccessFailedCount, 0)
                    .SetProperty(x => x.LockoutEnd, lockoutEnd)
                    .SetProperty(x => x.ConcurrencyStamp, Guid.NewGuid().ToString())
                    .SetProperty(x => x.UpdatedAt, now));
        }
        await transaction.CommitAsync();
        return true;
    }

    public async Task<bool> CompleteSignInAsync(ApplicationUser user, string? upgradedHash)
    {
        var stamp = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var query = Current(user);
        var updated = upgradedHash is null
            ? await query.ExecuteUpdateAsync(set => set
                .SetProperty(x => x.AccessFailedCount, 0)
                .SetProperty(x => x.ConcurrencyStamp, stamp)
                .SetProperty(x => x.UpdatedAt, now))
            : await query.ExecuteUpdateAsync(set => set
                .SetProperty(x => x.AccessFailedCount, 0)
                .SetProperty(x => x.PasswordHash, upgradedHash)
                .SetProperty(x => x.ConcurrencyStamp, stamp)
                .SetProperty(x => x.UpdatedAt, now));
        if (updated != 1) return false;
        user.AccessFailedCount = 0;
        if (upgradedHash is not null) user.PasswordHash = upgradedHash;
        user.ConcurrencyStamp = stamp;
        user.UpdatedAt = now;
        return true;
    }

    // Task 2's global logout must call this path, not UpdateSecurityStampAsync:
    // the latter delegates to the administrative full-entity EF store.
    public async Task<bool> RotateSecurityStampAsync(ApplicationUser user)
    {
        var securityStamp = Guid.NewGuid().ToString();
        var concurrencyStamp = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var updated = await Current(user).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.SecurityStamp, securityStamp)
            .SetProperty(x => x.ConcurrencyStamp, concurrencyStamp)
            .SetProperty(x => x.UpdatedAt, now));
        if (updated != 1) return false;
        user.SecurityStamp = securityStamp;
        user.ConcurrencyStamp = concurrencyStamp;
        user.UpdatedAt = now;
        return true;
    }

    // Match the snapshot before any write: a concurrent administrative update,
    // deactivation or logout cannot be overwritten by a stale login request.
    private IQueryable<ApplicationUser> Current(ApplicationUser user) =>
        database.Users.Where(x => x.Id == user.Id && x.IsActive && x.ConcurrencyStamp == user.ConcurrencyStamp);
}
