using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Notes.Api.IdentityAdministration;

public sealed record IdentityRateLimitHmacKeyEntry(string KeyId, string SecretBase64);

public sealed class IdentitySecurityConfigurationException()
    : InvalidOperationException("La configuración de seguridad de identidad no es válida.");

public sealed class IdentitySecurityOptions
{
    private static readonly Regex KeyIdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly string[] ForbiddenSelectors =
    [
        "IdentitySecurity:CurrentKeyId", "IdentitySecurity:ActiveKeyId",
        "IdentitySecurity:RateLimitHmacKeyId", "IdentitySecurity:Current", "IdentitySecurity:Active"
    ];

    private IdentitySecurityOptions(
        IReadOnlyList<IdentityRateLimitHmacKeyEntry> entries,
        IReadOnlyDictionary<string, byte[]> secrets)
    {
        RateLimitHmacKeys = entries;
        SecretsByKeyId = secrets;
    }

    public IReadOnlyList<IdentityRateLimitHmacKeyEntry> RateLimitHmacKeys { get; }
    public IReadOnlyDictionary<string, byte[]> SecretsByKeyId { get; }

    public static IdentitySecurityOptions BindAndValidate(IConfiguration configuration)
    {
        if (ForbiddenSelectors.Any(key => configuration[key] is not null))
            throw new IdentitySecurityConfigurationException();
        var entries = configuration.GetSection("IdentitySecurity:RateLimitHmacKeys")
            .Get<IdentityRateLimitHmacKeyEntry?[]>() ?? [];
        return Validate(entries);
    }

    public static IdentitySecurityOptions Validate(IReadOnlyList<IdentityRateLimitHmacKeyEntry?> entries)
    {
        if (entries.Count == 0) throw new IdentitySecurityConfigurationException();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.KeyId) || !identifiers.Add(entry.KeyId))
                throw new IdentitySecurityConfigurationException();
        }

        var ordered = new List<IdentityRateLimitHmacKeyEntry>(entries.Count);
        var secrets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || !KeyIdPattern.IsMatch(entry.KeyId)
                || string.IsNullOrEmpty(entry.SecretBase64)
                || entry.SecretBase64.Any(char.IsWhiteSpace))
                throw new IdentitySecurityConfigurationException();
            byte[] decoded;
            try { decoded = Convert.FromBase64String(entry.SecretBase64); }
            catch (FormatException) { throw new IdentitySecurityConfigurationException(); }
            if (decoded.Length < 32 || !string.Equals(Convert.ToBase64String(decoded), entry.SecretBase64, StringComparison.Ordinal))
                throw new IdentitySecurityConfigurationException();
            ordered.Add(entry);
            secrets.Add(entry.KeyId, decoded);
        }
        return new IdentitySecurityOptions(
            new ReadOnlyCollection<IdentityRateLimitHmacKeyEntry>(ordered),
            new ReadOnlyDictionary<string, byte[]>(secrets));
    }
}

public static class IdentitySecurityStartupValidator
{
    public static IdentitySecurityOptions Validate(IConfiguration configuration, Notes.Api.Database.PostgresDataSources dataSources)
    {
        var options = IdentitySecurityOptions.BindAndValidate(configuration);
        _ = dataSources.IdentityAdmin;
        return options;
    }
}
