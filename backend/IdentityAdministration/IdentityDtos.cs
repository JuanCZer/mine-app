using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Notes.Api.IdentityAdministration;

public sealed record AdminUserSummary(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ManagementVersion);

public sealed record AdminRoleOption(string Code, string Label);

public sealed record AdminCreateUserRequest(string? Username, string? DisplayName, string? Role);

public sealed record AdminCreateUserResponse(AdminUserSummary User, string TemporaryPassword);

public sealed record AdminRoleMutationRequest(
    JsonElement Role,
    JsonElement ExpectedManagementVersion);

public sealed record AdminStatusMutationRequest(
    JsonElement IsActive,
    JsonElement ExpectedManagementVersion);

public enum AdminUserMutationKind
{
    Role,
    Status
}

public sealed record AdminUserMutationIntent(
    Guid TargetUserId,
    long ExpectedManagementVersion,
    string ExpectedManagementVersionText,
    string? Role,
    bool? IsActive)
{
    private static readonly HashSet<string> CanonicalRoles =
        ["operador", "validador", "administrador"];

    public static bool TryCreateRole(
        string targetId,
        AdminRoleMutationRequest? request,
        out AdminUserMutationIntent? intent,
        out IReadOnlyDictionary<string, string[]> fieldErrors) =>
        TryCreate(targetId, request?.ExpectedManagementVersion ?? default,
            request?.Role ?? default, default, AdminUserMutationKind.Role,
            out intent, out fieldErrors);

    public static bool TryCreateStatus(
        string targetId,
        AdminStatusMutationRequest? request,
        out AdminUserMutationIntent? intent,
        out IReadOnlyDictionary<string, string[]> fieldErrors) =>
        TryCreate(targetId, request?.ExpectedManagementVersion ?? default,
            default, request?.IsActive ?? default, AdminUserMutationKind.Status,
            out intent, out fieldErrors);

    private static bool TryCreate(
        string targetId,
        JsonElement versionElement,
        JsonElement roleElement,
        JsonElement statusElement,
        AdminUserMutationKind kind,
        out AdminUserMutationIntent? intent,
        out IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var validTarget = Guid.TryParseExact(targetId, "D", out var target)
            && target != Guid.Empty
            && string.Equals(target.ToString("D"), targetId, StringComparison.Ordinal);
        if (!validTarget)
            errors["id"] = ["El identificador del usuario no es válido."];

        long version = 0;
        var versionText = versionElement.ValueKind == JsonValueKind.String
            ? versionElement.GetString() : null;
        var validVersion = versionText is { Length: > 0 }
            && versionText[0] is >= '1' and <= '9'
            && versionText.All(character => character is >= '0' and <= '9')
            && long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version >= 1;
        if (!validVersion)
        {
            errors["expectedManagementVersion"] =
                ["La versión de administración debe ser un string decimal positivo válido."];
        }

        string? role = null;
        bool? isActive = null;
        if (kind == AdminUserMutationKind.Role)
        {
            role = roleElement.ValueKind == JsonValueKind.String ? roleElement.GetString() : null;
            if (role is null || !CanonicalRoles.Contains(role))
                errors["role"] = ["El rol no es válido."];
        }
        else
        {
            if (statusElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isActive = statusElement.GetBoolean();
            else
                errors["isActive"] = ["El estado debe ser un valor booleano."];
        }

        fieldErrors = errors;
        if (errors.Count != 0)
        {
            intent = null;
            return false;
        }
        intent = new(target, version, versionText!, role, isActive);
        return true;
    }
}

public sealed record AdminUserMutationCommand(
    Guid ActorUserId,
    AdminUserMutationKind Kind,
    AdminUserMutationIntent Intent,
    DateTimeOffset Now,
    string? CorrelationId);

public enum AdminUserMutationOutcomeKind
{
    Updated,
    NoOp,
    NotFound,
    StaleManagementVersion,
    SelfManagementForbidden,
    LastActiveAdministrator
}

public sealed record AdminUserMutationOutcome(
    AdminUserMutationOutcomeKind Kind,
    AdminUserSummary? User = null);

public sealed record AdminCreateUserIntent(
    string Username,
    string NormalizedUsername,
    string DisplayName,
    string Role)
{
    private static readonly HashSet<string> CanonicalRoles =
        ["operador", "validador", "administrador"];

    public static bool TryCreate(
        AdminCreateUserRequest? request,
        ILookupNormalizer normalizer,
        out AdminCreateUserIntent? intent,
        out IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var username = request?.Username?.Trim() ?? string.Empty;
        var displayName = request?.DisplayName?.Trim() ?? string.Empty;
        var role = request?.Role ?? string.Empty;
        if (username.Length is < 3 or > 64
            || username.Any(character => !(character is >= 'A' and <= 'Z'
                || character is >= 'a' and <= 'z'
                || character is >= '0' and <= '9'
                || character is '.' or '_' or '-')))
            errors["username"] = ["El usuario debe tener de 3 a 64 caracteres ASCII válidos."];
        var displayRunes = displayName.EnumerateRunes().ToArray();
        if (displayRunes.Length is < 1 or > 120
            || displayRunes.Any(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control))
            errors["displayName"] = ["El nombre visible debe tener de 1 a 120 caracteres sin controles."];
        if (!CanonicalRoles.Contains(role))
            errors["role"] = ["El rol no es válido."];
        fieldErrors = errors;
        if (errors.Count != 0)
        {
            intent = null;
            return false;
        }
        intent = new(username, normalizer.NormalizeName(username)!, displayName, role);
        return true;
    }

    public byte[] PayloadHash()
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new[] { NormalizedUsername, DisplayName, Role })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }
        return SHA256.HashData(buffer.ToArray());
    }
}

public static class IdentityCreationProtocol
{
    public const string ReceiptProtectorPurpose = "GdsMine.IdentityCreateReceipt.v1";
    public static readonly TimeSpan ReceiptLifetime = TimeSpan.FromMinutes(10);
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";

    public static bool TryParseIdempotencyKey(string? value, out Guid key)
    {
        key = default;
        return value is not null
            && Guid.TryParseExact(value, "D", out key)
            && key != Guid.Empty
            && key.Version == 4
            && value[19] is '8' or '9' or 'a' or 'b'
            && string.Equals(value, key.ToString("D"), StringComparison.Ordinal);
    }

    public static byte[] KeyHash(Guid actor, Guid key) => SHA256.HashData(
        Encoding.UTF8.GetBytes(actor.ToString("D") + ":" + key.ToString("D")));

    public static string GenerateTemporaryPassword()
    {
        Span<char> password = stackalloc char[24];
        for (var index = 0; index < password.Length; index++)
            password[index] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
        return new string(password);
    }

    public static bool IsTemporaryPasswordFormat(string? value) =>
        value is { Length: 24 } && value.All(PasswordAlphabet.Contains);

    public static bool IsNormalizedUsernameConflict(
        string sqlState,
        string? schemaName,
        string? tableName,
        string? constraintName) =>
        string.Equals(sqlState, "23505", StringComparison.Ordinal)
        && string.Equals(schemaName, "public", StringComparison.Ordinal)
        && string.Equals(tableName, "AspNetUsers", StringComparison.Ordinal)
        && string.Equals(constraintName, "UserNameIndex", StringComparison.Ordinal);
}

public enum AdminCreateUserOutcomeKind
{
    Created,
    Replayed,
    UsernameConflict,
    IdempotencyKeyReused,
    IdempotencyResultExpired
}

public sealed record AdminCreateUserCommand(
    Guid ActorUserId,
    Guid IdempotencyKey,
    AdminCreateUserIntent Intent,
    DateTimeOffset Now,
    string? CorrelationId);

public sealed record AdminTemporaryCredential(
    Guid UserId,
    string PasswordHash,
    string TemporaryPassword);

public delegate AdminTemporaryCredential AdminTemporaryCredentialFactory(
    AdminCreateUserIntent intent);

public sealed record AdminCreateUserOutcome(
    AdminCreateUserOutcomeKind Kind,
    int StatusCode = 0,
    byte[]? Body = null);

public sealed record AdminCreateReceiptSnapshot(
    Guid ActorUserId,
    byte[] PayloadHash,
    Guid UserId,
    byte[]? ProtectedResponse,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record AdminCreateUserPreparation(
    AdminTemporaryCredential Credential,
    AdminCreateUserOutcome Outcome,
    byte[] ProtectedResponse,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Owns the exact protected receipt wire protocol used by every
/// IdentityAdministrationStore instance. Persistence and locking remain in the
/// PostgreSQL store; this component makes creation/replay behavior executable
/// without replacing it with a test-only copy.
/// </summary>
public sealed class IdentityCreationReceiptProtocol
{
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector;

    public IdentityCreationReceiptProtocol(IDataProtectionProvider protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector(IdentityCreationProtocol.ReceiptProtectorPurpose);
    }

    public AdminCreateUserPreparation CreateNew(
        AdminCreateUserCommand command,
        AdminTemporaryCredentialFactory credentialFactory)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        var credential = credentialFactory(command.Intent);
        if (credential.UserId == Guid.Empty
            || string.IsNullOrEmpty(credential.PasswordHash)
            || !IdentityCreationProtocol.IsTemporaryPasswordFormat(credential.TemporaryPassword))
            throw new InvalidOperationException("Invalid temporary credential result.");

        var createdAt = ToPostgresTimestamp(command.Now);
        var expiresAt = createdAt.Add(IdentityCreationProtocol.ReceiptLifetime);
        var summary = new AdminUserSummary(
            credential.UserId.ToString("D"), command.Intent.Username, command.Intent.DisplayName,
            command.Intent.Role, true, true, createdAt, createdAt, "1");
        var response = new AdminCreateUserResponse(summary, credential.TemporaryPassword);
        var body = JsonSerializer.SerializeToUtf8Bytes(response, ReceiptJson);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new ProtectedCreateReceipt(
                1, command.ActorUserId, credential.UserId, createdAt, expiresAt,
                StatusCodes.Status201Created, body), ReceiptJson);
        var protectedResponse = _protector.Protect(plaintext);
        return new(
            credential,
            new(AdminCreateUserOutcomeKind.Created, StatusCodes.Status201Created, body),
            protectedResponse,
            createdAt,
            expiresAt);
    }

    public AdminCreateUserOutcome ResolveExisting(
        AdminCreateUserCommand command,
        AdminCreateReceiptSnapshot receipt)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateMetadata(receipt, command);
        if (!receipt.PayloadHash.AsSpan().SequenceEqual(command.Intent.PayloadHash()))
            return new(AdminCreateUserOutcomeKind.IdempotencyKeyReused);
        if (receipt.ProtectedResponse is null || receipt.ExpiresAt <= command.Now)
            return new(AdminCreateUserOutcomeKind.IdempotencyResultExpired);

        ProtectedCreateReceipt protectedReceipt;
        try
        {
            var plaintext = _protector.Unprotect(receipt.ProtectedResponse);
            protectedReceipt = JsonSerializer.Deserialize<ProtectedCreateReceipt>(plaintext, ReceiptJson)
                ?? throw new IdentityAdminUnavailableException();
        }
        catch (CryptographicException error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
        catch (JsonException error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
        ValidateProtectedReceipt(protectedReceipt, receipt, command);
        return new(
            AdminCreateUserOutcomeKind.Replayed,
            protectedReceipt.StatusCode,
            protectedReceipt.Body);
    }

    private static void ValidateMetadata(
        AdminCreateReceiptSnapshot receipt,
        AdminCreateUserCommand command)
    {
        var createdAt = receipt.CreatedAt.ToUniversalTime();
        var expiresAt = receipt.ExpiresAt.ToUniversalTime();
        if (receipt.ActorUserId != command.ActorUserId
            || receipt.UserId == Guid.Empty
            || expiresAt - createdAt != IdentityCreationProtocol.ReceiptLifetime
            || createdAt > command.Now.ToUniversalTime())
            throw new IdentityAdminUnavailableException();
    }

    private static void ValidateProtectedReceipt(
        ProtectedCreateReceipt protectedReceipt,
        AdminCreateReceiptSnapshot databaseReceipt,
        AdminCreateUserCommand command)
    {
        if (protectedReceipt.ProtocolVersion != 1
            || protectedReceipt.ActorUserId != databaseReceipt.ActorUserId
            || protectedReceipt.ActorUserId != command.ActorUserId
            || protectedReceipt.UserId != databaseReceipt.UserId
            || protectedReceipt.CreatedAt.ToUniversalTime() != databaseReceipt.CreatedAt.ToUniversalTime()
            || protectedReceipt.ExpiresAt.ToUniversalTime() != databaseReceipt.ExpiresAt.ToUniversalTime()
            || protectedReceipt.ExpiresAt.ToUniversalTime()
                - protectedReceipt.CreatedAt.ToUniversalTime() != IdentityCreationProtocol.ReceiptLifetime
            || protectedReceipt.StatusCode != StatusCodes.Status201Created
            || protectedReceipt.Body is not { Length: > 0 })
            throw new IdentityAdminUnavailableException();
        AdminCreateUserResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AdminCreateUserResponse>(protectedReceipt.Body, ReceiptJson);
        }
        catch (JsonException error)
        {
            throw new IdentityAdminUnavailableException(error);
        }
        if (response?.User is null
            || !Guid.TryParseExact(response.User.Id, "D", out var responseUserId)
            || responseUserId != databaseReceipt.UserId
            || response.User.Username != command.Intent.Username
            || response.User.DisplayName != command.Intent.DisplayName
            || response.User.Role != command.Intent.Role
            || !response.User.IsActive
            || !response.User.MustChangePassword
            || response.User.CreatedAt.ToUniversalTime() != databaseReceipt.CreatedAt.ToUniversalTime()
            || response.User.UpdatedAt.ToUniversalTime() != databaseReceipt.CreatedAt.ToUniversalTime()
            || response.User.ManagementVersion != "1"
            || !IdentityCreationProtocol.IsTemporaryPasswordFormat(response.TemporaryPassword))
            throw new IdentityAdminUnavailableException();
    }

    private static DateTimeOffset ToPostgresTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    private sealed record ProtectedCreateReceipt(
        int ProtocolVersion,
        Guid ActorUserId,
        Guid UserId,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int StatusCode,
        byte[] Body);
}

public sealed record AdminUserDirectoryResponse(
    IReadOnlyList<AdminUserSummary> Items,
    int Page,
    int PageSize,
    long Total);

public sealed record AdminUserDirectoryAudit(
    Guid ActorUserId,
    string EntityType,
    string EntityId,
    string? CorrelationId,
    AdminUserDirectoryQuery Query);

public sealed record AdminUserDirectoryQuery(
    string? Search,
    string? Role,
    string? Status,
    int Page,
    int PageSize)
{
    private static readonly HashSet<string> CanonicalRoles =
        ["operador", "validador", "administrador"];
    private static readonly HashSet<string> CanonicalStatuses = ["active", "inactive"];

    public bool? IsActive => Status switch
    {
        "active" => true,
        "inactive" => false,
        _ => null
    };

    public static bool TryParse(
        string? search,
        string? role,
        string? status,
        string? page,
        string? pageSize,
        out AdminUserDirectoryQuery? query,
        out IReadOnlyDictionary<string, string[]> fieldErrors)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (normalizedSearch is not null && normalizedSearch.EnumerateRunes().Count() > 100)
            errors["q"] = ["La búsqueda admite hasta 100 caracteres."];
        if (role is not null && !CanonicalRoles.Contains(role))
            errors["role"] = ["El rol no es válido."];
        if (status is not null && !CanonicalStatuses.Contains(status))
            errors["status"] = ["El estado no es válido."];

        var parsedPage = ParsePositive(page, 1, int.MaxValue, "page", errors);
        var parsedPageSize = ParsePositive(pageSize, 25, 100, "pageSize", errors);
        fieldErrors = errors;
        if (errors.Count != 0)
        {
            query = null;
            return false;
        }

        query = new(normalizedSearch, role, status, parsedPage, parsedPageSize);
        return true;
    }

    private static int ParsePositive(
        string? value,
        int defaultValue,
        int maximum,
        string field,
        IDictionary<string, string[]> errors)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1 || parsed > maximum)
        {
            errors[field] = [field == "page"
                ? "La página debe ser mayor o igual a 1."
                : "El tamaño de página debe estar entre 1 y 100."];
            return defaultValue;
        }
        return parsed;
    }
}
