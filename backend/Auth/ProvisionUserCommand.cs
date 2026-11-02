using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Notes.Api.Auth;

// Local administrative command only; never expose this through a public endpoint.
public sealed class ProvisionUserCommand(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    ApplicationIdentityDbContext database)
{
    public static readonly IReadOnlyList<string> MinimumRoles = Array.AsReadOnly(new[] { "operador", "validador", "administrador" });

    public async Task<int> RunAsync(string[] args, TextReader input, TextWriter output)
    {
        var bootstrap = args.Contains("--bootstrap", StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var effective = bootstrap ? args.Where(arg => !string.Equals(arg, "--bootstrap", StringComparison.Ordinal)).ToArray() : args;
        for (var index = 0; index < effective.Length; index += 2)
        {
            if (index + 1 >= effective.Length || effective[index] is not ("--username" or "--display-name" or "--role") || !values.TryAdd(effective[index], effective[index + 1]))
                return await Fail(output, "Argumentos inválidos. Use --username, --display-name y --role; nunca pase la contraseña como argumento.");
        }
        if (!values.TryGetValue("--username", out var username) || string.IsNullOrWhiteSpace(username) || username.Trim().Length > 256 ||
            !values.TryGetValue("--display-name", out var displayName) || string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200 ||
            !values.TryGetValue("--role", out var role) || !MinimumRoles.Contains(role))
            return await Fail(output, "Usuario, nombre visible y un rol explícito permitido son obligatorios.");
        if (bootstrap && !string.Equals(role, "administrador", StringComparison.Ordinal))
            return await Fail(output, "El bootstrap solo admite el rol administrador.");
        if (bootstrap && await database.Users.AnyAsync())
            return await Fail(output, "El bootstrap solo se permite cuando la base no tiene usuarios.");

        var password = Environment.GetEnvironmentVariable("GDS_PROVISION_PASSWORD");
        Environment.SetEnvironmentVariable("GDS_PROVISION_PASSWORD", null);
        if (string.IsNullOrEmpty(password)) password = await input.ReadLineAsync();
        if (string.IsNullOrEmpty(password)) return await Fail(output, "Debe proporcionar una contraseña por entrada estándar o GDS_PROVISION_PASSWORD.");
        if (!PasswordInputPolicy.HasAllowedLength(password))
            return await Fail(output, "La contraseña excede el máximo permitido.");

        // Roles, account, and explicit membership succeed or roll back together.
        await using var transaction = await database.Database.BeginTransactionAsync();
        foreach (var name in MinimumRoles)
        {
            if (await roles.RoleExistsAsync(name)) continue;
            var created = await roles.CreateAsync(new IdentityRole<Guid>(name) { Id = Guid.NewGuid() });
            if (!created.Succeeded) return await Fail(output, "No se pudieron preparar los roles.");
        }
        var user = new ApplicationUser { UserName = username.Trim(), DisplayName = displayName.Trim(), IsActive = true, MustChangePassword = false, PasswordChangedAt = DateTimeOffset.UtcNow, ManagementVersion = 1 };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            return await Fail(output, "No se pudo crear la cuenta: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        result = await users.AddToRoleAsync(user, role);
        if (!result.Succeeded) return await Fail(output, "No se pudo asignar el rol explícito.");
        await transaction.CommitAsync();
        await output.WriteLineAsync($"Cuenta creada: {user.Id}; rol: {role}.");
        return 0;
    }

    private static async Task<int> Fail(TextWriter output, string message)
    {
        await output.WriteLineAsync(message);
        return 1;
    }
}
