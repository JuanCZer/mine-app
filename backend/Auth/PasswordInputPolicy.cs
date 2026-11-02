using System.Text;

namespace Notes.Api.Auth;

public static class PasswordInputPolicy
{
    public const int MaximumLength = 128;

    public static bool HasAllowedLength(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var characters = 0;
        foreach (var _ in password.EnumerateRunes())
            if (++characters > MaximumLength) return false;
        return true;
    }
}
