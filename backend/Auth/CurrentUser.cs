using System.Security.Claims;

namespace Notes.Api.Auth;

public static class CurrentUser
{
    public static Guid GetRequiredId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Se requiere una identidad autenticada válida.");
}
