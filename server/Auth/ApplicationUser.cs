using Microsoft.AspNetCore.Identity;

namespace Notes.Api.Auth;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() { Id = Guid.NewGuid(); }
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public long ManagementVersion { get; set; } = 1;
}
