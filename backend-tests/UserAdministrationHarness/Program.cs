using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Notes.Api.Auth;
using Notes.Api.IdentityAdministration;
using Notes.Api.Database;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using System.Collections.Concurrent;

var scenario = args.FirstOrDefault();
void Check(bool value, string message)
{
    if (!value) throw new Exception("ASSERT: " + message);
}

switch (scenario)
{
    case "identity-admin-real-directory":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: true);
        await using var sources = new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var store = new IdentityAdministrationStore(
            new IdentityAdminConnection(sources), new EphemeralDataProtectionProvider(), TimeProvider.System);
        var literalCases = new[]
        {
            (Query: "%", Username: "alpha.percent%", Version: "9007199254740993"),
            (Query: "_", Username: "bravo_under_", Version: "1"),
            (Query: @"\", Username: @"charlie\slash", Version: "1"),
            (Query: "Ág", Username: "Águila", Version: "1")
        };
        AdminUserDirectoryQuery? filtered = null;
        foreach (var expected in literalCases)
        {
            Check(AdminUserDirectoryQuery.TryParse(expected.Query, null, "active", null, null, out filtered, out _),
                "real directory literal/diacritic filter must parse");
            var match = await store.ListUsersAsync(filtered!, default);
            Check(match.Total == 1 && match.Items.Count == 1
                && match.Items[0].Username == expected.Username
                && match.Items[0].ManagementVersion == expected.Version,
                "real directory must treat slash/wildcards literally, preserve diacritics and stringify bigint");
        }

        var ordered = new List<AdminUserSummary>();
        foreach (var pageNumber in new[] { 1, 2, 3 })
        {
            Check(AdminUserDirectoryQuery.TryParse(null, null, null, pageNumber.ToString(), "3", out var query, out _),
                "real directory page must parse");
            var page = await store.ListUsersAsync(query!, default);
            Check(page.Total == 7 && page.Page == pageNumber && page.PageSize == 3,
                "every page count must remain consistent with the seven-row snapshot");
            ordered.AddRange(page.Items);
        }
        Check(ordered.Count == 7 && ordered.Select(item => item.Username).SequenceEqual(new[]
        {
            "alpha.percent%", "bravo_under_", @"charlie\slash", "Águila", "fresh.admin",
            "tie.first", "tie.second"
        }), "real directory order must be exact NormalizedUserName then UUID, including equal NULL ties");
        var actor = Guid.Parse("00000000-0000-4000-8000-000000000001");
        await store.RecordSuccessfulListAsync(new(actor, "user_directory", "user-directory", " real-directory ", filtered!), default);
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        await using var audit = new Npgsql.NpgsqlCommand("""
            SELECT count(*)
            FROM app.audit_events
            WHERE action='admin.users.list' AND outcome='succeeded'
              AND entity_type='user_directory' AND entity_id='user-directory'
              AND actor_user_id=@actor
              AND changes='{}'::jsonb
              AND NOT security_context ?| ARRAY['items','results','passwordHash','securityStamp','concurrencyStamp']
            """, observer);
        audit.Parameters.AddWithValue("actor", actor);
        Check(Convert.ToInt64(await audit.ExecuteScalarAsync()) == 1,
            "real successful audit must use stable identity and exclude results and secrets");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-role-status":
    {
        var adminId = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var highVersionId = Guid.Parse("00000000-0000-4000-8000-000000000021");
        var roleTargetId = Guid.Parse("00000000-0000-4000-8000-000000000022");
        var statusTargetId = Guid.Parse("00000000-0000-4000-8000-000000000023");
        var raceTargetId = Guid.Parse("00000000-0000-4000-8000-000000000024");
        var rollbackTargetId = Guid.Parse("00000000-0000-4000-8000-000000000025");
        var secondAdminId = Guid.Parse("00000000-0000-4000-8000-000000000026");
        var constraintTargetId = Guid.Parse("00000000-0000-4000-8000-000000000027");
        await using var control = await OpenControlConnection();
        await using (var seed = new Npgsql.NpgsqlCommand("""
            BEGIN;
            SET LOCAL app.actor_user_id='00000000-0000-4000-8000-000000000001';
            INSERT INTO public."AspNetUsers"
                ("Id","UserName","NormalizedUserName","PasswordHash","SecurityStamp",
                 "ConcurrencyStamp","DisplayName","IsActive","CreatedAt","UpdatedAt",
                 "MustChangePassword","PasswordChangedAt","ManagementVersion")
            SELECT value.id, value.name, upper(value.name), 'HASH-' || value.name,
                   'SEC-' || value.name, 'CON-' || value.name, value.name, TRUE,
                   now() - interval '1 day', now() - interval '1 day', FALSE, now() - interval '1 day',
                   value.version
            FROM (VALUES
                ('00000000-0000-4000-8000-000000000021'::uuid,'task12.high',9007199254740993::bigint),
                ('00000000-0000-4000-8000-000000000022'::uuid,'task12.role',3::bigint),
                ('00000000-0000-4000-8000-000000000023'::uuid,'task12.status',5::bigint),
                ('00000000-0000-4000-8000-000000000024'::uuid,'task12.race',8::bigint),
                ('00000000-0000-4000-8000-000000000025'::uuid,'task12.rollback',11::bigint),
                ('00000000-0000-4000-8000-000000000026'::uuid,'task12.second.admin',1::bigint),
                ('00000000-0000-4000-8000-000000000027'::uuid,'task12.constraint',15::bigint)
            ) AS value(id,name,version)
            ON CONFLICT ("Id") DO NOTHING;
            INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
            SELECT value.id, role."Id"
            FROM (VALUES
                ('00000000-0000-4000-8000-000000000021'::uuid),
                ('00000000-0000-4000-8000-000000000022'::uuid),
                ('00000000-0000-4000-8000-000000000023'::uuid),
                ('00000000-0000-4000-8000-000000000024'::uuid),
                ('00000000-0000-4000-8000-000000000025'::uuid),
                ('00000000-0000-4000-8000-000000000027'::uuid)
            ) AS value(id)
            CROSS JOIN public."AspNetRoles" AS role
            WHERE role."Name"='operador'
              AND NOT EXISTS (SELECT 1 FROM public."AspNetUserRoles" existing WHERE existing."UserId"=value.id);
            INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
            SELECT '00000000-0000-4000-8000-000000000026', role."Id"
            FROM public."AspNetRoles" AS role
            WHERE role."Name"='administrador'
              AND NOT EXISTS (
                SELECT 1 FROM public."AspNetUserRoles" existing
                WHERE existing."UserId"='00000000-0000-4000-8000-000000000026');
            COMMIT;
            """, control)) await seed.ExecuteNonQueryAsync();

        var configuration = RealPostgresConfiguration(includeIdentityAdmin: true);
        await using var sources = new PostgresDataSources(
            configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var store = new IdentityAdministrationStore(
            new IdentityAdminConnection(sources), new EphemeralDataProtectionProvider(), TimeProvider.System);
        var rateLimiter = new IdentityRateLimiter(
            new IdentityAdminConnection(sources), RateKeys("rate-limit-v1"));
        var now = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);

        AdminUserMutationCommand Role(Guid actor, Guid target, long version, string role) =>
            new(actor, AdminUserMutationKind.Role,
                new(target, version, version.ToString(System.Globalization.CultureInfo.InvariantCulture), role, null),
                now, "task12-real-role");
        AdminUserMutationCommand Status(Guid actor, Guid target, long version, bool active) =>
            new(actor, AdminUserMutationKind.Status,
                new(target, version, version.ToString(System.Globalization.CultureInfo.InvariantCulture), null, active),
                now, "task12-real-status");
        async Task<(string Security, string Concurrency, long Version, bool Active, DateTimeOffset UpdatedAt)> Snapshot(Guid id)
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT "SecurityStamp","ConcurrencyStamp","ManagementVersion","IsActive","UpdatedAt"
                FROM public."AspNetUsers" WHERE "Id"=@id
                """, control);
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync();
            Check(await reader.ReadAsync(), "real mutation snapshot target must exist");
            return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3),
                reader.GetFieldValue<DateTimeOffset>(4));
        }
        async Task<long> AuditCount(Guid id, string? action = null)
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT count(*) FROM app.audit_events
                WHERE entity_type='user' AND entity_id=@id
                  AND (@action IS NULL OR action=@action)
                """, control);
            command.Parameters.AddWithValue("id", id.ToString("D"));
            command.Parameters.Add("action", NpgsqlTypes.NpgsqlDbType.Varchar).Value = action ?? (object)DBNull.Value;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task<bool> AuditIsExact(
            Guid id, string action, string field, string oldValue, string newValue,
            string oldVersion, string newVersion, bool valuesAreBoolean)
        {
            await using var command = new Npgsql.NpgsqlCommand("""
                SELECT count(*) = 1
                FROM app.audit_events
                WHERE entity_type='user' AND entity_id=@id AND action=@action
                  AND outcome='succeeded' AND actor_user_id=@actor
                  AND changes->>'field'=@field
                  AND changes->>'oldValue'=@old_value AND changes->>'newValue'=@new_value
                  AND changes->>'oldManagementVersion'=@old_version
                  AND changes->>'newManagementVersion'=@new_version
                  AND jsonb_typeof(changes->'oldValue')=@value_type
                  AND jsonb_typeof(changes->'newValue')=@value_type
                  AND security_context='{}'::jsonb
                """, control);
            command.Parameters.AddWithValue("id", id.ToString("D"));
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("actor", adminId);
            command.Parameters.AddWithValue("field", field);
            command.Parameters.AddWithValue("old_value", oldValue);
            command.Parameters.AddWithValue("new_value", newValue);
            command.Parameters.AddWithValue("old_version", oldVersion);
            command.Parameters.AddWithValue("new_version", newVersion);
            command.Parameters.AddWithValue("value_type", valuesAreBoolean ? "boolean" : "string");
            return (bool)(await command.ExecuteScalarAsync() ?? false);
        }
        async Task<long> RateAttemptTotal()
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT COALESCE(sum(attempt_count),0) FROM app.identity_rate_limits", control);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        async Task WaitForBlockedLocks(int advisoryWaiters, int otherWaiters, string message)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await using var command = new Npgsql.NpgsqlCommand("""
                    SELECT count(*) FILTER (WHERE locktype='advisory' AND NOT granted),
                           count(*) FILTER (WHERE locktype<>'advisory' AND NOT granted)
                    FROM pg_catalog.pg_locks
                    WHERE database IS NULL OR database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                    """, control);
                await using var reader = await command.ExecuteReaderAsync();
                _ = await reader.ReadAsync();
                if (reader.GetInt64(0) >= advisoryWaiters && reader.GetInt64(1) >= otherWaiters)
                    return;
                await Task.Delay(25);
            }
            throw new Exception("ASSERT: " + message);
        }

        async Task ExpectIntegrityFailure(AdminUserMutationCommand mutation, string message)
        {
            var unavailable = false;
            try { _ = await store.MutateUserAsync(mutation, default); }
            catch (IdentityAdminUnavailableException) { unavailable = true; }
            Check(unavailable, message);
        }

        await using (var corrupt = new Npgsql.NpgsqlCommand("""
            ALTER TABLE public."AspNetUserRoles" DISABLE TRIGGER USER;
            DELETE FROM public."AspNetUserRoles"
            WHERE "UserId"='00000000-0000-4000-8000-000000000021';
            ALTER TABLE public."AspNetUserRoles" ENABLE TRIGGER USER;
            """, control)) await corrupt.ExecuteNonQueryAsync();
        await ExpectIntegrityFailure(Role(adminId, highVersionId, 9007199254740993L, "operador"),
            "an existing target with zero memberships must fail closed instead of looking missing/no-op");
        await using (var restore = new Npgsql.NpgsqlCommand("""
            BEGIN;
            SET LOCAL app.actor_user_id='00000000-0000-4000-8000-000000000001';
            INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
            SELECT '00000000-0000-4000-8000-000000000021', "Id"
            FROM public."AspNetRoles" WHERE "Name"='operador';
            COMMIT;
            """, control)) await restore.ExecuteNonQueryAsync();

        await using (var corrupt = new Npgsql.NpgsqlCommand("""
            ALTER TABLE public."AspNetUserRoles" DISABLE TRIGGER USER;
            INSERT INTO public."AspNetUserRoles" ("UserId","RoleId")
            SELECT '00000000-0000-4000-8000-000000000021', "Id"
            FROM public."AspNetRoles" WHERE "Name"='validador';
            ALTER TABLE public."AspNetUserRoles" ENABLE TRIGGER USER;
            """, control)) await corrupt.ExecuteNonQueryAsync();
        await ExpectIntegrityFailure(Role(adminId, highVersionId, 9007199254740993L, "operador"),
            "an existing target with multiple memberships must fail closed before no-op");
        await using (var restore = new Npgsql.NpgsqlCommand("""
            ALTER TABLE public."AspNetUserRoles" DISABLE TRIGGER USER;
            DELETE FROM public."AspNetUserRoles"
            WHERE "UserId"='00000000-0000-4000-8000-000000000021'
              AND "RoleId"=(SELECT "Id" FROM public."AspNetRoles" WHERE "Name"='validador');
            ALTER TABLE public."AspNetUserRoles" ENABLE TRIGGER USER;
            """, control)) await restore.ExecuteNonQueryAsync();

        await using (var corrupt = new Npgsql.NpgsqlCommand("""
            ALTER TABLE public."AspNetRoles" DISABLE TRIGGER USER;
            UPDATE public."AspNetRoles" SET "NormalizedName"='OPERADOR-CORRUPT'
            WHERE "Name"='operador';
            ALTER TABLE public."AspNetRoles" ENABLE TRIGGER USER;
            """, control)) await corrupt.ExecuteNonQueryAsync();
        await ExpectIntegrityFailure(Role(adminId, highVersionId, 9007199254740993L, "operador"),
            "a membership whose role name pair is not canonical must fail closed");
        await using (var restore = new Npgsql.NpgsqlCommand("""
            ALTER TABLE public."AspNetRoles" DISABLE TRIGGER USER;
            UPDATE public."AspNetRoles" SET "NormalizedName"='OPERADOR' WHERE "Name"='operador';
            ALTER TABLE public."AspNetRoles" ENABLE TRIGGER USER;
            """, control)) await restore.ExecuteNonQueryAsync();

        var noOpBefore = await Snapshot(highVersionId);
        var noOp = await store.MutateUserAsync(
            Role(adminId, highVersionId, 9007199254740993L, "operador"), default);
        var noOpAfter = await Snapshot(highVersionId);
        Check(noOp.Kind == AdminUserMutationOutcomeKind.NoOp
            && noOp.User?.ManagementVersion == "9007199254740993"
            && noOpBefore == noOpAfter && await AuditCount(highVersionId) == 0,
            "real guarded no-op must preserve version, time-equivalent state, both stamps and success audit count");

        Check((await store.MutateUserAsync(
                Role(adminId, Guid.Parse("00000000-0000-4000-8000-000000000099"), 1, "operador"), default)).Kind
                == AdminUserMutationOutcomeKind.NotFound,
            "real missing target must be stable");
        Check((await store.MutateUserAsync(Role(adminId, highVersionId, 1, "operador"), default)).Kind
                == AdminUserMutationOutcomeKind.StaleManagementVersion,
            "real stale target must reject before no-op");
        Check((await store.MutateUserAsync(Role(adminId, adminId, 1, "administrador"), default)).Kind
                == AdminUserMutationOutcomeKind.SelfManagementForbidden,
            "real self guard must run before no-op");
        var mappedGuardBefore = await Snapshot(constraintTargetId);
        foreach (var guard in new[]
        {
            (Message: "identity_self_management_forbidden", Outcome: AdminUserMutationOutcomeKind.SelfManagementForbidden),
            (Message: "identity_last_active_admin", Outcome: AdminUserMutationOutcomeKind.LastActiveAdministrator)
        })
        {
            var installSql = $"""
                CREATE OR REPLACE FUNCTION app.task12_force_guard_error()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
                BEGIN
                    IF NEW."Id"='00000000-0000-4000-8000-000000000027' THEN
                        RAISE EXCEPTION '{guard.Message}' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $body$;
                DROP TRIGGER IF EXISTS trg_task12_force_guard_error ON public."AspNetUsers";
                CREATE TRIGGER trg_task12_force_guard_error
                BEFORE UPDATE ON public."AspNetUsers" FOR EACH ROW
                EXECUTE FUNCTION app.task12_force_guard_error();
                """;
            await using (var install = new Npgsql.NpgsqlCommand(installSql, control))
                await install.ExecuteNonQueryAsync();
            var mapped = await store.MutateUserAsync(
                Status(adminId, constraintTargetId, 15, false), default);
            Check(mapped.Kind == guard.Outcome,
                "known PostgreSQL guard errors must map to their exact stable domain outcome");
            await using var cleanup = new Npgsql.NpgsqlCommand("""
                DROP TRIGGER IF EXISTS trg_task12_force_guard_error ON public."AspNetUsers";
                DROP FUNCTION IF EXISTS app.task12_force_guard_error();
                """, control);
            await cleanup.ExecuteNonQueryAsync();
        }
        Check(await Snapshot(constraintTargetId) == mappedGuardBefore
            && await AuditCount(constraintTargetId) == 0,
            "mapped trigger guard rejections must roll back every target and success-audit change");

        var roleBefore = await Snapshot(roleTargetId);
        var controlConnection = Environment.GetEnvironmentVariable("GDS_TEST_CONTROL_CONNECTION")
            ?? throw new InvalidOperationException("Missing local control connection.");
        var realClock = new MutableTimeProvider(now);
        await using var realHttp = await RealMutationHttpHarness.StartAsync(
            controlConnection, store, rateLimiter, new CaptureRejectionAudit(), realClock);
        using var realClient = new HttpClient { BaseAddress = realHttp.BaseAddress };
        var actorStepUp = await realHttp.IssueStepUpCookieAsync();
        var oldTargetSession = await realHttp.IssueTargetSessionCookieAsync(roleTargetId);
        using (var roleResponse = await SendMutationToTargetAsync(
            realClient, actorStepUp, roleTargetId.ToString("D"), "role",
            "{\"role\":\"validador\",\"expectedManagementVersion\":\"3\"}"))
        {
            Check(roleResponse.StatusCode == HttpStatusCode.OK
                && !roleResponse.Headers.Contains("Set-Cookie"),
                "real HTTP role mutation must not issue or renew actor session/StepUp cookies");
        }
        using (var noOpResponse = await SendMutationToTargetAsync(
            realClient, actorStepUp, roleTargetId.ToString("D"), "role",
            "{\"role\":\"validador\",\"expectedManagementVersion\":\"4\"}"))
        {
            Check(noOpResponse.StatusCode == HttpStatusCode.OK
                && !noOpResponse.Headers.Contains("Set-Cookie"),
                "the same actor StepUp must remain reusable without renewal on a real no-op request");
        }
        using (var probe = new HttpRequestMessage(HttpMethod.Get, "/api/harness/session-probe"))
        {
            probe.Headers.Add("Cookie", $"GDS.Session={oldTargetSession}");
            using var rejectedSession = await realClient.SendAsync(probe);
            Check(rejectedSession.StatusCode == HttpStatusCode.Unauthorized,
                "a target session carrying the pre-mutation SecurityStamp must be rejected after rotation");
        }
        var roleAfter = await Snapshot(roleTargetId);
        Check(roleAfter.Version == 4 && roleAfter.Security != roleBefore.Security
            && roleAfter.Concurrency != roleBefore.Concurrency
            && roleAfter.UpdatedAt == now
            && roleAfter.Security.Length == 64 && roleAfter.Concurrency.Length == 64
            && roleAfter.Security != roleAfter.Concurrency
            && roleAfter.Security.All(Uri.IsHexDigit) && roleAfter.Concurrency.All(Uri.IsHexDigit)
            && await AuditIsExact(roleTargetId, "admin.user.role.change", "role",
                "operador", "validador", "3", "4", false),
            "real role mutation must rotate both random stamps, increment once and commit one atomic audit");

        var statusBefore = await Snapshot(statusTargetId);
        var statusChanged = await store.MutateUserAsync(Status(adminId, statusTargetId, 5, false), default);
        var statusAfter = await Snapshot(statusTargetId);
        Check(statusChanged.Kind == AdminUserMutationOutcomeKind.Updated
            && statusChanged.User is { IsActive: false, ManagementVersion: "6" }
            && statusAfter.Version == 6 && !statusAfter.Active
            && statusAfter.Security != statusBefore.Security
            && statusAfter.Concurrency != statusBefore.Concurrency
            && statusAfter.UpdatedAt == now
            && await AuditIsExact(statusTargetId, "admin.user.status.change", "isActive",
                "true", "false", "5", "6", true),
            "real status mutation must update only state plus management fields and atomic audit");

        var raceBefore = await Snapshot(raceTargetId);
        var sameVersionQuotaBefore = await RateAttemptTotal();
        var sameVersionFirstQuota = await rateLimiter.ConsumeAsync(new(
            "admin-mutation", adminId.ToString("D"), null, now), default);
        var sameVersionSecondQuota = await rateLimiter.ConsumeAsync(new(
            "admin-mutation", adminId.ToString("D"), null, now), default);
        Check(sameVersionFirstQuota.Allowed && sameVersionSecondQuota.Allowed
            && await RateAttemptTotal() == sameVersionQuotaBefore + 2,
            "the real same-version race must persist two independent actor quota attempts before either direct store mutation");
        await using var raceBarrier = await OpenControlConnection();
        await using (var hold = new Npgsql.NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_lock(714205736012345678::bigint)", raceBarrier))
            await hold.ExecuteNonQueryAsync();
        var firstRaceWriter = store.MutateUserAsync(
            Role(adminId, raceTargetId, 8, "validador"), default);
        await WaitForBlockedLocks(1, 0,
            "the first same-version writer did not reach the observable PostgreSQL advisory barrier");
        var secondRaceWriter = store.MutateUserAsync(
            Role(adminId, raceTargetId, 8, "validador"), default);
        await WaitForBlockedLocks(1, 1,
            "the second writer did not block behind the target row held before the advisory lock");
        await using (var release = new Npgsql.NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_unlock(714205736012345678::bigint)", raceBarrier))
            Check((bool)(await release.ExecuteScalarAsync() ?? false),
                "same-version PostgreSQL barrier must release its owned advisory lock");
        var raceResults = await Task.WhenAll(firstRaceWriter, secondRaceWriter);
        var raceAfter = await Snapshot(raceTargetId);
        long raceMembershipCount;
        string raceMembershipRole;
        await using (var raceMembership = new Npgsql.NpgsqlCommand("""
            SELECT count(*), min(role."Name")
            FROM public."AspNetUserRoles" membership
            JOIN public."AspNetRoles" role ON role."Id"=membership."RoleId"
            WHERE membership."UserId"=@target
            """, control))
        {
            raceMembership.Parameters.AddWithValue("target", raceTargetId);
            await using var reader = await raceMembership.ExecuteReaderAsync();
            _ = await reader.ReadAsync();
            raceMembershipCount = reader.GetInt64(0);
            raceMembershipRole = reader.GetString(1);
        }
        Check(raceResults.Count(item => item.Kind == AdminUserMutationOutcomeKind.Updated) == 1
            && raceResults.Count(item => item.Kind == AdminUserMutationOutcomeKind.StaleManagementVersion) == 1
            && await RateAttemptTotal() == sameVersionQuotaBefore + 2
            && raceAfter.Version == 9 && raceAfter.Active && raceAfter.UpdatedAt == now
            && raceAfter.Security != raceBefore.Security
            && raceAfter.Concurrency != raceBefore.Concurrency
            && raceAfter.Security.Length == 64 && raceAfter.Concurrency.Length == 64
            && raceAfter.Security != raceAfter.Concurrency
            && raceMembershipCount == 1 && raceMembershipRole == "validador"
            && await AuditIsExact(raceTargetId, "admin.user.role.change", "role",
                "operador", "validador", "8", "9", false),
            "real same-version race must have two persisted quotas, one writer, one stale response and exact user/membership/audit state");

        var lastAdminQuotaBefore = await RateAttemptTotal();
        var firstQuota = await rateLimiter.ConsumeAsync(new(
            "admin-mutation", adminId.ToString("D"), null, now), default);
        var secondQuota = await rateLimiter.ConsumeAsync(new(
            "admin-mutation", secondAdminId.ToString("D"), null, now), default);
        Check(firstQuota.Allowed && secondQuota.Allowed,
            "both concurrent last-admin attempts must consume their independent actor quotas");
        await using var lastAdminBarrier = await OpenControlConnection();
        await using (var hold = new Npgsql.NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_lock(714205736012345678::bigint)", lastAdminBarrier))
            await hold.ExecuteNonQueryAsync();
        var demoteSecond = store.MutateUserAsync(
            Role(adminId, secondAdminId, 1, "operador"), default);
        var demoteFirst = store.MutateUserAsync(
            Role(secondAdminId, adminId, 1, "operador"), default);
        await WaitForBlockedLocks(2, 0,
            "both last-administrator writers must be observably queued at PostgreSQL before release");
        await using (var release = new Npgsql.NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_unlock(714205736012345678::bigint)", lastAdminBarrier))
            Check((bool)(await release.ExecuteScalarAsync() ?? false),
                "last-administrator PostgreSQL barrier must release its owned advisory lock");
        var lastAdminResults = await Task.WhenAll(demoteSecond, demoteFirst);
        long activeAdminCount;
        long lastAdminAuditCount;
        await using (var finalAdminState = new Npgsql.NpgsqlCommand("""
            SELECT
                (SELECT count(*)
                 FROM public."AspNetUsers" u
                 JOIN public."AspNetUserRoles" ur ON ur."UserId"=u."Id"
                 JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
                 WHERE u."IsActive" AND r."Name"='administrador'
                   AND r."NormalizedName"='ADMINISTRADOR'),
                (SELECT count(*) FROM app.audit_events
                 WHERE action='admin.user.role.change'
                   AND entity_id IN ('00000000-0000-4000-8000-000000000001',
                                     '00000000-0000-4000-8000-000000000026'))
            """, control))
        await using (var reader = await finalAdminState.ExecuteReaderAsync())
        {
            _ = await reader.ReadAsync();
            activeAdminCount = reader.GetInt64(0);
            lastAdminAuditCount = reader.GetInt64(1);
        }
        Check(lastAdminResults.Count(item => item.Kind == AdminUserMutationOutcomeKind.Updated) == 1
            && lastAdminResults.Count(item => item.Kind == AdminUserMutationOutcomeKind.LastActiveAdministrator) == 1
            && activeAdminCount == 1 && lastAdminAuditCount == 1
            && await RateAttemptTotal() == lastAdminQuotaBefore + 2,
            "concurrent demotions must leave exactly one active administrator, one success audit and two committed quota attempts");
        Guid remainingAdminId;
        await using (var remaining = new Npgsql.NpgsqlCommand("""
            SELECT u."Id"
            FROM public."AspNetUsers" u
            JOIN public."AspNetUserRoles" ur ON ur."UserId"=u."Id"
            JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
            WHERE u."IsActive" AND r."Name"='administrador'
              AND r."NormalizedName"='ADMINISTRADOR'
            """, control))
            remainingAdminId = (Guid)(await remaining.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("No active administrator remained."));

        await using (var total = new Npgsql.NpgsqlCommand(
            "SELECT COALESCE(sum(attempt_count),0) FROM app.identity_rate_limits", control))
        {
            var before = Convert.ToInt64(await total.ExecuteScalarAsync());
            var decision = await rateLimiter.ConsumeAsync(new(
                "admin-mutation", remainingAdminId.ToString("D"), null, now), default);
            Check(decision.Allowed, "real admin-mutation quota fixture must allow the attempt");
            var stale = await store.MutateUserAsync(Role(remainingAdminId, raceTargetId, 8, "operador"), default);
            var after = Convert.ToInt64(await total.ExecuteScalarAsync());
            Check(stale.Kind == AdminUserMutationOutcomeKind.StaleManagementVersion && after == before + 1,
                "real quota transaction must remain committed after the later stale mutation transaction");
        }

        var rollbackBefore = await Snapshot(rollbackTargetId);
        var auditFailureQuotaBefore = await RateAttemptTotal();
        Check((await rateLimiter.ConsumeAsync(new(
                "admin-mutation", remainingAdminId.ToString("D"), null, now), default)).Allowed,
            "the audit-failure attempt must commit its quota before the mutation transaction");
        await using (var installFailure = new Npgsql.NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION app.task12_reject_success_audit()
            RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            BEGIN
                IF NEW.action IN ('admin.user.role.change','admin.user.status.change')
                   AND NEW.entity_id='00000000-0000-4000-8000-000000000025' THEN
                    RAISE EXCEPTION 'task12_forced_audit_failure';
                END IF;
                RETURN NEW;
            END $body$;
            DROP TRIGGER IF EXISTS trg_task12_reject_success_audit ON app.audit_events;
            CREATE TRIGGER trg_task12_reject_success_audit
            BEFORE INSERT ON app.audit_events FOR EACH ROW EXECUTE FUNCTION app.task12_reject_success_audit();
            """, control)) await installFailure.ExecuteNonQueryAsync();
        var auditFailed = false;
        try
        {
            _ = await store.MutateUserAsync(Status(remainingAdminId, rollbackTargetId, 11, false), default);
        }
        catch (IdentityAdminUnavailableException) { auditFailed = true; }
        finally
        {
            await using var cleanup = new Npgsql.NpgsqlCommand("""
                DROP TRIGGER IF EXISTS trg_task12_reject_success_audit ON app.audit_events;
                DROP FUNCTION IF EXISTS app.task12_reject_success_audit();
                """, control);
            await cleanup.ExecuteNonQueryAsync();
        }
        var rollbackAfter = await Snapshot(rollbackTargetId);
        Check(auditFailed && rollbackAfter == rollbackBefore
            && await AuditCount(rollbackTargetId, "admin.user.status.change") == 0
            && await RateAttemptTotal() == auditFailureQuotaBefore + 1,
            "real success-audit failure must roll back state/version/stamps/audit while preserving the committed quota");

        var constraintBefore = await Snapshot(constraintTargetId);
        var constraintQuotaBefore = await RateAttemptTotal();
        Check((await rateLimiter.ConsumeAsync(new(
                "admin-mutation", remainingAdminId.ToString("D"), null, now), default)).Allowed,
            "the deferred-constraint failure attempt must commit quota first");
        await using (var installConstraintFailure = new Npgsql.NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION app.task12_reject_immediate_constraints()
            RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            BEGIN
                IF NEW."UserId"='00000000-0000-4000-8000-000000000027' THEN
                    RAISE EXCEPTION 'task12_forced_constraint_failure' USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END $body$;
            DROP TRIGGER IF EXISTS trg_task12_reject_immediate_constraints
                ON public."AspNetUserRoles";
            CREATE CONSTRAINT TRIGGER trg_task12_reject_immediate_constraints
            AFTER INSERT ON public."AspNetUserRoles"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            EXECUTE FUNCTION app.task12_reject_immediate_constraints();
            """, control)) await installConstraintFailure.ExecuteNonQueryAsync();
        var constraintFailed = false;
        try
        {
            _ = await store.MutateUserAsync(
                Role(remainingAdminId, constraintTargetId, 15, "validador"), default);
        }
        catch (IdentityAdminUnavailableException) { constraintFailed = true; }
        finally
        {
            await using var cleanup = new Npgsql.NpgsqlCommand("""
                DROP TRIGGER IF EXISTS trg_task12_reject_immediate_constraints
                    ON public."AspNetUserRoles";
                DROP FUNCTION IF EXISTS app.task12_reject_immediate_constraints();
                """, control);
            await cleanup.ExecuteNonQueryAsync();
        }
        var constraintAfter = await Snapshot(constraintTargetId);
        string constraintRole;
        long constraintMembershipCount;
        await using (var membership = new Npgsql.NpgsqlCommand("""
            SELECT count(*), min(r."Name")
            FROM public."AspNetUserRoles" ur
            JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
            WHERE ur."UserId"='00000000-0000-4000-8000-000000000027'
            """, control))
        await using (var reader = await membership.ExecuteReaderAsync())
        {
            _ = await reader.ReadAsync();
            constraintMembershipCount = reader.GetInt64(0);
            constraintRole = reader.GetString(1);
        }
        Check(constraintFailed && constraintAfter == constraintBefore
            && constraintMembershipCount == 1 && constraintRole == "operador"
            && await AuditCount(constraintTargetId) == 0
            && await RateAttemptTotal() == constraintQuotaBefore + 1,
            "forced SET CONSTRAINTS failure must restore exact membership/state/audit and preserve quota");
        await using (var finalCounts = new Npgsql.NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM (
                 SELECT ur."UserId" FROM public."AspNetUserRoles" ur
                 WHERE ur."UserId" IN (
                   '00000000-0000-4000-8000-000000000021',
                   '00000000-0000-4000-8000-000000000022',
                   '00000000-0000-4000-8000-000000000023',
                   '00000000-0000-4000-8000-000000000024',
                   '00000000-0000-4000-8000-000000000025',
                   '00000000-0000-4000-8000-000000000026',
                   '00000000-0000-4000-8000-000000000027')
                 GROUP BY ur."UserId" HAVING count(*)=1
               ) exact_memberships),
              (SELECT count(*) FROM public."AspNetUsers" u
               JOIN public."AspNetUserRoles" ur ON ur."UserId"=u."Id"
               JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
               WHERE (u."Id"='00000000-0000-4000-8000-000000000021'
                      AND u."ManagementVersion"=9007199254740993 AND r."Name"='operador')
                  OR (u."Id"='00000000-0000-4000-8000-000000000022'
                      AND u."ManagementVersion"=4 AND r."Name"='validador')
                  OR (u."Id"='00000000-0000-4000-8000-000000000023'
                      AND u."ManagementVersion"=6 AND NOT u."IsActive" AND r."Name"='operador')
                  OR (u."Id"='00000000-0000-4000-8000-000000000024'
                      AND u."ManagementVersion"=9 AND r."Name"='validador')
                  OR (u."Id"='00000000-0000-4000-8000-000000000025'
                      AND u."ManagementVersion"=11 AND u."IsActive" AND r."Name"='operador')
                  OR (u."Id"='00000000-0000-4000-8000-000000000027'
                      AND u."ManagementVersion"=15 AND u."IsActive" AND r."Name"='operador')),
              (SELECT count(*) FROM app.audit_events
               WHERE action IN ('admin.user.role.change','admin.user.status.change')
                 AND entity_id IN (
                   '00000000-0000-4000-8000-000000000022',
                   '00000000-0000-4000-8000-000000000023',
                   '00000000-0000-4000-8000-000000000024'))
            """, control))
        await using (var reader = await finalCounts.ExecuteReaderAsync())
        {
            _ = await reader.ReadAsync();
            Check(reader.GetInt64(0) == 7 && reader.GetInt64(1) == 6 && reader.GetInt64(2) == 3,
                "Task 12 final targets must have exact membership, state/version and success-audit counts");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-current-role":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: true);
        await using var sources = new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var factory = new IdentityAdminConnection(sources);
        await using (var unprivileged = await sources.IdentityAdmin.OpenConnectionAsync(default))
        {
            var denied = false;
            try
            {
                await using var managedAccess = new Npgsql.NpgsqlCommand(
                    "SELECT count(\"Id\") FROM public.\"AspNetUsers\"", unprivileged);
                _ = await managedAccess.ExecuteScalarAsync();
            }
            catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.InsufficientPrivilege)
            {
                denied = true;
            }
            Check(denied, "managed access must be denied before SET ROLE");
        }
        for (var checkout = 0; checkout < 2; checkout++)
        {
            await using var connection = await factory.OpenVerifiedAsync(default);
            await using var command = new Npgsql.NpgsqlCommand("SELECT current_role::text", connection);
            Check((string?)await command.ExecuteScalarAsync() == "gds_identity_admin", "every checkout must set and verify current_role");
            await using var managedAccess = new Npgsql.NpgsqlCommand(
                "SELECT count(\"Id\") FROM public.\"AspNetUsers\"", connection);
            Check(Convert.ToInt64(await managedAccess.ExecuteScalarAsync()) >= 0,
                "managed access must succeed after verified SET ROLE");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-no-primary-fallback":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: false);
        await using var sources = new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var failed = false;
        try { await using var ignored = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default); }
        catch (IdentityAdminUnavailableException error)
        {
            failed = true;
            Check(!error.Message.Contains("Host=", StringComparison.OrdinalIgnoreCase), "failure must be opaque");
        }
        Check(failed, "IdentityAdmin must never fall back to Primary");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-missing-retained":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: true, includeHmac: true);
        await using var sources = new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var connectionFactory = new IdentityAdminConnection(sources);
        IIdentityRateLimitKeyStateReader reader = new PostgresIdentityRateLimitKeyStateReader();
        var readiness = new IdentityReadiness(connectionFactory, reader, IdentitySecurityOptions.BindAndValidate(configuration));
        var result = await readiness.CheckAsync(default);
        Check(!result.IsReady && result.Code == "identity_rate_limit_key_missing", "missing retained key must fail readiness");
        Check(result.MissingKeyIds.SequenceEqual(new[] { "rate-limit-v0" }), "readiness must identify only missing retained IDs");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-injected-reader":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: true, includeHmac: true);
        await using var sources = new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var injected = new InjectedKeyStateReader(new("rate-limit-v1", ["rate-limit-v0"]));
        var readiness = new IdentityReadiness(new IdentityAdminConnection(sources), injected, IdentitySecurityOptions.BindAndValidate(configuration));
        var result = await readiness.CheckAsync(default);
        Check(injected.Calls == 1, "CheckAsync must call the injected key-state reader");
        Check(!result.IsReady && result.Code == "identity_rate_limit_key_missing" && result.MissingKeyIds.SequenceEqual(new[]{"rate-limit-v0"}), "injected DB snapshot must own readiness result");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-admin-real-create-receipts":
    {
        var sqlLogProvider = new CaptureLogProvider();
        using var sqlLoggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.None)
            .AddFilter("Npgsql.Command", LogLevel.Debug)
            .AddProvider(sqlLogProvider));
#pragma warning disable CS0618 // Test-only capture for NpgsqlDataSource.Create used by the production owner.
        Npgsql.NpgsqlLoggingConfiguration.InitializeLogging(sqlLoggerFactory, parameterLoggingEnabled: false);
#pragma warning restore CS0618
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        await using var sources = new PostgresDataSources(
            RealPostgresConfiguration(includeIdentityAdmin: true), new HarnessEnvironment(),
            NullLogger<PostgresDataSources>.Instance);
        var provider = new EphemeralDataProtectionProvider();
        var firstStore = new IdentityAdministrationStore(new IdentityAdminConnection(sources), provider,
            clock);
        var secondStore = new IdentityAdministrationStore(new IdentityAdminConnection(sources), provider,
            clock);
        var normalizer = new UpperInvariantLookupNormalizer();
        Check(AdminCreateUserIntent.TryCreate(new("receipt.user", "Receipt User", "administrador"),
            normalizer, out var intent, out _), "real intent must validate");
        var actor = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var key = Guid.Parse("123e4567-e89b-42d3-a456-426614174000");
        var password = IdentityCreationProtocol.GenerateTemporaryPassword();
        var createdUserId = Guid.Parse("10000000-0000-4000-8000-000000000001");
        var createFactoryCalls = 0;
        var created = await firstStore.CreateUserAsync(
            new(actor, key, intent!, now, "real-create"),
            _ =>
            {
                createFactoryCalls++;
                return new(createdUserId, "HASH-ONE", password);
            }, default);
        var replayFactoryCalls = 0;
        var replay = await secondStore.CreateUserAsync(
            new(actor, key, intent!, now, "real-replay"),
            _ =>
            {
                replayFactoryCalls++;
                return new(Guid.NewGuid(), "HASH-MUST-NOT-BE-CREATED", "must-not-be-created");
            }, default);
        Check(created.Kind == AdminCreateUserOutcomeKind.Created
            && created.StatusCode == StatusCodes.Status201Created
            && replay.Kind == AdminCreateUserOutcomeKind.Replayed
            && replay.StatusCode == created.StatusCode
            && created.Body is not null && replay.Body is not null
            && replay.Body.SequenceEqual(created.Body)
            && createFactoryCalls == 1 && replayFactoryCalls == 0,
            "two real stores must decide replay before RNG/hasher and return exact status/body bytes");
        var replayPayload = JsonSerializer.Deserialize<AdminCreateUserResponse>(replay.Body!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(replayPayload is { TemporaryPassword: var replayPassword }
            && replayPassword == password && replayPayload.User.Id == createdUserId.ToString("D"),
            "real replay must reveal the original one-time credential and user only");

        Check(AdminCreateUserIntent.TryCreate(new("receipt.changed", "Changed", "operador"),
            normalizer, out var changed, out _), "changed intent must validate");
        var changedFactoryCalls = 0;
        var reused = await firstStore.CreateUserAsync(new(actor, key, changed!, now, "real-reused"),
            _ =>
            {
                changedFactoryCalls++;
                return new(Guid.NewGuid(), "HASH", "unused-password-value-1");
            }, default);
        Check(reused.Kind == AdminCreateUserOutcomeKind.IdempotencyKeyReused && changedFactoryCalls == 0,
            "changed canonical payload must reject before generating a credential");

        Check(AdminCreateUserIntent.TryCreate(new("actor.isolated", "Actor Isolated", "validador"),
            normalizer, out var isolated, out _), "isolated intent must validate");
        var isolatedResult = await firstStore.CreateUserAsync(
            new(createdUserId, key, isolated!, now, "real-isolated"),
            _ => new(Guid.Parse("10000000-0000-4000-8000-000000000003"), "HASH-THREE",
                IdentityCreationProtocol.GenerateTemporaryPassword()), default);
        Check(isolatedResult.Kind == AdminCreateUserOutcomeKind.Created,
            "same UUID under another actor must create an independent receipt");

        Check(AdminCreateUserIntent.TryCreate(new("expired.user", "Expired User", "operador"),
            normalizer, out var expiredIntent, out _), "expired intent must validate");
        var expiredKey = Guid.Parse("223e4567-e89b-42d3-a456-426614174000");
        var old = now.AddHours(-1);
        var oldCreate = await firstStore.CreateUserAsync(new(actor, expiredKey, expiredIntent!, old, "real-old"),
            _ => new(Guid.Parse("10000000-0000-4000-8000-000000000004"), "HASH-OLD",
                IdentityCreationProtocol.GenerateTemporaryPassword()), default);
        var expiredFactoryCalls = 0;
        var expiredReplay = await secondStore.CreateUserAsync(
            new(actor, expiredKey, expiredIntent!, now, "real-expired"),
            _ =>
            {
                expiredFactoryCalls++;
                return new(Guid.NewGuid(), "HASH-NEW", IdentityCreationProtocol.GenerateTemporaryPassword());
            }, default);
        Check(oldCreate.Kind == AdminCreateUserOutcomeKind.Created
            && expiredReplay.Kind == AdminCreateUserOutcomeKind.IdempotencyResultExpired
            && expiredFactoryCalls == 0,
            "expired result must become a tombstone without generating or duplicating a user");

        await using var control = await OpenControlConnection();

        byte[] initialCiphertext;
        await using (var ciphertextCommand = new Npgsql.NpgsqlCommand("""
            SELECT protected_response FROM app.identity_idempotency_receipts
            WHERE key_sha256=@key
            """, control))
        {
            ciphertextCommand.Parameters.AddWithValue("key", IdentityCreationProtocol.KeyHash(actor, key));
            initialCiphertext = (byte[])(await ciphertextCommand.ExecuteScalarAsync()
                ?? throw new Exception("ASSERT: initial ciphertext must be captured before cleanup"));
        }

        Check(AdminCreateUserIntent.TryCreate(new("tampered.receipt", "Tampered Receipt", "operador"),
            normalizer, out var tamperedIntent, out _), "tampered-receipt intent must validate");
        var tamperedKey = Guid.Parse("723e4567-e89b-42d3-a456-426614174000");
        var tamperedHash = IdentityCreationProtocol.KeyHash(actor, tamperedKey);
        _ = await firstStore.CreateUserAsync(
            new(actor, tamperedKey, tamperedIntent!, now, "tampered-create"),
            _ => new(Guid.Parse("10000000-0000-4000-8000-000000000008"), "HASH-TAMPERED",
                IdentityCreationProtocol.GenerateTemporaryPassword()), default);
        await using (var tamperLifetime = new Npgsql.NpgsqlCommand("""
            UPDATE app.identity_idempotency_receipts
            SET expires_at = created_at + interval '11 minutes'
            WHERE key_sha256=@key
            """, control))
        {
            tamperLifetime.Parameters.AddWithValue("key", tamperedHash);
            Check(await tamperLifetime.ExecuteNonQueryAsync() == 1,
                "control connection must tamper one disposable receipt lifetime");
        }
        var malformedLifetimeFailedClosed = false;
        try
        {
            _ = await secondStore.CreateUserAsync(
                new(actor, tamperedKey, tamperedIntent!, now, "tampered-replay"),
                _ => throw new Exception("receipt validation must happen before credential generation"), default);
        }
        catch (IdentityAdminUnavailableException)
        {
            malformedLifetimeFailedClosed = true;
        }
        Check(malformedLifetimeFailedClosed,
            "receipt metadata with a non-exact absolute ten-minute lifetime must fail closed");

        Check(AdminCreateUserIntent.TryCreate(new("corrupt.receipt", "Corrupt Receipt", "operador"),
            normalizer, out var corruptIntent, out _), "corrupt-receipt intent must validate");
        var corruptKey = Guid.Parse("823e4567-e89b-42d3-a456-426614174000");
        var corruptHash = IdentityCreationProtocol.KeyHash(actor, corruptKey);
        _ = await firstStore.CreateUserAsync(
            new(actor, corruptKey, corruptIntent!, now, "corrupt-create"),
            _ => new(Guid.Parse("10000000-0000-4000-8000-000000000009"), "HASH-CORRUPT",
                IdentityCreationProtocol.GenerateTemporaryPassword()), default);
        await using (var corruptCiphertext = new Npgsql.NpgsqlCommand("""
            UPDATE app.identity_idempotency_receipts
            SET protected_response = decode('00', 'hex')
            WHERE key_sha256=@key
            """, control))
        {
            corruptCiphertext.Parameters.AddWithValue("key", corruptHash);
            Check(await corruptCiphertext.ExecuteNonQueryAsync() == 1,
                "control connection must corrupt one disposable protected response");
        }
        var corruptCiphertextFailedClosed = false;
        try
        {
            _ = await secondStore.CreateUserAsync(
                new(actor, corruptKey, corruptIntent!, now, "corrupt-replay"),
                _ => throw new Exception("receipt validation must happen before credential generation"), default);
        }
        catch (IdentityAdminUnavailableException)
        {
            corruptCiphertextFailedClosed = true;
        }
        Check(corruptCiphertextFailedClosed,
            "invalid protected receipt bytes must fail closed without credential generation");

        Check(AdminCreateUserIntent.TryCreate(new("duplicate.pk", "Duplicate PK", "operador"),
            normalizer, out var duplicatePkIntent, out _), "duplicate-PK intent must validate");
        var nonUsernameUniqueFailedClosed = false;
        try
        {
            _ = await firstStore.CreateUserAsync(
                new(actor, Guid.Parse("323e4567-e89b-42d3-a456-426614174000"), duplicatePkIntent!, now,
                    "non-username-23505"),
                _ => new(actor, "HASH-DUPLICATE", IdentityCreationProtocol.GenerateTemporaryPassword()), default);
        }
        catch (IdentityAdminUnavailableException)
        {
            nonUsernameUniqueFailedClosed = true;
        }
        Check(nonUsernameUniqueFailedClosed,
            "a non-username PostgreSQL unique violation must fail closed, never map to username_conflict");

        async Task AssertCompleteRollbackAsync(
            string label,
            string username,
            Guid userId,
            Guid rollbackKey,
            string installFailureSql,
            string removeFailureSql)
        {
            Check(AdminCreateUserIntent.TryCreate(new(username, label + " rollback", "operador"),
                normalizer, out var rollbackIntent, out _), label + " rollback intent must validate");
            await using (var installFailure = new Npgsql.NpgsqlCommand(installFailureSql, control))
                await installFailure.ExecuteNonQueryAsync();
            var failureObserved = false;
            try
            {
                _ = await firstStore.CreateUserAsync(
                    new(actor, rollbackKey, rollbackIntent!, now, "forced-" + label),
                    _ => new(userId, "HASH-" + label.ToUpperInvariant(),
                        IdentityCreationProtocol.GenerateTemporaryPassword()), default);
            }
            catch (IdentityAdminUnavailableException)
            {
                failureObserved = true;
            }
            finally
            {
                await using var removeFailure = new Npgsql.NpgsqlCommand(removeFailureSql, control);
                await removeFailure.ExecuteNonQueryAsync();
            }

            await using var rollbackInspect = new Npgsql.NpgsqlCommand("""
                SELECT
                    (SELECT count(*) FROM public."AspNetUsers" WHERE "Id"=@user),
                    (SELECT count(*) FROM public."AspNetUserRoles" WHERE "UserId"=@user),
                    (SELECT count(*) FROM app.audit_events WHERE entity_id=@entity),
                    (SELECT count(*) FROM app.identity_idempotency_receipts WHERE key_sha256=@key)
                """, control);
            rollbackInspect.Parameters.AddWithValue("user", userId);
            rollbackInspect.Parameters.AddWithValue("entity", userId.ToString("D"));
            rollbackInspect.Parameters.AddWithValue("key", IdentityCreationProtocol.KeyHash(actor, rollbackKey));
            await using var rollbackReader = await rollbackInspect.ExecuteReaderAsync();
            Check(failureObserved && await rollbackReader.ReadAsync()
                && Enumerable.Range(0, 4).All(index => rollbackReader.GetInt64(index) == 0),
                label + " failure must roll back user, membership, success audit and receipt");
        }

        var membershipFailureUser = Guid.Parse("10000000-0000-4000-8000-000000000010");
        await AssertCompleteRollbackAsync(
            "membership", "rollback.membership", membershipFailureUser,
            Guid.Parse("923e4567-e89b-42d3-a456-426614174000"),
            $"""
            CREATE OR REPLACE FUNCTION app.task11_force_membership_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'task11 forced membership failure';
            END; $$;
            DROP TRIGGER IF EXISTS task11_force_membership_failure ON public."AspNetUserRoles";
            CREATE TRIGGER task11_force_membership_failure
                BEFORE INSERT ON public."AspNetUserRoles" FOR EACH ROW
                WHEN (NEW."UserId" = '{membershipFailureUser:D}'::uuid)
                EXECUTE FUNCTION app.task11_force_membership_failure();
            """,
            """
            DROP TRIGGER IF EXISTS task11_force_membership_failure ON public."AspNetUserRoles";
            DROP FUNCTION IF EXISTS app.task11_force_membership_failure();
            """);

        var constraintFailureUser = Guid.Parse("10000000-0000-4000-8000-000000000011");
        await AssertCompleteRollbackAsync(
            "deferred-constraint", "rollback.constraint", constraintFailureUser,
            Guid.Parse("a23e4567-e89b-42d3-a456-426614174000"),
            $"""
            CREATE OR REPLACE FUNCTION app.task11_force_deferred_constraint_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'task11 forced deferred constraint failure';
            END; $$;
            DROP TRIGGER IF EXISTS task11_force_deferred_constraint_failure ON public."AspNetUsers";
            CREATE CONSTRAINT TRIGGER task11_force_deferred_constraint_failure
                AFTER INSERT ON public."AspNetUsers"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                WHEN (NEW."Id" = '{constraintFailureUser:D}'::uuid)
                EXECUTE FUNCTION app.task11_force_deferred_constraint_failure();
            """,
            """
            DROP TRIGGER IF EXISTS task11_force_deferred_constraint_failure ON public."AspNetUsers";
            DROP FUNCTION IF EXISTS app.task11_force_deferred_constraint_failure();
            """);

        var auditFailureUser = Guid.Parse("10000000-0000-4000-8000-000000000012");
        await AssertCompleteRollbackAsync(
            "audit", "rollback.audit", auditFailureUser,
            Guid.Parse("b23e4567-e89b-42d3-a456-426614174000"),
            $"""
            CREATE OR REPLACE FUNCTION app.task11_force_audit_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'task11 forced audit failure';
            END; $$;
            DROP TRIGGER IF EXISTS task11_force_audit_failure ON app.audit_events;
            CREATE TRIGGER task11_force_audit_failure
                BEFORE INSERT ON app.audit_events FOR EACH ROW
                WHEN (NEW.entity_id = '{auditFailureUser:D}')
                EXECUTE FUNCTION app.task11_force_audit_failure();
            """,
            """
            DROP TRIGGER IF EXISTS task11_force_audit_failure ON app.audit_events;
            DROP FUNCTION IF EXISTS app.task11_force_audit_failure();
            """);

        var receiptFailureUser = Guid.Parse("10000000-0000-4000-8000-000000000013");
        await AssertCompleteRollbackAsync(
            "receipt-after-audit", "rollback.receipt", receiptFailureUser,
            Guid.Parse("c23e4567-e89b-42d3-a456-426614174000"),
            $"""
            CREATE OR REPLACE FUNCTION app.task11_force_receipt_failure()
            RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                RAISE EXCEPTION 'task11 forced receipt failure';
            END; $$;
            DROP TRIGGER IF EXISTS task11_force_receipt_failure ON app.identity_idempotency_receipts;
            CREATE TRIGGER task11_force_receipt_failure
                BEFORE INSERT ON app.identity_idempotency_receipts FOR EACH ROW
                WHEN (NEW.user_id = '{receiptFailureUser:D}'::uuid)
                EXECUTE FUNCTION app.task11_force_receipt_failure();
            """,
            """
            DROP TRIGGER IF EXISTS task11_force_receipt_failure ON app.identity_idempotency_receipts;
            DROP FUNCTION IF EXISTS app.task11_force_receipt_failure();
            """);

        Check(AdminCreateUserIntent.TryCreate(new("race.user", "Race User", "operador"),
            normalizer, out var raceIntent, out _), "race intent must validate");
        using var raceGate = new Barrier(2);
        AdminTemporaryCredential RaceCredential(Guid id)
        {
            Check(raceGate.SignalAndWait(TimeSpan.FromSeconds(20)),
                "both real store transactions must pass username preflight before insert");
            return new(id, "HASH-" + id.ToString("N"),
                IdentityCreationProtocol.GenerateTemporaryPassword());
        }
        var raceFirst = Task.Run(() => firstStore.CreateUserAsync(
            new(actor, Guid.Parse("523e4567-e89b-42d3-a456-426614174000"), raceIntent!, now, "race-first"),
            _ => RaceCredential(Guid.Parse("10000000-0000-4000-8000-000000000006")), default));
        var raceSecond = Task.Run(() => secondStore.CreateUserAsync(
            new(actor, Guid.Parse("623e4567-e89b-42d3-a456-426614174000"), raceIntent!, now, "race-second"),
            _ => RaceCredential(Guid.Parse("10000000-0000-4000-8000-000000000007")), default));
        var raceOutcomes = await Task.WhenAll(raceFirst, raceSecond);
        Check(raceOutcomes.Count(item => item.Kind == AdminCreateUserOutcomeKind.Created) == 1
            && raceOutcomes.Count(item => item.Kind == AdminCreateUserOutcomeKind.UsernameConflict) == 1,
            "the exact normalized-username 23505 race must map one loser to username_conflict");

        await using (var raceInspect = new Npgsql.NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM public."AspNetUsers" WHERE "NormalizedUserName"='RACE.USER'),
                (SELECT count(*) FROM app.audit_events
                 WHERE action='admin.user.create' AND entity_id IN
                    ('10000000-0000-4000-8000-000000000006','10000000-0000-4000-8000-000000000007')),
                (SELECT count(*) FROM app.identity_idempotency_receipts
                 WHERE user_id IN
                    ('10000000-0000-4000-8000-000000000006','10000000-0000-4000-8000-000000000007'))
            """, control))
        {
            await using var raceReader = await raceInspect.ExecuteReaderAsync();
            Check(await raceReader.ReadAsync()
                && raceReader.GetInt64(0) == 1 && raceReader.GetInt64(1) == 1 && raceReader.GetInt64(2) == 1,
                "the username race must leave exactly one user, success audit and receipt");
        }

        await using (var beforeCleanup = new Npgsql.NpgsqlCommand("""
            SELECT count(*), count(*) FILTER (WHERE protected_response IS NULL)
            FROM app.identity_idempotency_receipts
            """, control))
        await using (var beforeReader = await beforeCleanup.ExecuteReaderAsync())
        {
            Check(await beforeReader.ReadAsync(), "receipt inventory must be readable before cleanup");
            var totalBefore = beforeReader.GetInt64(0);
            var tombstonesBefore = beforeReader.GetInt64(1);
            clock.Advance(TimeSpan.FromMinutes(11));
            Check(await firstStore.TombstoneExpiredReceiptsAsync(1, default) == 1,
                "bounded cleanup must tombstone exactly one eligible receipt");
            await beforeReader.CloseAsync();
            await using var afterCleanup = new Npgsql.NpgsqlCommand("""
                SELECT count(*), count(*) FILTER (WHERE protected_response IS NULL)
                FROM app.identity_idempotency_receipts
                """, control);
            await using var afterReader = await afterCleanup.ExecuteReaderAsync();
            Check(await afterReader.ReadAsync() && afterReader.GetInt64(0) == totalBefore
                && afterReader.GetInt64(1) == tombstonesBefore + 1,
                "cleanup must preserve every receipt row and null one ciphertext per requested batch");
        }

        await using var inspect = new Npgsql.NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE protected_response IS NULL), count(*),
                   bool_or(position(convert_to(@password, 'UTF8')
                       in COALESCE(protected_response, ''::bytea)) > 0),
                   (SELECT count(*) FROM app.audit_events
                    WHERE security_context::text LIKE '%' || @password || '%'
                       OR changes::text LIKE '%' || @password || '%'
                       OR security_context::text LIKE '%' || @key_text || '%'
                       OR changes::text LIKE '%' || @key_text || '%'),
                   (SELECT count(*) FROM public."AspNetUsers"
                    WHERE "PasswordHash"=@password OR "SecurityStamp"=@password
                       OR "ConcurrencyStamp"=@password)
            FROM app.identity_idempotency_receipts
            """, control);
        inspect.Parameters.AddWithValue("password", password);
        inspect.Parameters.AddWithValue("key_text", key.ToString("D"));
        await using var reader = await inspect.ExecuteReaderAsync();
        Check(await reader.ReadAsync() && reader.GetInt64(0) >= 2 && reader.GetInt64(1) >= 4
            && (reader.IsDBNull(2) || !reader.GetBoolean(2))
            && reader.GetInt64(3) == 0 && reader.GetInt64(4) == 0,
            "database receipts/audits must preserve tombstones and contain no plaintext credentials or key UUIDs");
        await reader.CloseAsync();

        var sqlLogs = sqlLogProvider.Text;
        var identityConnection = Environment.GetEnvironmentVariable("GDS_TEST_IDENTITY_ADMIN_CONNECTION")!;
        var connectionPassword = new Npgsql.NpgsqlConnectionStringBuilder(identityConnection).Password;
        Check(sqlLogs.Contains("INSERT INTO public.\"AspNetUsers\"", StringComparison.Ordinal)
            && !new[]
            {
                key.ToString("D"), "receipt.user", "Receipt User", password, "HASH-ONE",
                Convert.ToBase64String(initialCiphertext), identityConnection, connectionPassword
            }.Any(secret => !string.IsNullOrEmpty(secret)
                && sqlLogs.Contains(secret, StringComparison.Ordinal)),
            "captured SQL/log output must prove execution while excluding key, payload, password, hash, ciphertext and connection material");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "identity-model-management-fields":
    {
        var userType = typeof(ApplicationUser);
        var mustChangePassword = userType.GetProperty("MustChangePassword");
        var passwordChangedAt = userType.GetProperty("PasswordChangedAt");
        var managementVersion = userType.GetProperty("ManagementVersion");
        Check(mustChangePassword?.PropertyType == typeof(bool), "MustChangePassword must be a non-nullable bool");
        Check(passwordChangedAt?.PropertyType == typeof(DateTimeOffset?), "PasswordChangedAt must be a nullable DateTimeOffset");
        Check(managementVersion?.PropertyType == typeof(long), "ManagementVersion must be a non-nullable long");

        var options = new DbContextOptionsBuilder<ApplicationIdentityDbContext>()
            .UseSqlite("Data Source=" + args[1]).Options;
        await using var database = new ApplicationIdentityDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var entity = database.Model.FindEntityType(userType)!;
        var table = StoreObjectIdentifier.Table("AspNetUsers", "public");
        foreach (var name in new[] { "MustChangePassword", "PasswordChangedAt", "ManagementVersion" })
        {
            var property = entity.FindProperty(name);
            Check(property is not null, name + " must be mapped by EF");
            Check(property!.GetColumnName(table) == name, name + " must map to the existing SQL column");
        }

        var changedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var user = new ApplicationUser { UserName = "Managed.User", DisplayName = "Managed User" };
        mustChangePassword!.SetValue(user, true);
        passwordChangedAt!.SetValue(user, changedAt);
        managementVersion!.SetValue(user, 7L);
        database.Users.Add(user);
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var saved = await database.Users.SingleAsync(x => x.Id == user.Id);
        Check((bool)mustChangePassword.GetValue(saved)!, "MustChangePassword must round-trip");
        Check((long)managementVersion.GetValue(saved)! == 7L, "ManagementVersion must round-trip without numeric loss");
        Check(((DateTimeOffset?)passwordChangedAt.GetValue(saved))?.ToUnixTimeSeconds() == changedAt.ToUnixTimeSeconds(), "PasswordChangedAt must round-trip");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-key-array-binds-indexed-environment-entries":
    {
        var secret = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentitySecurity:RateLimitHmacKeys:0:KeyId"] = "a",
            ["IdentitySecurity:RateLimitHmacKeys:0:SecretBase64"] = secret,
            ["IdentitySecurity:RateLimitHmacKeys:1:KeyId"] = new string('z', 64),
            ["IdentitySecurity:RateLimitHmacKeys:1:SecretBase64"] = secret
        }).Build();
        var options = IdentitySecurityOptions.BindAndValidate(configuration);
        Check(options.RateLimitHmacKeys.Count == 2, "indexed array must bind every entry");
        Check(options.SecretsByKeyId.Keys.SequenceEqual(new[] { "a", new string('z', 64) }), "binding must preserve array order");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-key-list-rejects-null-incomplete-duplicate-and-invalid-entries":
    {
        var good = Convert.ToBase64String(new byte[32]);
        var badCases = new List<IReadOnlyList<IdentityRateLimitHmacKeyEntry?>>
        {
            Array.Empty<IdentityRateLimitHmacKeyEntry?>(), new IdentityRateLimitHmacKeyEntry?[] { null },
            new IdentityRateLimitHmacKeyEntry?[] { new("", good) }, new IdentityRateLimitHmacKeyEntry?[] { new("key", "") },
            new IdentityRateLimitHmacKeyEntry?[] { new("dup", good), new("dup", good) },
            new IdentityRateLimitHmacKeyEntry?[] { new("Upper", good) }, new IdentityRateLimitHmacKeyEntry?[] { new(new string('a', 65), good) },
            new IdentityRateLimitHmacKeyEntry?[] { new("key", " " + good) }, new IdentityRateLimitHmacKeyEntry?[] { new("key", good.TrimEnd('=')) },
            new IdentityRateLimitHmacKeyEntry?[] { new("key", Convert.ToBase64String(new byte[31])) },
            new IdentityRateLimitHmacKeyEntry?[] { new("key", "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!") }
        };
        foreach (var entries in badCases)
        {
            var failed = false;
            try { _ = IdentitySecurityOptions.Validate(entries); }
            catch (IdentitySecurityConfigurationException) { failed = true; }
            Check(failed, "invalid key list must fail closed");
        }
        foreach (var pair in new[]
        {
            (Id: "a", Secret: good),
            (Id: new string('a', 64), Secret: good),
            (Id: "long-secret", Secret: Convert.ToBase64String(new byte[33]))
        })
            Check(IdentitySecurityOptions.Validate([new(pair.Id, pair.Secret)]).SecretsByKeyId.ContainsKey(pair.Id), "valid key ID and secrets of at least 32 bytes must be accepted");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "configuration-rejects-local-current-or-active-selector":
    {
        foreach (var selector in new[] { "IdentitySecurity:CurrentKeyId", "IdentitySecurity:ActiveKeyId", "IdentitySecurity:RateLimitHmacKeyId" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IdentitySecurity:RateLimitHmacKeys:0:KeyId"] = "rate-limit-v1",
                ["IdentitySecurity:RateLimitHmacKeys:0:SecretBase64"] = Convert.ToBase64String(new byte[32]),
                [selector] = "rate-limit-v1"
            }).Build();
            var failed = false;
            try { _ = IdentitySecurityOptions.BindAndValidate(configuration); }
            catch (IdentitySecurityConfigurationException) { failed = true; }
            Check(failed, "local active/current selectors must be rejected");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "readiness-requires-exactly-one-active-and-every-retained-secret":
    {
        var options = IdentitySecurityOptions.Validate([new("active", Convert.ToBase64String(new byte[32])), new("retained", Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()))]);
        var ready = IdentityReadiness.Evaluate(options, new("active", ["retained"]));
        Check(ready.IsReady && ready.Code == "ready" && ready.MissingKeyIds.Count == 0, "DB-selected active plus retained keys must be ready");
        var missing = IdentityReadiness.Evaluate(IdentitySecurityOptions.Validate([new("active", Convert.ToBase64String(new byte[32]))]), new("active", ["retained"]));
        Check(!missing.IsReady && missing.Code == "identity_rate_limit_key_missing" && missing.MissingKeyIds.SequenceEqual(new[] { "retained" }), "every retained secret is required");
        var absentActive = IdentityReadiness.Evaluate(options, new("", ["retained"]));
        Check(!absentActive.IsReady && absentActive.Code == "identity_rate_limit_active_key_invalid", "exactly one DB active key is required");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "startup-requires-identity-admin-pool-and-complete-hmac-keys":
    {
        var complete = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=runtime;Password=unused;SSL Mode=VerifyFull",
            ["ConnectionStrings:IdentityAdmin"] = "Host=localhost;Database=unused;Username=identity;Password=unused;SSL Mode=VerifyFull",
            ["IdentitySecurity:RateLimitHmacKeys:0:KeyId"] = "rate-limit-v1",
            ["IdentitySecurity:RateLimitHmacKeys:0:SecretBase64"] = Convert.ToBase64String(new byte[32])
        }).Build();
        await using var sources = new PostgresDataSources(complete, new ProductionHarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        IdentitySecurityStartupValidator.Validate(complete, sources);
        foreach (var missing in new[] { "ConnectionStrings:IdentityAdmin", "IdentitySecurity:RateLimitHmacKeys:0:SecretBase64" })
        {
            var values = complete.AsEnumerable().Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value);
            values.Remove(missing);
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var failed = false;
            try
            {
                await using var invalidSources = new PostgresDataSources(invalid, new ProductionHarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
                IdentitySecurityStartupValidator.Validate(invalid, invalidSources);
            }
            catch (InvalidOperationException error)
            {
                failed = true;
                Check(!error.Message.Contains("Host=", StringComparison.OrdinalIgnoreCase), "startup failures must be opaque");
            }
            Check(failed, missing + " must fail before serving");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rejection-record-requires-stable-code-allowlisted-nonempty-entity-rejected-outcome-and-bounded-metadata":
    {
        var valid = new IdentitySecurityRejection(IdentitySecurityActions.AuthorizationReject, "permission_denied", null, "authentication", "unknown", "rejected", "  correlation  ", new Dictionary<string,string>{{"reason","policy"}});
        Check(IdentitySecurityAudit.Normalize(valid).CorrelationId == "correlation", "correlation must be trimmed");
        foreach (var invalid in new[]
        {
            valid with { Action = "unknown" }, valid with { Code = "unknown" }, valid with { EntityType = "body" },
            valid with { EntityId = "" }, valid with { Outcome = "accepted" },
            valid with { Metadata = new Dictionary<string,string>{{"password","secret"}} },
            valid with { Metadata = Enumerable.Range(0, 7).ToDictionary(x => "k" + x, x => "v") }
        })
        {
            var failed = false;
            try { _ = IdentitySecurityAudit.Normalize(invalid); } catch (ArgumentException) { failed = true; }
            Check(failed, "invalid rejection must fail validation");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rejection-audit-persists-exact-top-level-columns-empty-changes-and-security-context-json":
    {
        var normalized = IdentitySecurityAudit.Normalize(new(IdentitySecurityActions.AuthorizationReject, "permission_denied", null, "authentication", "unknown", "rejected", new string('c', 140), new Dictionary<string,string>{{"policy",new string('p', 140)}}));
        var row = IdentitySecurityAudit.CreateRow(normalized);
        Check(row.Action == IdentitySecurityActions.AuthorizationReject && row.Outcome == "rejected" && row.EntityType == "authentication" && row.EntityId == "unknown", "top-level columns must be mapped explicitly");
        Check(row.ChangesJson == "{}", "changes must be exact empty JSON");
        Check(row.CorrelationId?.Length == 128, "correlation must truncate to 128 runes");
        using var json = System.Text.Json.JsonDocument.Parse(row.SecurityContextJson);
        Check(json.RootElement.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[] { "Code", "policy" }), "security_context must contain only Code and allowlisted metadata");
        Check(json.RootElement.GetProperty("policy").GetString()!.Length == 128, "metadata values must truncate to 128 runes");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rejection-audit-writes-in-its-own-transaction":
    {
        var sink = new CaptureAuditSink();
        var audit = new IdentitySecurityAudit(sink, NullLogger<IdentitySecurityAudit>.Instance);
        await audit.RecordRejectionAsync(new(IdentitySecurityActions.AuthorizationReject, "permission_denied", null, "authentication", "unknown", "rejected", null, new Dictionary<string,string>()), default);
        Check(sink.IndependentTransactionWrites == 1, "each rejection must request its own independent transaction");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rejection-audit-failure-preserves-original-rejection":
    {
        var audit = new IdentitySecurityAudit(new CaptureAuditSink { Fail = true }, NullLogger<IdentitySecurityAudit>.Instance);
        await audit.RecordRejectionAsync(new(IdentitySecurityActions.AuthorizationReject, "authentication_required", null, "authentication", "unknown", "rejected", null, new Dictionary<string,string>()), default);
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rejection-audit-never-accepts-body-password-cookie-csrf-or-connection-data":
    {
        foreach (var key in new[] { "body", "password", "cookie", "csrf", "connection", "connectionString" })
        {
            var failed = false;
            try { _ = IdentitySecurityAudit.Normalize(new(IdentitySecurityActions.ValidationReject, "validation_failed", null, "http_route", "admin-users-create", "rejected", null, new Dictionary<string,string>{{key,"secret"}})); }
            catch (ArgumentException) { failed = true; }
            Check(failed, "sensitive metadata key must be rejected");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-attempt-commits-before-service-returns":
    {
        await using var sources = RealRateLimitSources();
        var limiter = CreateRateLimiter(sources, RateKeys("rate-limit-v1"));
        var decision = await limiter.ConsumeAsync(LoginRequest(), default);
        Check(decision.Allowed && decision.Remaining == 9 && decision.RetryAfter is null,
            "first login attempt must be allowed with nine remaining");
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        Check(await RateLimitAttemptTotal(observer) == 1,
            "a second connection must observe the committed attempt before ConsumeAsync returns");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-429-attempt-commits-before-service-returns":
    {
        await using var sources = RealRateLimitSources();
        var limiter = CreateRateLimiter(sources, RateKeys("rate-limit-v1"));
        IdentityRateLimitDecision? decision = null;
        var request = LoginRequest();
        for (var attempt = 0; attempt < 11; attempt++)
            decision = await limiter.ConsumeAsync(request, default);
        Check(decision is { Allowed: false, Remaining: 0, RetryAfter: not null },
            "the eleventh login attempt must be a persisted 429 decision");
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        Check(await RateLimitAttemptTotal(observer) == 11,
            "a second connection must observe the denied attempt before ConsumeAsync returns");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-attempt-survives-synthetic-later-rollback":
    {
        await using var sources = RealRateLimitSources();
        var limiter = CreateRateLimiter(sources, RateKeys("rate-limit-v1"));
        _ = await limiter.ConsumeAsync(LoginRequest(), default);
        await using (var later = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default))
        await using (var transaction = await later.BeginTransactionAsync())
        {
            await using var synthetic = new Npgsql.NpgsqlCommand(
                "UPDATE app.identity_rate_limits SET attempt_count = attempt_count + 100", later, transaction);
            _ = await synthetic.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        Check(await RateLimitAttemptTotal(observer) == 1,
            "a later protected-operation rollback must not undo the limiter's committed attempt");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-database-failure-returns-service-unavailable":
    {
        var configuration = RealPostgresConfiguration(includeIdentityAdmin: false, includeHmac: true);
        await using var sources = new PostgresDataSources(
            configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
        var limiter = CreateRateLimiter(sources, RateKeys("rate-limit-v1"));
        var failedClosed = false;
        try { _ = await limiter.ConsumeAsync(LoginRequest(), default); }
        catch (IdentityRateLimitUnavailableException error)
        {
            failedClosed = true;
            Check(!error.Message.Contains("Host=", StringComparison.OrdinalIgnoreCase),
                "database failures must remain opaque");
        }
        Check(failedClosed, "database failure must produce the service-unavailable domain exception");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "expired-rate-limit-buckets-are-deleted-in-bounded-batches":
    {
        await using var sources = RealRateLimitSources();
        var limiter = CreateRateLimiter(sources, RateKeys("rate-limit-v1"));
        Check(await limiter.DeleteExpiredBatchAsync(2, default) == 2,
            "cleanup must delete exactly the requested bounded batch");
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        await using var count = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM app.identity_rate_limits WHERE expires_at <= now()", observer);
        Check(Convert.ToInt32(await count.ExecuteScalarAsync()) == 1,
            "cleanup must leave expired rows beyond the batch bound");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-rotation-is-shared-ordered-and-fails-closed":
    {
        await using var firstSources = RealRateLimitSources();
        await using var secondSources = RealRateLimitSources();
        var first = CreateRateLimiter(firstSources, RateKeys("rate-limit-v1", "rate-limit-v2"));
        var second = CreateRateLimiter(secondSources, RateKeys("rate-limit-v2", "rate-limit-v1"));
        var request = LoginRequest();
        for (var attempt = 0; attempt < 5; attempt++)
            Check((await first.ConsumeAsync(request, default)).Allowed,
                "pre-rotation attempts must be allowed");

        await using (var control = await OpenControlConnection())
        await using (var transaction = await control.BeginTransactionAsync())
        {
            await using var rotate = new Npgsql.NpgsqlCommand("""
                INSERT INTO app.identity_rate_limit_key_state (key_id, state, changed_at)
                VALUES ('rate-limit-v2', 'accepted', @flip)
                ON CONFLICT (key_id) DO UPDATE SET state='accepted', changed_at=EXCLUDED.changed_at;
                UPDATE app.identity_rate_limit_key_state
                SET state='retained', changed_at=@flip WHERE key_id='rate-limit-v1';
                UPDATE app.identity_rate_limit_key_state
                SET state='active', changed_at=@flip WHERE key_id='rate-limit-v2';
                """, control, transaction);
            rotate.Parameters.AddWithValue("flip", DateTimeOffset.UtcNow);
            _ = await rotate.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        for (var attempt = 0; attempt < 5; attempt++)
            Check((await second.ConsumeAsync(request, default)).Allowed,
                "the shared total must permit only the remaining pre-rotation quota");
        var denied = await first.ConsumeAsync(request, default);
        Check(!denied.Allowed && denied.Remaining == 0,
            "rotation must sum active and retained buckets rather than reset quota");
        await using (var observer = await new IdentityAdminConnection(firstSources).OpenVerifiedAsync(default))
        {
            var counts = await RateLimitAttemptTotalsByKey(observer);
            Check(counts.GetValueOrDefault("rate-limit-v1") == 5,
                "the retained v1 counter must remain unchanged after the flip");
            Check(counts.GetValueOrDefault("rate-limit-v2") == 6,
                "only active v2 may receive five allowed and one denied post-flip increments");
        }

        foreach (var incomplete in new[] { RateKeys("rate-limit-v1"), RateKeys("rate-limit-v2") })
        {
            var failedClosed = false;
            try { _ = await CreateRateLimiter(firstSources, incomplete).ConsumeAsync(request, default); }
            catch (IdentityRateLimitUnavailableException) { failedClosed = true; }
            Check(failedClosed, "opposite/incomplete key maps must fail closed");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var concurrent = Enumerable.Range(0, 20).Select(index =>
            (index % 2 == 0 ? first : second).ConsumeAsync(request, timeout.Token));
        var decisions = await Task.WhenAll(concurrent);
        Check(decisions.Length == 20 && decisions.All(item => !item.Allowed),
            "ordered state then bucket locks must complete without deadlock");
        await using (var observer = await new IdentityAdminConnection(firstSources).OpenVerifiedAsync(default))
        {
            var counts = await RateLimitAttemptTotalsByKey(observer);
            Check(counts.GetValueOrDefault("rate-limit-v1") == 5,
                "retained v1 must never increment after the flip, including denied attempts");
            Check(counts.GetValueOrDefault("rate-limit-v2") == 26,
                "active v2 must contain every allowed and denied post-flip attempt");
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "rate-limit-cleanup-state-transition-order-does-not-deadlock":
    {
        await using var sources = RealRateLimitSources();
        var limiter = CreateRateLimiter(
            sources, RateKeys("rate-limit-v2", "rate-limit-v0", "rate-limit-v1"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var control = await OpenControlConnection();
        await using var transition = await control.BeginTransactionAsync();
        await using (var lockState = new Npgsql.NpgsqlCommand("""
            SELECT key_id
            FROM app.identity_rate_limit_key_state
            WHERE key_id IN ('rate-limit-v0', 'rate-limit-v2')
            ORDER BY key_id
            FOR UPDATE
            """, control, transition))
        await using (var locked = await lockState.ExecuteReaderAsync(timeout.Token))
            while (await locked.ReadAsync(timeout.Token)) { }

        var consume = limiter.ConsumeAsync(LoginRequest(), timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(400), timeout.Token);
        Check(!consume.IsCompleted,
            "consume cleanup must wait on accepted/retired state before touching their expired buckets");

        await using (var rotate = new Npgsql.NpgsqlCommand("""
            UPDATE app.identity_rate_limit_key_state
            SET state='retired', changed_at=now() WHERE key_id='rate-limit-v0';
            UPDATE app.identity_rate_limit_key_state
            SET state='retained', changed_at=now() WHERE key_id='rate-limit-v1';
            UPDATE app.identity_rate_limit_key_state
            SET state='active', changed_at=now() WHERE key_id='rate-limit-v2';
            """, control, transition))
            _ = await rotate.ExecuteNonQueryAsync(timeout.Token);
        await transition.CommitAsync(timeout.Token);

        Check((await consume).Allowed,
            "consume and the concurrent transition must complete within the anti-deadlock timeout");
        await using var observer = await new IdentityAdminConnection(sources).OpenVerifiedAsync(default);
        await using var expired = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM app.identity_rate_limits WHERE expires_at <= now()", observer);
        Check(Convert.ToInt32(await expired.ExecuteScalarAsync()) == 0,
            "opportunistic cleanup must delete expired accepted and retired buckets after the state transition");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-authorization":
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddPersistedIdentity();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync("AdministerUsers");
        Check(policy is not null, "the administrator policy must be registered");
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        Check(!(await authorization.AuthorizeAsync(new ClaimsPrincipal(), null, policy!)).Succeeded,
            "anonymous users must fail the administrator policy");
        foreach (var role in new[] { "operador", "validador" })
            Check(!(await authorization.AuthorizeAsync(AdminPrincipal(role), null, policy!)).Succeeded,
                role + " must fail the administrator policy");
        Check((await authorization.AuthorizeAsync(AdminPrincipal("administrador"), null, policy!)).Succeeded,
            "administrator with calculated users:provision permission must pass");

        var rejectionAudit = new CaptureRejectionAudit();
        var handler = new IdentityAuthorizationResultHandler(rejectionAudit);
        foreach (var item in new[]
        {
            (Result: PolicyAuthorizationResult.Challenge(), Status: 401, Code: "authentication_required"),
            (Result: PolicyAuthorizationResult.Forbid(), Status: 403, Code: "permission_denied")
        })
        {
            var context = HttpContextWithQuery(null, new FakeAdministrationStore(), rejectionAudit);
            await handler.HandleAsync(_ => Task.CompletedTask, context, policy!, item.Result);
            var json = await ResponseJson(context);
            Check(context.Response.StatusCode == item.Status && json.RootElement.GetProperty("code").GetString() == item.Code,
                "authorization handler must own the exact rejection envelope");
        }
        Check(rejectionAudit.Rejections.Count == 2, "authorization handler must audit each rejection exactly once");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-reset-http-contract":
    {
        _ = IdentitySecurityAudit.Normalize(new(IdentitySecurityActions.AdminStepUpReject,
            "admin_reauthentication_required", null, "http_route", "admin-user-password-reset",
            "rejected", null, new Dictionary<string, string> { ["httpMethod"] = "POST" }));
        var store = new FakeAdministrationStore();
        await using var harness = await StepUpHttpHarness.StartAsync(args[1] + ".reset", TimeProvider.System,
            new CaptureRejectionAudit(), store, new FakeAntiforgery(false), new FakeRateLimiter());
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        async Task<HttpResponseMessage> Send(string role, string? cookie, string id, string body, bool key = true)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users/" + id + "/password-reset");
            message.Headers.Add("X-Harness-Role", role);
            if (cookie is not null) message.Headers.Add("Cookie", $"GDS.AdminStepUp={cookie}");
            if (key) message.Headers.Add("Idempotency-Key", "123e4567-e89b-42d3-a456-426614174000");
            message.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return await client.SendAsync(message);
        }
        var target = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        var body = "{\"expectedManagementVersion\":\"1\"}";
        using var noPermission = await Send("operador", null, target, body);
        Check(noPermission.StatusCode == HttpStatusCode.Forbidden, "operator must not reset credentials");
        using var noStepUp = await Send("administrador", null, target, body);
        Check(noStepUp.StatusCode == HttpStatusCode.Forbidden, "reset must require step-up");
        var cookie = await harness.IssueCookieAsync();
        using var noKey = await Send("administrador", cookie, target, body, false);
        Check(noKey.StatusCode == HttpStatusCode.BadRequest, "reset must require idempotency key");
        using var self = await Send("administrador", cookie, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", body);
        Check(self.StatusCode == HttpStatusCode.Conflict && store.ResetCalls == 0, "administrator cannot reset their own password");
        using var success = await Send("administrador", cookie, target, body);
        using var json = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        Check(success.StatusCode == HttpStatusCode.OK && success.Headers.CacheControl?.NoStore == true
            && json.RootElement.GetProperty("temporaryPassword").GetString() == "TEST-TEMPORARY-PASSWORD"
            && store.ResetCalls == 1 && store.LastReset?.Version == 1,
            "valid reset returns the temporary password without caching and passes the requested version");
        Check((string?)store.LastReset?.GetType().GetProperty("ActorSecurityStamp")?.GetValue(store.LastReset) == "task-10-security-stamp",
            "reset must bind the transaction to the actor security stamp from the authenticated session");
        store.NextResetOutcome = new(AdminResetResult.ActorStateChanged);
        using var staleActor = await Send("administrador", cookie, target, body);
        using var staleJson = JsonDocument.Parse(await staleActor.Content.ReadAsStringAsync());
        Check(staleActor.StatusCode == HttpStatusCode.Forbidden
            && staleJson.RootElement.GetProperty("code").GetString() == "admin_reauthentication_required"
            && !staleJson.RootElement.TryGetProperty("temporaryPassword", out _),
            "actor invalidated inside transaction must fail closed without releasing a credential");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-registered-http-pipeline":
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(IdentityAdministrationEndpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:" + AvailablePort());
        builder.Services.AddAuthentication(HarnessAdminAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HarnessAdminAuthenticationHandler>(
                HarnessAdminAuthenticationHandler.SchemeName, _ => { });
        await using var rolesConnection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await rolesConnection.OpenAsync();
        var rolesOptions = new DbContextOptionsBuilder<ApplicationIdentityDbContext>().UseSqlite(rolesConnection).Options;
        await using (var seed = new ApplicationIdentityDbContext(rolesOptions))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Roles.AddRange(
                new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "validador", NormalizedName = "VALIDADOR" },
                new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "administrador", NormalizedName = "ADMINISTRADOR" },
                new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "desconocido", NormalizedName = "DESCONOCIDO" });
            await seed.SaveChangesAsync();
        }
        builder.Services.AddScoped(_ => new ApplicationIdentityDbContext(rolesOptions));
        builder.Services.AddIdentityAdministrationDirectory();
        builder.Services.RemoveAll<IIdentityAdministrationStore>();
        var directory = new FakeAdministrationStore();
        var securityAudit = new CaptureRejectionAudit();
        builder.Services.AddSingleton<IIdentityAdministrationStore>(directory);
        builder.Services.AddSingleton<IIdentitySecurityAudit>(securityAudit);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapIdentityAdministrationEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var resetAnonymous = await client.PostAsync("/api/admin/users/11111111-1111-4111-8111-111111111111/password-reset", null);
            Check(resetAnonymous.StatusCode == HttpStatusCode.Unauthorized,
                "password reset must be registered and require administrator authentication");
            securityAudit.Rejections.Clear();
            foreach (var path in new[] { "/api/admin/users", "/api/admin/roles" })
            {
                using var anonymous = await client.GetAsync(path);
                Check(anonymous.StatusCode == HttpStatusCode.Unauthorized
                    && anonymous.Headers.CacheControl?.NoStore == true
                    && JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString() == "authentication_required",
                    "each registered anonymous GET must return the exact Task 7 401 envelope and no-store");

                foreach (var role in new[] { "operador", "validador" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, path);
                    request.Headers.Add("X-Harness-Role", role);
                    using var denied = await client.SendAsync(request);
                    Check(denied.StatusCode == HttpStatusCode.Forbidden
                        && denied.Headers.CacheControl?.NoStore == true
                        && JsonDocument.Parse(await denied.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString() == "permission_denied",
                        "each registered " + role + " GET must return the exact Task 7 403 envelope and no-store");
                }
            }

            using var usersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
            usersRequest.Headers.Add("X-Harness-Role", "administrador");
            using var users = await client.SendAsync(usersRequest);
            Check(users.StatusCode == HttpStatusCode.OK && users.Headers.CacheControl?.NoStore == true,
                "administrator GET users must succeed without a CSRF token and use no-store");

            using var rolesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
            rolesRequest.Headers.Add("X-Harness-Role", "administrador");
            using var roles = await client.SendAsync(rolesRequest);
            using var rolesJson = JsonDocument.Parse(await roles.Content.ReadAsStringAsync());
            Check(roles.StatusCode == HttpStatusCode.OK && roles.Headers.CacheControl?.NoStore == true
                && rolesJson.RootElement.EnumerateArray().Select(item => item.GetProperty("code").GetString())
                    .SequenceEqual(new[] { "validador", "administrador" }),
                "administrator GET roles must return only supported roles present in the database");

            await using (var seed = new ApplicationIdentityDbContext(rolesOptions))
            {
                seed.Roles.Add(new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = "operador", NormalizedName = "OPERADOR" });
                await seed.SaveChangesAsync();
            }
            using var updatedRolesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/roles");
            updatedRolesRequest.Headers.Add("X-Harness-Role", "administrador");
            using var updatedRoles = await client.SendAsync(updatedRolesRequest);
            using var updatedJson = JsonDocument.Parse(await updatedRoles.Content.ReadAsStringAsync());
            Check(updatedRoles.StatusCode == HttpStatusCode.OK && updatedRoles.Headers.CacheControl?.NoStore == true
                && updatedJson.RootElement.EnumerateArray().Select(item => item.GetProperty("code").GetString())
                    .SequenceEqual(new[] { "operador", "validador", "administrador" }),
                "role catalog must reflect newly added supported database roles on the next request");

            using var rolesPostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/roles");
            rolesPostRequest.Headers.Add("X-Harness-Role", "administrador");
            using var rolesPost = await client.SendAsync(rolesPostRequest);
            Check(rolesPost.StatusCode == HttpStatusCode.MethodNotAllowed,
                "the roles catalog must remain GET-only");
            using var usersPutRequest = new HttpRequestMessage(HttpMethod.Put, "/api/admin/users");
            usersPutRequest.Headers.Add("X-Harness-Role", "administrador");
            using var usersPut = await client.SendAsync(usersPutRequest);
            Check(usersPut.StatusCode == HttpStatusCode.MethodNotAllowed,
                "the users collection must reject unsupported methods");
            Check(directory.ListCalls == 1 && directory.SuccessAudits.Count == 1,
                "only the successful registered users GET may reach the store and success audit");
            Check(securityAudit.Rejections.Count == 6
                && securityAudit.Rejections.Count(item => item.Code == "authentication_required") == 2
                && securityAudit.Rejections.Count(item => item.Code == "permission_denied") == 4,
                "the registered Task 7 handler must exclusively audit both routes' 401/403 responses");
        }
        finally
        {
            await app.StopAsync();
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-default-pagination":
    {
        Check(AdminUserDirectoryQuery.TryParse(null, null, null, null, null, out var query, out var errors),
            "omitted query values must be valid");
        Check(errors.Count == 0 && query is { Page: 1, PageSize: 25, Search: null },
            "omitted pagination must use exact 1/25 defaults");
        Check(AdminUserDirectoryQuery.TryParse("   ", null, null, null, null, out var empty, out _)
            && empty!.Search is null, "empty trimmed q must mean no filter");
        Check(IdentityAdministrationEndpoints.CanonicalRoles().Select(role => (role.Code, role.Label))
            .SequenceEqual(new[]
            {
                ("operador", "Operador"),
                ("validador", "Validador"),
                ("administrador", "Administrador")
            }), "roles endpoint must expose the fixed canonical order and labels");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-validation":
    {
        foreach (var invalid in new[]
        {
            (Q: new string('x', 101), Role: (string?)null, Status: (string?)null, Page: (string?)null, Size: (string?)null, Field: "q"),
            (Q: (string?)null, Role: "root", Status: (string?)null, Page: (string?)null, Size: (string?)null, Field: "role"),
            (Q: (string?)null, Role: (string?)null, Status: "enabled", Page: (string?)null, Size: (string?)null, Field: "status"),
            (Q: (string?)null, Role: (string?)null, Status: (string?)null, Page: "0", Size: (string?)null, Field: "page"),
            (Q: (string?)null, Role: (string?)null, Status: (string?)null, Page: (string?)null, Size: "101", Field: "pageSize")
        })
        {
            Check(!AdminUserDirectoryQuery.TryParse(invalid.Q, invalid.Role, invalid.Status, invalid.Page, invalid.Size, out _, out var invalidErrors)
                && invalidErrors.ContainsKey(invalid.Field), invalid.Field + " must be rejected");
        }
        Check(AdminUserDirectoryQuery.TryParse("á", "administrador", "inactive", "2", "100", out var valid, out _)
            && valid is { Page: 2, PageSize: 100, IsActive: false }, "canonical bounded filters must pass");
        var supplementary = char.ConvertFromUtf32(0x1F6E1);
        Check(AdminUserDirectoryQuery.TryParse(string.Concat(Enumerable.Repeat(supplementary, 100)), null, null, null, null, out _, out _),
            "100 supplementary Unicode runes must pass regardless of UTF-16 length");
        Check(!AdminUserDirectoryQuery.TryParse(string.Concat(Enumerable.Repeat(supplementary, 101)), null, null, null, null, out _, out var runeErrors)
            && runeErrors.ContainsKey("q"), "101 supplementary Unicode runes must fail");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-no-store-envelope":
    {
        var store = new FakeAdministrationStore();
        var audit = new CaptureRejectionAudit();
        var success = HttpContextWithQuery("?page=1&pageSize=25", store, audit);
        var successResult = await IdentityAdministrationEndpoints.ListUsersAsync(success, store, audit);
        await successResult.ExecuteAsync(success);
        using var successJson = await ResponseJson(success);
        Check(success.Response.StatusCode == 200 && success.Response.Headers.CacheControl == "no-store",
            "success must use no-store");
        Check(successJson.RootElement.TryGetProperty("items", out _)
            && successJson.RootElement.GetProperty("page").GetInt32() == 1
            && successJson.RootElement.GetProperty("pageSize").GetInt32() == 25
            && successJson.RootElement.TryGetProperty("total", out _), "success envelope must be stable");

        var invalid = HttpContextWithQuery("?page=0", store, audit);
        var invalidResult = await IdentityAdministrationEndpoints.ListUsersAsync(invalid, store, audit);
        await invalidResult.ExecuteAsync(invalid);
        using var invalidJson = await ResponseJson(invalid);
        Check(invalid.Response.StatusCode == 400 && invalid.Response.Headers.CacheControl == "no-store",
            "validation errors must use no-store");
        Check(invalidJson.RootElement.GetProperty("code").GetString() == "validation_failed"
            && invalidJson.RootElement.TryGetProperty("error", out _)
            && invalidJson.RootElement.TryGetProperty("fieldErrors", out _), "error envelope must be stable");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-search-and-order":
    {
        Check(IdentityAdministrationStore.EscapeLikeLiteral(@"a%_\z") == @"a\%\_\\z",
            "LIKE wildcard and escape characters must be literal");
        var source = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "backend/IdentityAdministration/IdentityAdministrationStore.cs"));
        Check(source.Contains("""ILIKE @pattern ESCAPE '\'""", StringComparison.Ordinal),
            "search must use parameterized ILIKE with an explicit escape");
        Check(source.Contains("ORDER BY u.\"NormalizedUserName\", u.\"Id\"", StringComparison.Ordinal),
            "ordering must be normalized name then UUID");
        var directoryProjection = source[source.IndexOf("private static async Task<IReadOnlyList<AdminUserSummary>> ReadPageAsync", StringComparison.Ordinal)..
            source.IndexOf("private static NpgsqlCommand CreateCommand", StringComparison.Ordinal)];
        Check(!directoryProjection.Contains("PasswordHash", StringComparison.Ordinal)
            && !directoryProjection.Contains("SecurityStamp", StringComparison.Ordinal)
            && !directoryProjection.Contains("ConcurrencyStamp", StringComparison.Ordinal),
            "directory projection must not expose identity secrets or stamps");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-success-audit":
    {
        var store = new FakeAdministrationStore();
        var audit = new CaptureRejectionAudit();
        var context = HttpContextWithQuery("?q=%20name%20&role=operador&status=active&page=2&pageSize=10", store, audit);
        var result = await IdentityAdministrationEndpoints.ListUsersAsync(context, store, audit);
        await result.ExecuteAsync(context);
        Check(store.ListCalls == 1 && store.SuccessAudits.Count == 1,
            "successful list must be read and audited exactly once");
        var recorded = store.SuccessAudits.Single();
        Check(recorded.EntityType == "user_directory" && recorded.EntityId == "user-directory"
            && recorded.Query is { Search: "name", Role: "operador", Status: "active", Page: 2, PageSize: 10 },
            "success audit must contain only stable identity and sanitized filters/pagination");
        Check(recorded.GetType().GetProperties().All(property => !property.Name.Contains("Items", StringComparison.OrdinalIgnoreCase)
            && !property.Name.Contains("Results", StringComparison.OrdinalIgnoreCase)),
            "success audit contract must not accept directory results");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-directory-validation-audit":
    {
        var store = new FakeAdministrationStore();
        var capture = new CaptureAuditSink { Fail = true };
        var audit = new IdentitySecurityAudit(capture, NullLogger<IdentitySecurityAudit>.Instance);
        var context = HttpContextWithQuery("?role=unknown&page=0", store, audit);
        var result = await IdentityAdministrationEndpoints.ListUsersAsync(context, store, audit);
        await result.ExecuteAsync(context);
        using var json = await ResponseJson(context);
        Check(context.Response.StatusCode == 400 && json.RootElement.GetProperty("code").GetString() == "validation_failed",
            "audit failure must preserve the original 400 rejection");
        Check(capture.IndependentTransactionWrites == 1 && store.ListCalls == 0 && store.SuccessAudits.Count == 0,
            "endpoint must own one validation audit and perform no read/success audit");
        Check(capture.Rows.Single() is { EntityType: "user_directory", EntityId: "user-directory", Action: "auth.validation.reject" },
            "validation audit must use the stable user-directory identity");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-stepup-filter-missing-expired":
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(args[1], clock, audit);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var missingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/test-mutation");
        missingRequest.Headers.Add("X-Harness-Role", "administrador");
        using var missing = await client.SendAsync(missingRequest);
        using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        Check(missing.StatusCode == HttpStatusCode.Forbidden
            && missing.Headers.CacheControl?.NoStore == true
            && missingJson.RootElement.GetProperty("code").GetString() == "admin_reauthentication_required",
            "missing StepUp must return the exact no-store 403 envelope");
        Check(audit.Rejections.Count == 1
            && audit.Rejections[0] is { Action: "auth.admin_stepup.reject", Code: "admin_reauthentication_required" },
            "filter must own exactly one missing-StepUp audit");

        var protectedValue = await harness.IssueCookieAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        using var expiredRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/test-mutation");
        expiredRequest.Headers.Add("X-Harness-Role", "administrador");
        expiredRequest.Headers.Add("Cookie", $"GDS.AdminStepUp={protectedValue}");
        using var expired = await client.SendAsync(expiredRequest);
        using var expiredJson = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
        Check(expired.StatusCode == HttpStatusCode.Forbidden
            && expiredJson.RootElement.GetProperty("code").GetString() == "admin_reauthentication_required"
            && expired.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("GDS.AdminStepUp=", StringComparison.Ordinal)),
            "expired StepUp must return 403 and clear its cookie");
        Check(audit.Rejections.Count == 2
            && audit.Rejections.All(item => item.Action == IdentitySecurityActions.AdminStepUpReject)
            && harness.MutationCalls == 0,
            "missing/expired requests must each be audited once before target access");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-stepup-filter-audit-ownership":
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(args[1], clock, audit);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var anonymous = await client.PostAsync("/api/admin/test-mutation", null);
        Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "authorization owns anonymous rejection");
        using var operatorRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/test-mutation");
        operatorRequest.Headers.Add("X-Harness-Role", "operador");
        using var roleDenied = await client.SendAsync(operatorRequest);
        Check(roleDenied.StatusCode == HttpStatusCode.Forbidden, "authorization owns non-admin rejection");
        using var adminRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/test-mutation");
        adminRequest.Headers.Add("X-Harness-Role", "administrador");
        using var stepDenied = await client.SendAsync(adminRequest);
        Check(stepDenied.StatusCode == HttpStatusCode.Forbidden, "StepUp filter owns authenticated admin rejection");
        using var readRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/test-read");
        readRequest.Headers.Add("X-Harness-Role", "administrador");
        using var read = await client.SendAsync(readRequest);
        Check(read.StatusCode == HttpStatusCode.NoContent, "admin GET remains unaffected by StepUp");
        Check(audit.Rejections.Count == 3
            && audit.Rejections.Count(item => item.Code == "authentication_required") == 1
            && audit.Rejections.Count(item => item.Code == "permission_denied") == 1
            && audit.Rejections.Count(item => item.Code == "admin_reauthentication_required") == 1
            && harness.MutationCalls == 0,
            "authorization and StepUp owners must emit one distinct audit each without duplicates");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "create-csrf-returns-exact-code":
    {
        var store = new FakeAdministrationStore();
        var quota = new FakeRateLimiter();
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1], TimeProvider.System, audit, store, new FakeAntiforgery(true), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users");
        request.Headers.Add("X-Harness-Role", "administrador");
        request.Headers.Add("Idempotency-Key", "123e4567-e89b-42d3-a456-426614174000");
        request.Headers.Add("Cookie", $"GDS.AdminStepUp={await harness.IssueCookieAsync()}");
        request.Content = new StringContent(JsonSerializer.Serialize(ValidCreateRequest()),
            System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.BadRequest
            && response.Headers.CacheControl?.NoStore == true
            && json.RootElement.GetProperty("code").GetString() == "csrf_invalid",
            "CSRF rejection must use the exact code");
        Check(quota.Calls == 0 && store.CreateCalls == 0 && audit.Rejections.Count == 1,
            "CSRF must reject before quota, hashing and mutation");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "create-validates-fields-uuid-v4-and-temporary-password":
    {
        var normalizer = new UpperInvariantLookupNormalizer();
        Check(AdminCreateUserIntent.TryCreate(ValidCreateRequest(), normalizer, out var intent, out _)
            && intent is { Username: "alpha.user", NormalizedUsername: "ALPHA.USER", DisplayName: "Álpha User", Role: "operador" },
            "valid fields must trim and normalize exactly");
        foreach (var invalid in new[]
        {
            new AdminCreateUserRequest("ab", "Name", "operador"),
            new AdminCreateUserRequest("bad user", "Name", "operador"),
            new AdminCreateUserRequest("valid.user", "\u0001", "operador"),
            new AdminCreateUserRequest("valid.user", new string('x', 121), "operador"),
            new AdminCreateUserRequest("valid.user", "Name", "root")
        })
            Check(!AdminCreateUserIntent.TryCreate(invalid, normalizer, out _, out var errors) && errors.Count > 0,
                "each invalid field set must be rejected");
        Check(IdentityCreationProtocol.TryParseIdempotencyKey("123e4567-e89b-42d3-a456-426614174000", out _),
            "canonical lowercase UUID v4 must pass");
        foreach (var invalidKey in new[]
        {
            "123E4567-E89B-42D3-A456-426614174000", "123e4567e89b42d3a456426614174000",
            "123e4567-e89b-12d3-a456-426614174000", "123e4567-e89b-42d3-7456-426614174000",
            "not-a-guid"
        }) Check(!IdentityCreationProtocol.TryParseIdempotencyKey(invalidKey, out _), "noncanonical/non-v4 key must fail");
        var password = IdentityCreationProtocol.GenerateTemporaryPassword();
        Check(IdentityCreationProtocol.IsTemporaryPasswordFormat(password)
            && password.All(character => !"0O1Il".Contains(character))
            && !IdentityCreationProtocol.IsTemporaryPasswordFormat(new string('A', 23))
            && !IdentityCreationProtocol.IsTemporaryPasswordFormat(new string('0', 24)),
            "temporary password must be 24 unambiguous characters");
        Check(IdentityCreationProtocol.IsNormalizedUsernameConflict(
                "23505", "public", "AspNetUsers", "UserNameIndex")
            && !IdentityCreationProtocol.IsNormalizedUsernameConflict(
                "23505", "app", "AspNetUsers", "UserNameIndex")
            && !IdentityCreationProtocol.IsNormalizedUsernameConflict(
                "23505", "public", "AspNetRoles", "UserNameIndex")
            && !IdentityCreationProtocol.IsNormalizedUsernameConflict(
                "23505", "public", "AspNetUsers", "identity_idempotency_receipts_pkey")
            && !IdentityCreationProtocol.IsNormalizedUsernameConflict(
                "23514", "public", "AspNetUsers", "UserNameIndex"),
            "only the exact normalized-username 23505 may map to username_conflict");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "idempotency-hash-matches-shared-vector":
    {
        var actual = Convert.ToHexString(IdentityCreationProtocol.KeyHash(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("123e4567-e89b-42d3-a456-426614174000"))).ToLowerInvariant();
        Check(actual == "62a2b5e4351b21ac9b4446e43682c26070f0ae39cad27d56f960bb96f356b775",
            "shared actor:key UTF-8 vector must match without Guid byte order");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "create-headers-are-exact-for-original-and-replay":
    {
        foreach (var kind in new[] { AdminCreateUserOutcomeKind.Created, AdminCreateUserOutcomeKind.Replayed })
        {
            var response = SampleCreateResponse("temporary-A");
            var exactBody = JsonSerializer.SerializeToUtf8Bytes(response,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var store = new FakeAdministrationStore
            {
                NextCreateOutcome = new(kind, StatusCodes.Status201Created, exactBody)
            };
            var quota = new FakeRateLimiter();
            var countingHasher = new CountingPasswordHasher();
            var databasePath = args[1] + "." + kind;
            await using var harness = await StepUpHttpHarness.StartAsync(databasePath, TimeProvider.System,
                new CaptureRejectionAudit(), store, new FakeAntiforgery(false), quota, countingHasher);
            using var client = new HttpClient { BaseAddress = harness.BaseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users");
            request.Headers.Add("X-Harness-Role", "administrador");
            request.Headers.Add("Idempotency-Key", "123e4567-e89b-42d3-a456-426614174000");
            request.Headers.Add("Cookie", $"GDS.AdminStepUp={await harness.IssueCookieAsync()}");
            request.Content = new StringContent(JsonSerializer.Serialize(ValidCreateRequest()),
                System.Text.Encoding.UTF8, "application/json");
            using var result = await client.SendAsync(request);
            var actualBody = await result.Content.ReadAsByteArrayAsync();
            Check(result.StatusCode == HttpStatusCode.Created && result.Headers.CacheControl?.NoStore == true
                && result.Headers.TryGetValues("Pragma", out var pragma) && pragma.Single() == "no-cache"
                && !result.Headers.Contains("Location") && actualBody.SequenceEqual(exactBody),
                "original and replay must preserve the store's exact status/body bytes and cache headers");
            Check(kind == AdminCreateUserOutcomeKind.Replayed
                ? result.Headers.GetValues("Idempotency-Replayed").Single() == "true"
                : !result.Headers.Contains("Idempotency-Replayed"),
                "only replay may include Idempotency-Replayed:true");
            Check(quota.Requests.Single() is { Policy: "admin-mutation", Subject: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" },
                "creation must consume the shared admin-mutation quota for the authenticated actor");
            Check(quota.Requests.Single().NormalizedIp is null,
                "admin-mutation quota must not include an IP component");
            Check(countingHasher.Calls == (kind == AdminCreateUserOutcomeKind.Created ? 1 : 0),
                "only a new receipt decision may invoke the password hasher");
        }

        var duplicateStore = new FakeAdministrationStore();
        var duplicateQuota = new FakeRateLimiter();
        await using var duplicateHarness = await StepUpHttpHarness.StartAsync(args[1] + ".duplicate",
            TimeProvider.System, new CaptureRejectionAudit(), duplicateStore,
            new FakeAntiforgery(false), duplicateQuota, new CountingPasswordHasher());
        using var duplicateClient = new HttpClient { BaseAddress = duplicateHarness.BaseAddress };
        using var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users");
        duplicateRequest.Headers.Add("X-Harness-Role", "administrador");
        duplicateRequest.Headers.TryAddWithoutValidation("Idempotency-Key", new[]
        {
            "123e4567-e89b-42d3-a456-426614174000",
            "223e4567-e89b-42d3-a456-426614174000"
        });
        duplicateRequest.Headers.Add("Cookie", $"GDS.AdminStepUp={await duplicateHarness.IssueCookieAsync()}");
        duplicateRequest.Content = new StringContent(JsonSerializer.Serialize(ValidCreateRequest()),
            System.Text.Encoding.UTF8, "application/json");
        using var duplicate = await duplicateClient.SendAsync(duplicateRequest);
        Check(duplicate.StatusCode == HttpStatusCode.BadRequest
            && duplicateQuota.Calls == 0 && duplicateStore.CreateCalls == 0,
            "duplicate/comma Idempotency-Key values must reject before quota and store");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "create-two-stores-in-one-process-replay-identical-protected-response":
    {
        var provider = new EphemeralDataProtectionProvider();
        var firstStoreProtocol = new IdentityCreationReceiptProtocol(provider);
        var secondStoreProtocol = new IdentityCreationReceiptProtocol(provider);
        var normalizer = new UpperInvariantLookupNormalizer();
        Check(AdminCreateUserIntent.TryCreate(ValidCreateRequest(), normalizer, out var intent, out _),
            "local receipt intent must validate");
        var command = new AdminCreateUserCommand(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("123e4567-e89b-42d3-a456-426614174000"),
            intent!, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), "local-two-store");
        var factoryCalls = 0;
        var created = firstStoreProtocol.CreateNew(command, _ =>
        {
            factoryCalls++;
            return new(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), "COUNTING-HASH",
                IdentityCreationProtocol.GenerateTemporaryPassword());
        });
        var sharedReceipt = new AdminCreateReceiptSnapshot(
            command.ActorUserId, command.Intent.PayloadHash(), created.Credential.UserId,
            created.ProtectedResponse, created.CreatedAt, created.ExpiresAt);
        var replay = secondStoreProtocol.ResolveExisting(command, sharedReceipt);
        Check(factoryCalls == 1
            && replay.Kind == AdminCreateUserOutcomeKind.Replayed
            && replay.StatusCode == created.Outcome.StatusCode
            && replay.Body is not null && created.Outcome.Body is not null
            && replay.Body.SequenceEqual(created.Outcome.Body),
            "two production receipt-protocol instances sharing a key ring/database snapshot must replay exact bytes without another factory call");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "same-idempotency-uuid-is-isolated-by-actor":
    {
        var key = Guid.Parse("123e4567-e89b-42d3-a456-426614174000");
        Check(!IdentityCreationProtocol.KeyHash(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), key)
            .SequenceEqual(IdentityCreationProtocol.KeyHash(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), key)),
            "same idempotency UUID must hash differently for different actors");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "same-actor-key-with-different-canonical-payload-returns-idempotency-key-reused":
    {
        var normalizer = new UpperInvariantLookupNormalizer();
        _ = AdminCreateUserIntent.TryCreate(ValidCreateRequest(), normalizer, out var first, out _);
        _ = AdminCreateUserIntent.TryCreate(ValidCreateRequest() with { DisplayName = "Different" }, normalizer, out var second, out _);
        Check(!first!.PayloadHash().SequenceEqual(second!.PayloadHash()),
            "length-prefixed canonical payload must distinguish changed creation data");
        var store = new FakeAdministrationStore
        {
            NextCreateOutcome = new(AdminCreateUserOutcomeKind.IdempotencyKeyReused)
        };
        var context = CreateContext("123e4567-e89b-42d3-a456-426614174000");
        var result = await IdentityAdministrationEndpoints.CreateUserAsync(context, ValidCreateRequest(),
            new FakeAntiforgery(false), new FakeRateLimiter(), store, new CaptureRejectionAudit(), normalizer,
            new PasswordHasher<ApplicationUser>(), TimeProvider.System);
        await result.ExecuteAsync(context);
        using var json = await ResponseJson(context);
        Check(context.Response.StatusCode == 409 && json.RootElement.GetProperty("code").GetString() == "idempotency_key_reused",
            "changed payload result must map to exact 409 code");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "distinct-creation-intents-generate-different-temporary-passwords":
    {
        var passwords = Enumerable.Range(0, 16).Select(_ => IdentityCreationProtocol.GenerateTemporaryPassword()).ToHashSet();
        Check(passwords.Count == 16 && passwords.All(value => value.Length == 24),
            "independent intents must generate distinct 24-character CSPRNG passwords");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "expired-receipt-becomes-tombstone-without-duplicate":
    {
        var store = new FakeAdministrationStore
        {
            NextCreateOutcome = new(AdminCreateUserOutcomeKind.IdempotencyResultExpired)
        };
        var context = CreateContext("123e4567-e89b-42d3-a456-426614174000");
        var result = await IdentityAdministrationEndpoints.CreateUserAsync(context, ValidCreateRequest(),
            new FakeAntiforgery(false), new FakeRateLimiter(), store, new CaptureRejectionAudit(),
            new UpperInvariantLookupNormalizer(), new PasswordHasher<ApplicationUser>(), TimeProvider.System);
        await result.ExecuteAsync(context);
        using var json = await ResponseJson(context);
        Check(context.Response.StatusCode == 409 && store.CreateCalls == 1
            && json.RootElement.GetProperty("code").GetString() == "idempotency_result_expired",
            "expired receipt must reject once without a second creation attempt");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "receipt-cleanup-nulls-ciphertext-and-preserves-row":
    {
        var cleanupStore = new FakeAdministrationStore();
        var cleanupService = new IdentityIdempotencyReceiptCleanupService(
            cleanupStore, NullLogger<IdentityIdempotencyReceiptCleanupService>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cleanupService.StartAsync(cancellation.Token);
        for (var attempt = 0; attempt < 20 && cleanupStore.TombstoneCalls == 0; attempt++)
            await Task.Delay(10);
        cancellation.Cancel();
        await cleanupService.StopAsync(default);
        Check(cleanupStore.TombstoneCalls == 1 && cleanupStore.TombstoneBatchSizes.Single() == 250,
            "hosted cleanup must invoke exactly one bounded tombstoning batch before cancellation");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "create-rejections-are-best-effort-audited-without-secrets":
    {
        var sink = new CaptureAuditSink { Fail = true };
        var audit = new IdentitySecurityAudit(sink, NullLogger<IdentitySecurityAudit>.Instance);
        var context = CreateContext("123e4567-e89b-42d3-a456-426614174000");
        var result = await IdentityAdministrationEndpoints.CreateUserAsync(context, ValidCreateRequest(),
            new FakeAntiforgery(true), new FakeRateLimiter(), new FakeAdministrationStore(), audit,
            new UpperInvariantLookupNormalizer(), new PasswordHasher<ApplicationUser>(), TimeProvider.System);
        await result.ExecuteAsync(context);
        Check(context.Response.StatusCode == 400 && sink.Rows.Count == 1
            && !sink.Rows[0].SecurityContextJson.Contains("123e4567", StringComparison.OrdinalIgnoreCase)
            && !sink.Rows[0].SecurityContextJson.Contains("alpha.user", StringComparison.OrdinalIgnoreCase),
            "audit sink failure must preserve rejection and omit key/payload secrets");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "receipt-plaintext-never-reaches-db-log-or-audit":
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = provider.CreateProtector(IdentityCreationProtocol.ReceiptProtectorPurpose);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(
            "temporary-password-never-persisted:123e4567-e89b-42d3-a456-426614174000");
        var firstCiphertext = protector.Protect(plaintext);
        var secondCiphertext = protector.Protect(plaintext);
        Check(!firstCiphertext.SequenceEqual(plaintext)
            && !firstCiphertext.SequenceEqual(secondCiphertext)
            && protector.Unprotect(firstCiphertext).SequenceEqual(plaintext),
            "receipt protection must be randomized, reversible only through its purpose and never equal plaintext");

        var secretValues = new[]
        {
            "123e4567-e89b-42d3-a456-426614174000", "alpha.user", "Álpha User",
            "TemporarySecret123!", "HASH-SECRET", "CIPHERTEXT-SECRET",
            "Host=private-db;Password=connection-secret"
        };
        var failingStore = new FakeAdministrationStore
        {
            CreateException = new IdentityAdminUnavailableException(
                new InvalidOperationException(string.Join('|', secretValues)))
        };
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".privacy", TimeProvider.System, new CaptureRejectionAudit(), failingStore,
            new FakeAntiforgery(false), new FakeRateLimiter(), new CountingPasswordHasher());
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users");
        request.Headers.Add("X-Harness-Role", "administrador");
        request.Headers.Add("Idempotency-Key", secretValues[0]);
        request.Headers.Add("Cookie", $"GDS.AdminStepUp={await harness.IssueCookieAsync()}");
        request.Content = new StringContent(JsonSerializer.Serialize(ValidCreateRequest()),
            System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        using var responseJson = JsonDocument.Parse(responseText);
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable
            && response.Headers.CacheControl?.NoStore == true
            && responseJson.RootElement.GetProperty("code").GetString() == "service_unavailable"
            && secretValues.All(secret => !responseText.Contains(secret, StringComparison.Ordinal)),
            "registered HTTP pipeline must return a generic 503 without exception, credential, receipt or connection details");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "role-status-csrf-returns-exact-code":
    {
        var store = new FakeAdministrationStore();
        var quota = new FakeRateLimiter();
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-csrf", TimeProvider.System, audit,
            store, new FakeAntiforgery(true), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        var cookie = await harness.IssueCookieAsync();
        foreach (var (suffix, body) in new[] { ("role", "{"), ("status", "not-json") })
        {
            using var response = await SendMutationAsync(client, cookie, suffix, body);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == HttpStatusCode.BadRequest
                && response.Headers.CacheControl?.NoStore == true
                && json.RootElement.GetProperty("code").GetString() == "csrf_invalid",
                "both administrative mutations must reject invalid CSRF with the exact envelope before JSON parsing");
        }
        Check(store.MutationCalls == 0 && quota.Calls == 0 && audit.Rejections.Count == 2
            && audit.Rejections.All(item => item.Action == IdentitySecurityActions.CsrfReject
                && item.Code == "csrf_invalid"),
            "CSRF must own one audit per route and run before input, quota and mutation logic");

        var precedenceAudit = new CaptureRejectionAudit();
        var precedenceStore = new FakeAdministrationStore();
        await using var precedenceHarness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-precedence", TimeProvider.System, precedenceAudit,
            precedenceStore, new FakeAntiforgery(false), new FakeRateLimiter());
        using var precedenceClient = new HttpClient { BaseAddress = precedenceHarness.BaseAddress };
        var precedenceCookie = await precedenceHarness.IssueCookieAsync();
        foreach (var suffix in new[] { "role", "status" })
        {
            var body = suffix == "role"
                ? "{\"role\":\"operador\",\"expectedManagementVersion\":\"1\"}"
                : "{\"isActive\":true,\"expectedManagementVersion\":\"1\"}";
            foreach (var expected in new[]
            {
                (Role: (string?)null, Mandatory: false, Cookie: (string?)null,
                    Status: HttpStatusCode.Unauthorized, Code: "authentication_required"),
                (Role: "administrador", Mandatory: true, Cookie: precedenceCookie,
                    Status: HttpStatusCode.Forbidden, Code: "password_change_required"),
                (Role: "operador", Mandatory: false, Cookie: precedenceCookie,
                    Status: HttpStatusCode.Forbidden, Code: "permission_denied"),
                (Role: "administrador", Mandatory: false, Cookie: (string?)null,
                    Status: HttpStatusCode.Forbidden, Code: "admin_reauthentication_required")
            })
            {
                using var rejected = await SendMutationWithHeadersAsync(
                    precedenceClient, suffix, body, expected.Role, expected.Cookie, expected.Mandatory);
                using var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
                Check(rejected.StatusCode == expected.Status
                    && json.RootElement.GetProperty("code").GetString() == expected.Code,
                    "both real mutation routes must preserve anonymous, mandatory-password, role and StepUp precedence");
            }
        }
        Check(precedenceStore.MutationCalls == 0
            && precedenceAudit.Rejections.Count(item => item.Code == "authentication_required") == 2
            && precedenceAudit.Rejections.Count(item => item.Code == "password_change_required") == 2
            && precedenceAudit.Rejections.Count(item => item.Code == "permission_denied") == 2
            && precedenceAudit.Rejections.Count(item => item.Code == "admin_reauthentication_required") == 2,
            "each precedence owner must audit each route exactly once before target access");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "management-version-rejects-json-numbers-and-preserves-over-2pow53":
    {
        const string exactVersion = "9007199254740993";
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var store = new FakeAdministrationStore
        {
            MutationHandler = command => new(AdminUserMutationOutcomeKind.NoOp,
                new(command.Intent.TargetUserId.ToString("D"), "target.user", "Target User",
                    "operador", true, false, now.AddDays(-1), now,
                    command.Intent.ExpectedManagementVersionText))
        };
        var quota = new FakeRateLimiter();
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-version", TimeProvider.System, audit, store,
            new FakeAntiforgery(false), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        var cookie = await harness.IssueCookieAsync();
        string Body(string suffix, string versionJson) => suffix == "role"
            ? "{\"role\":\"operador\",\"expectedManagementVersion\":" + versionJson + "}"
            : "{\"isActive\":true,\"expectedManagementVersion\":" + versionJson + "}";
        var invalidVersions = new[]
        {
            "null", "true", "false", "[]", "{}", "1", "9007199254740993",
            "\"0\"", "\"01\"", "\"+1\"", "\"-1\"", "\" 1\"", "\"1 \"",
            "\"1.0\"", "\"1e0\"", "\"9223372036854775808\""
        };
        foreach (var suffix in new[] { "role", "status" })
        foreach (var invalid in invalidVersions)
        {
            using var rejected = await SendMutationAsync(client, cookie, suffix, Body(suffix, invalid));
            using var json = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Check(rejected.StatusCode == HttpStatusCode.BadRequest
                && json.RootElement.GetProperty("code").GetString() == "validation_failed",
                "both routes must reject every noncanonical management-version boundary without coercion");
        }
        using (var invalidBoolean = await SendMutationAsync(client, cookie, "status",
            "{\"isActive\":\"true\",\"expectedManagementVersion\":\"1\"}"))
            Check(invalidBoolean.StatusCode == HttpStatusCode.BadRequest,
                "status mutation must require an actual JSON boolean");
        foreach (var (suffix, duplicate) in new[]
        {
            ("role", "{\"role\":\"operador\",\"role\":\"validador\",\"expectedManagementVersion\":\"1\"}"),
            ("role", "{\"role\":\"operador\",\"expectedManagementVersion\":\"1\",\"expectedManagementVersion\":\"2\"}"),
            ("status", "{\"isActive\":true,\"isActive\":false,\"expectedManagementVersion\":\"1\"}"),
            ("status", "{\"isActive\":true,\"expectedManagementVersion\":\"1\",\"expectedManagementVersion\":\"2\"}")
        })
        {
            using var rejected = await SendMutationAsync(client, cookie, suffix, duplicate);
            Check(rejected.StatusCode == HttpStatusCode.BadRequest,
                "both routes must reject duplicate relevant and management-version properties");
        }
        foreach (var invalidRole in new[] { "OPERADOR", "admin" })
        {
            using var rejected = await SendMutationAsync(client, cookie, "role",
                "{\"role\":\"" + invalidRole + "\",\"expectedManagementVersion\":\"1\"}");
            Check(rejected.StatusCode == HttpStatusCode.BadRequest,
                "roles must use one exact lowercase canonical catalog value");
        }
        var invalidTargets = new[]
        {
            "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB",
            "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb",
            "{bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb}",
            "urn:uuid:bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            " bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb ",
            "not-a-uuid",
            "00000000-0000-0000-0000-000000000000"
        };
        foreach (var suffix in new[] { "role", "status" })
        {
            foreach (var invalidId in invalidTargets)
            {
                using var rejected = await SendMutationToTargetAsync(
                    client, cookie, invalidId, suffix, Body(suffix, "\"1\""));
                Check(rejected.StatusCode == HttpStatusCode.BadRequest,
                    "both routes must reject uppercase, N, braces, URN, whitespace, malformed and empty UUID values");
            }
            using var emptySegment = await SendMutationToTargetAsync(
                client, cookie, string.Empty, suffix, Body(suffix, "\"1\""));
            Check(emptySegment.StatusCode == HttpStatusCode.NotFound,
                "an absent route segment must remain an unroutable empty target on both routes");
        }
        Check(store.MutationCalls == 0 && quota.Calls == 0,
            "invalid typed input must reject before quota and store access");
        foreach (var suffix in new[] { "role", "status" })
        foreach (var (versionText, versionValue) in new[]
        {
            ("1", 1L),
            (exactVersion, 9007199254740993L),
            ("9223372036854775807", long.MaxValue)
        })
        {
            using var accepted = await SendMutationAsync(
                client, cookie, suffix, Body(suffix, JsonSerializer.Serialize(versionText)));
            var acceptedText = await accepted.Content.ReadAsStringAsync();
            using var acceptedJson = JsonDocument.Parse(acceptedText);
            var command = store.MutationCommands.Last();
            Check(accepted.StatusCode == HttpStatusCode.OK
                && acceptedJson.RootElement.GetProperty("managementVersion").GetString() == versionText
                && command.Kind == (suffix == "role" ? AdminUserMutationKind.Role : AdminUserMutationKind.Status)
                && command.Intent.ExpectedManagementVersionText == versionText
                && command.Intent.ExpectedManagementVersion == versionValue
                && acceptedText.Contains("\"managementVersion\":\"" + versionText + "\"", StringComparison.Ordinal),
                "both routes must preserve valid >2^53 and signed-long-maximum strings end to end");
        }
        Check(store.MutationCalls == 6 && quota.Calls == 6,
            "only the six valid route/version combinations may consume quota or reach the store");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "role-status-return-stable-errors-for-missing-stale-self-and-last-admin":
    {
        var store = new FakeAdministrationStore();
        var matrixOutcomes = new[]
        {
            AdminUserMutationOutcomeKind.NotFound,
            AdminUserMutationOutcomeKind.StaleManagementVersion,
            AdminUserMutationOutcomeKind.SelfManagementForbidden,
            AdminUserMutationOutcomeKind.LastActiveAdministrator
        };
        foreach (var _ in new[] { "role", "status" })
        foreach (var outcome in matrixOutcomes)
            store.MutationOutcomes.Enqueue(new(outcome));
        var audit = new CaptureRejectionAudit();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-matrix", TimeProvider.System, audit, store,
            new FakeAntiforgery(false), new FakeRateLimiter());
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        var cookie = await harness.IssueCookieAsync();
        var expected = new[]
        {
            (HttpStatusCode.NotFound, "validation_failed"),
            (HttpStatusCode.Conflict, "stale_management_version"),
            (HttpStatusCode.Conflict, "self_management_forbidden"),
            (HttpStatusCode.Conflict, "last_active_administrator")
        };
        foreach (var suffix in new[] { "role", "status" })
        for (var index = 0; index < expected.Length; index++)
        {
            var body = suffix == "role"
                ? "{\"role\":\"validador\",\"expectedManagementVersion\":\"7\"}"
                : "{\"isActive\":false,\"expectedManagementVersion\":\"7\"}";
            using var response = await SendMutationAsync(client, cookie, suffix, body);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(response.StatusCode == expected[index].Item1
                && json.RootElement.GetProperty("code").GetString() == expected[index].Item2,
                "missing, stale, self and last-admin outcomes must retain their exact status/code matrix");
        }
        Check(audit.Rejections.Count == 8
            && audit.Rejections.Select(item => item.Code).SequenceEqual(
                expected.Select(item => item.Item2).Concat(expected.Select(item => item.Item2)))
            && audit.Rejections.All(item => item.EntityType == "user"
                && item.Metadata.Keys.All(key => key is "httpMethod" or "routeTemplate" or "reason"
                    or "validationFields" or "expectedVersion")),
            "each business/concurrency rejection must be audited once with allowlisted metadata");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "noop-runs-guards-without-version-or-success-audit":
    {
        var now = new DateTimeOffset(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);
        var store = new FakeAdministrationStore
        {
            NextMutationOutcome = new(AdminUserMutationOutcomeKind.NoOp,
                new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", "target.user", "Target User",
                    "operador", true, false, now.AddDays(-1), now, "41"))
        };
        var audit = new CaptureRejectionAudit();
        var quota = new FakeRateLimiter();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-noop", TimeProvider.System, audit, store,
            new FakeAntiforgery(false), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        var cookie = await harness.IssueCookieAsync();
        using var response = await SendMutationAsync(client, cookie, "role",
            "{\"role\":\"operador\",\"expectedManagementVersion\":\"41\"}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.OK
            && json.RootElement.GetProperty("managementVersion").GetString() == "41"
            && json.RootElement.GetProperty("updatedAt").GetDateTimeOffset() == now
            && store.MutationCalls == 1 && quota.Calls == 1 && audit.Rejections.Count == 0
            && !response.Headers.Contains("Set-Cookie"),
            "guarded no-op must return the unchanged summary, consume quota, avoid rejection/success-side signals and not renew StepUp");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "admin-mutation-quota-survives-stale-conflict":
    {
        var store = new FakeAdministrationStore
        {
            NextMutationOutcome = new(AdminUserMutationOutcomeKind.StaleManagementVersion)
        };
        var quota = new FakeRateLimiter();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-quota", TimeProvider.System, new CaptureRejectionAudit(), store,
            new FakeAntiforgery(false), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var response = await SendMutationAsync(client, await harness.IssueCookieAsync(), "status",
            "{\"isActive\":false,\"expectedManagementVersion\":\"3\"}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Check(response.StatusCode == HttpStatusCode.Conflict
            && json.RootElement.GetProperty("code").GetString() == "stale_management_version"
            && quota.Calls == 1 && store.MutationCalls == 1
            && quota.Requests.Single() is { Policy: "admin-mutation", Subject: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", NormalizedIp: null },
            "actor-only admin-mutation quota must complete before and survive the later stale outcome");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "mutation-rejections-are-best-effort-audited-without-changing-errors":
    {
        var sink = new CaptureAuditSink { Fail = true };
        var audit = new IdentitySecurityAudit(sink, NullLogger<IdentitySecurityAudit>.Instance);
        var store = new FakeAdministrationStore
        {
            NextMutationOutcome = new(AdminUserMutationOutcomeKind.StaleManagementVersion)
        };
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-audit", TimeProvider.System, audit, store,
            new FakeAntiforgery(false), new FakeRateLimiter());
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        using var response = await SendMutationAsync(client, await harness.IssueCookieAsync(), "role",
            "{\"role\":\"validador\",\"expectedManagementVersion\":\"19\"}");
        var responseText = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(responseText);
        Check(response.StatusCode == HttpStatusCode.Conflict
            && json.RootElement.GetProperty("code").GetString() == "stale_management_version"
            && sink.Rows.Count == 1 && sink.IndependentTransactionWrites == 1
            && sink.Rows[0].Action == IdentitySecurityActions.ConcurrencyReject
            && !sink.Rows[0].SecurityContextJson.Contains("Cookie", StringComparison.OrdinalIgnoreCase)
            && !sink.Rows[0].SecurityContextJson.Contains("csrf", StringComparison.OrdinalIgnoreCase)
            && !responseText.Contains("test-only", StringComparison.OrdinalIgnoreCase),
            "best-effort audit failure must preserve the exact rejection and never expose request/security material");

        var unavailableStore = new FakeAdministrationStore
        {
            MutationException = new IdentityAdminUnavailableException(
                new InvalidOperationException("Host=private;Password=never-expose"))
        };
        await using var unavailableHarness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-audit-service", TimeProvider.System, audit, unavailableStore,
            new FakeAntiforgery(false), new FakeRateLimiter());
        using var unavailableClient = new HttpClient { BaseAddress = unavailableHarness.BaseAddress };
        using var unavailable = await SendMutationAsync(
            unavailableClient, await unavailableHarness.IssueCookieAsync(), "status",
            "{\"isActive\":false,\"expectedManagementVersion\":\"19\"}");
        var unavailableText = await unavailable.Content.ReadAsStringAsync();
        using var unavailableJson = JsonDocument.Parse(unavailableText);
        Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable
            && unavailableJson.RootElement.GetProperty("code").GetString() == "service_unavailable"
            && sink.Rows.Count == 2
            && sink.Rows.Count(row => row.Action == IdentitySecurityActions.ValidationReject) == 1
            && !unavailableText.Contains("private", StringComparison.OrdinalIgnoreCase)
            && !unavailableText.Contains("Password", StringComparison.OrdinalIgnoreCase),
            "store, constraint or success-audit failure must receive one endpoint-owned best-effort audit and generic 503");
        Console.WriteLine("PASS " + scenario);
        break;
    }
    case "same-version-has-single-writer-and-rotates-both-stamps":
    {
        var winners = 0;
        var store = new FakeAdministrationStore
        {
            MutationHandler = command => Interlocked.Increment(ref winners) == 1
                ? new(AdminUserMutationOutcomeKind.Updated,
                    new(command.Intent.TargetUserId.ToString("D"), "target.user", "Target User",
                        "validador", true, false, command.Now.AddDays(-1), command.Now, "10"))
                : new(AdminUserMutationOutcomeKind.StaleManagementVersion)
        };
        var quota = new FakeRateLimiter();
        await using var harness = await StepUpHttpHarness.StartAsync(
            args[1] + ".task12-race", TimeProvider.System, new CaptureRejectionAudit(), store,
            new FakeAntiforgery(false), quota);
        using var client = new HttpClient { BaseAddress = harness.BaseAddress };
        var cookie = await harness.IssueCookieAsync();
        var body = "{\"role\":\"validador\",\"expectedManagementVersion\":\"9\"}";
        var firstTask = SendMutationAsync(client, cookie, "role", body);
        var secondTask = SendMutationAsync(client, cookie, "role", body);
        var responses = await Task.WhenAll(firstTask, secondTask);
        try
        {
            Check(responses.Count(item => item.StatusCode == HttpStatusCode.OK) == 1
                && responses.Count(item => item.StatusCode == HttpStatusCode.Conflict) == 1
                && store.MutationCalls == 2 && quota.Calls == 2,
                "two HTTP writers with the same expected version must expose one winner and one stale conflict");
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
        Console.WriteLine("PASS " + scenario);
        break;
    }
    default:
        throw new InvalidOperationException($"Unknown user-administration scenario: {scenario ?? "<missing>"}");
}

IConfiguration RealPostgresConfiguration(bool includeIdentityAdmin, bool includeHmac = false)
{
    var values = new Dictionary<string, string?>
    {
        ["ConnectionStrings:Postgres"] = Environment.GetEnvironmentVariable("GDS_TEST_PRIMARY_CONNECTION")
    };
    if (includeIdentityAdmin)
        values["ConnectionStrings:IdentityAdmin"] = Environment.GetEnvironmentVariable("GDS_TEST_IDENTITY_ADMIN_CONNECTION");
    if (includeHmac)
    {
        values["IdentitySecurity:RateLimitHmacKeys:0:KeyId"] = "rate-limit-v1";
        values["IdentitySecurity:RateLimitHmacKeys:0:SecretBase64"] = Convert.ToBase64String(new byte[32]);
    }
    return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}

async Task<HttpResponseMessage> SendMutationAsync(
    HttpClient client, string stepUpCookie, string suffix, string json)
    => await SendMutationToTargetAsync(
        client, stepUpCookie, "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", suffix, json);

async Task<HttpResponseMessage> SendMutationToTargetAsync(
    HttpClient client, string stepUpCookie, string targetId, string suffix, string json)
{
    var encodedTargetId = Uri.EscapeDataString(targetId);
    using var request = new HttpRequestMessage(HttpMethod.Put,
        $"/api/admin/users/{encodedTargetId}/{suffix}");
    request.Headers.Add("X-Harness-Role", "administrador");
    request.Headers.Add("Cookie", $"GDS.AdminStepUp={stepUpCookie}");
    request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    return await client.SendAsync(request);
}

async Task<HttpResponseMessage> SendMutationWithHeadersAsync(
    HttpClient client, string suffix, string json, string? role, string? stepUpCookie,
    bool mustChangePassword)
{
    using var request = new HttpRequestMessage(HttpMethod.Put,
        $"/api/admin/users/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb/{suffix}");
    if (role is not null) request.Headers.Add("X-Harness-Role", role);
    if (mustChangePassword) request.Headers.Add("X-Harness-Must-Change", "true");
    if (stepUpCookie is not null)
        request.Headers.Add("Cookie", $"GDS.AdminStepUp={stepUpCookie}");
    request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    return await client.SendAsync(request);
}

PostgresDataSources RealRateLimitSources()
{
    var configuration = RealPostgresConfiguration(includeIdentityAdmin: true, includeHmac: true);
    return new PostgresDataSources(configuration, new HarnessEnvironment(), NullLogger<PostgresDataSources>.Instance);
}

IdentitySecurityOptions RateKeys(params string[] keyIds) => IdentitySecurityOptions.Validate(
    keyIds.Select(keyId => new IdentityRateLimitHmacKeyEntry(
        keyId, Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes("harness-secret:" + keyId))))).ToArray());

IdentityRateLimiter CreateRateLimiter(PostgresDataSources sources, IdentitySecurityOptions options) =>
    new(new IdentityAdminConnection(sources), options);

IdentityRateLimitRequest LoginRequest() =>
    new("login", "NORMALIZED.USER", "192.0.2.10", DateTimeOffset.UtcNow);

async Task<long> RateLimitAttemptTotal(Npgsql.NpgsqlConnection connection)
{
    await using var command = new Npgsql.NpgsqlCommand(
        "SELECT COALESCE(sum(attempt_count), 0) FROM app.identity_rate_limits", connection);
    return Convert.ToInt64(await command.ExecuteScalarAsync());
}

async Task<Dictionary<string, long>> RateLimitAttemptTotalsByKey(Npgsql.NpgsqlConnection connection)
{
    await using var command = new Npgsql.NpgsqlCommand("""
        SELECT key_id, COALESCE(sum(attempt_count), 0)
        FROM app.identity_rate_limits
        GROUP BY key_id
        ORDER BY key_id
        """, connection);
    var result = new Dictionary<string, long>(StringComparer.Ordinal);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) result.Add(reader.GetString(0), reader.GetInt64(1));
    return result;
}

ClaimsPrincipal AdminPrincipal(string role)
{
    var identity = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa").ToString("D")),
        new Claim(ClaimTypes.Name, "harness.user"),
        new Claim(ClaimTypes.Role, role),
        new Claim(SessionAuthentication.DisplayNameClaim, "Harness User"),
        new Claim(SessionAuthentication.MustChangePasswordClaim, "false")
    }, "Harness");
    return new ClaimsPrincipal(identity);
}

DefaultHttpContext HttpContextWithQuery(string? query, IIdentityAdministrationStore store, IIdentitySecurityAudit audit)
{
    var services = new ServiceCollection().AddLogging().AddRouting()
        .AddSingleton(store).AddSingleton(audit).BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services, User = AdminPrincipal("administrador") };
    context.Request.Method = HttpMethods.Get;
    context.Request.Path = "/api/admin/users";
    context.Request.QueryString = new QueryString(query ?? string.Empty);
    context.Response.Body = new MemoryStream();
    context.TraceIdentifier = "task-9-correlation";
    return context;
}

async Task<JsonDocument> ResponseJson(HttpContext context)
{
    context.Response.Body.Position = 0;
    return await JsonDocument.ParseAsync(context.Response.Body);
}

AdminCreateUserRequest ValidCreateRequest() => new(" alpha.user ", " Álpha User ", "operador");

AdminCreateUserResponse SampleCreateResponse(string password)
{
    var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    return new(new("cccccccc-cccc-4ccc-8ccc-cccccccccccc", "alpha.user", "Álpha User",
        "operador", true, true, now, now, "1"), password);
}

DefaultHttpContext CreateContext(string idempotencyKey)
{
    var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
    var context = new DefaultHttpContext
    {
        RequestServices = services,
        User = AdminPrincipal("administrador")
    };
    context.Request.Method = HttpMethods.Post;
    context.Request.Path = "/api/admin/users";
    context.Request.Headers["Idempotency-Key"] = idempotencyKey;
    context.Response.Body = new MemoryStream();
    context.TraceIdentifier = "task-11-correlation";
    return context;
}

int AvailablePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

async Task<Npgsql.NpgsqlConnection> OpenControlConnection()
{
    var value = Environment.GetEnvironmentVariable("GDS_TEST_CONTROL_CONNECTION");
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Missing local control connection.");
    var connection = new Npgsql.NpgsqlConnection(value);
    await connection.OpenAsync();
    return connection;
}

sealed class CaptureLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    public string Text => string.Join('\n', _entries);
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);
    public void Dispose() { }

    private sealed class CaptureLogger(
        string category,
        ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            entries.Enqueue(category + ":" + formatter(state, exception)
                + (exception is null ? string.Empty : "\n" + exception));
        }
    }
}

sealed class CaptureAuditSink : IIdentitySecurityAuditSink
{
    public int IndependentTransactionWrites { get; private set; }
    public List<IdentitySecurityAuditRow> Rows { get; } = [];
    public bool Fail { get; init; }
    public Task WriteInIndependentTransactionAsync(IdentitySecurityAuditRow row, CancellationToken cancellationToken)
    {
        IndependentTransactionWrites++;
        Rows.Add(row);
        if (Fail) throw new InvalidOperationException("test-only failure");
        return Task.CompletedTask;
    }
}
sealed class CaptureRejectionAudit : IIdentitySecurityAudit
{
    public List<IdentitySecurityRejection> Rejections { get; } = [];
    public Task RecordRejectionAsync(IdentitySecurityRejection rejection, CancellationToken cancellationToken = default)
    {
        Rejections.Add(rejection);
        return Task.CompletedTask;
    }
}
sealed class FakeAdministrationStore : IIdentityAdministrationStore
{
    private int _mutationCalls;
    public int ResetCalls { get; private set; }
    public AdminResetCommand? LastReset { get; private set; }
    public AdminResetOutcome? NextResetOutcome { get; set; }
    public Task<AdminResetOutcome> ResetPasswordAsync(AdminResetCommand command, IPasswordHasher<ApplicationUser> hasher, CancellationToken cancellationToken)
    {
        ResetCalls++;
        LastReset = command;
        return Task.FromResult(NextResetOutcome ?? new AdminResetOutcome(AdminResetResult.Succeeded, "TEST-TEMPORARY-PASSWORD"));
    }
    public int ListCalls { get; private set; }
    public int CreateCalls { get; private set; }
    public int TombstoneCalls { get; private set; }
    public int MutationCalls => _mutationCalls;
    public List<int> TombstoneBatchSizes { get; } = [];
    public List<AdminUserDirectoryAudit> SuccessAudits { get; } = [];
    public AdminCreateUserOutcome? NextCreateOutcome { get; init; }
    public Exception? CreateException { get; init; }
    public Exception? MutationException { get; init; }
    public AdminUserMutationOutcome? NextMutationOutcome { get; init; }
    public Func<AdminUserMutationCommand, AdminUserMutationOutcome>? MutationHandler { get; init; }
    public ConcurrentQueue<AdminUserMutationOutcome> MutationOutcomes { get; } = new();
    public ConcurrentQueue<AdminUserMutationCommand> MutationCommands { get; } = new();
    public Task<AdminUserDirectoryResponse> ListUsersAsync(AdminUserDirectoryQuery query, CancellationToken cancellationToken)
    {
        ListCalls++;
        return Task.FromResult(new AdminUserDirectoryResponse([], query.Page, query.PageSize, 0));
    }
    public Task RecordSuccessfulListAsync(AdminUserDirectoryAudit audit, CancellationToken cancellationToken)
    {
        SuccessAudits.Add(audit);
        return Task.CompletedTask;
    }
    public Task<AdminCreateUserOutcome> CreateUserAsync(
        AdminCreateUserCommand command,
        AdminTemporaryCredentialFactory credentialFactory,
        CancellationToken cancellationToken)
    {
        CreateCalls++;
        if (NextCreateOutcome is { Kind: not AdminCreateUserOutcomeKind.Created })
            return Task.FromResult(NextCreateOutcome);
        var credential = credentialFactory(command.Intent);
        if (CreateException is not null) throw CreateException;
        if (NextCreateOutcome is not null)
            return Task.FromResult(NextCreateOutcome);
        var response = new AdminCreateUserResponse(
            new(credential.UserId.ToString("D"), command.Intent.Username, command.Intent.DisplayName,
                command.Intent.Role, true, true, command.Now, command.Now, "1"),
            credential.TemporaryPassword);
        var body = JsonSerializer.SerializeToUtf8Bytes(response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Task.FromResult(new AdminCreateUserOutcome(
            AdminCreateUserOutcomeKind.Created, StatusCodes.Status201Created, body));
    }
    public Task<int> TombstoneExpiredReceiptsAsync(int batchSize, CancellationToken cancellationToken)
    {
        TombstoneCalls++;
        TombstoneBatchSizes.Add(batchSize);
        return Task.FromResult(0);
    }
    public Task<AdminUserMutationOutcome> MutateUserAsync(
        AdminUserMutationCommand command, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _mutationCalls);
        MutationCommands.Enqueue(command);
        if (MutationException is not null) throw MutationException;
        if (MutationHandler is not null) return Task.FromResult(MutationHandler(command));
        if (MutationOutcomes.TryDequeue(out var queued)) return Task.FromResult(queued);
        if (NextMutationOutcome is not null) return Task.FromResult(NextMutationOutcome);
        var now = command.Now;
        return Task.FromResult(new AdminUserMutationOutcome(
            AdminUserMutationOutcomeKind.Updated,
            new(command.Intent.TargetUserId.ToString("D"), "target.user", "Target User",
                command.Kind == AdminUserMutationKind.Role ? command.Intent.Role! : "operador",
                command.Kind == AdminUserMutationKind.Status ? command.Intent.IsActive!.Value : true,
                false, now.AddDays(-1), now,
                checked(command.Intent.ExpectedManagementVersion + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
sealed class FakeRateLimiter : IIdentityRateLimiter
{
    private int _calls;
    public int Calls => _calls;
    public List<IdentityRateLimitRequest> Requests { get; } = [];
    public IdentityRateLimitDecision Decision { get; init; } = new(true, null, 4);
    public Task<IdentityRateLimitDecision> ConsumeAsync(IdentityRateLimitRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests) Requests.Add(request);
        return Task.FromResult(Decision);
    }
    public Task<int> DeleteExpiredBatchAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
}
sealed class FakeAntiforgery(bool fail) : Microsoft.AspNetCore.Antiforgery.IAntiforgery
{
    public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException();
    public Microsoft.AspNetCore.Antiforgery.AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(!fail);
    public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
    public Task ValidateRequestAsync(HttpContext httpContext) => fail
        ? Task.FromException(new Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException("test"))
        : Task.CompletedTask;
}
sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
{
    public int Calls { get; private set; }
    public string HashPassword(ApplicationUser user, string password)
    {
        Calls++;
        return "COUNTING-HASH";
    }
    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword) =>
        PasswordVerificationResult.Failed;
}
sealed class HarnessAdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "HarnessAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Harness-Role", out var role))
            return Task.FromResult(AuthenticateResult.NoResult());
        var mustChange = Request.Headers.TryGetValue("X-Harness-Must-Change", out var mandatory)
            && string.Equals(mandatory.ToString(), "true", StringComparison.Ordinal);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            new Claim(ClaimTypes.Name, "http.harness"),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim(SessionAuthentication.DisplayNameClaim, "HTTP Harness"),
            new Claim(SessionAuthentication.StampClaim, "task-10-security-stamp"),
            new Claim(SessionAuthentication.MustChangePasswordClaim, mustChange ? "true" : "false")
        }, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
sealed class InjectedKeyStateReader(IdentityRateLimitKeyStateSnapshot snapshot) : IIdentityRateLimitKeyStateReader
{
    public int Calls { get; private set; }
    public Task<IdentityRateLimitKeyStateSnapshot> ReadAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(snapshot);
    }
}

sealed class HarnessEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "UserAdministrationHarness";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}
sealed class ProductionHarnessEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "UserAdministrationHarness";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now = _now.Add(value);
}

sealed class StepUpHttpHarness : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Guid _adminId;
    public Uri BaseAddress { get; private set; }
    public int MutationCalls { get; private set; }

    private StepUpHttpHarness(WebApplication app, Uri baseAddress, Guid adminId)
    {
        _app = app;
        BaseAddress = baseAddress;
        _adminId = adminId;
    }

    public static async Task<StepUpHttpHarness> StartAsync(
        string databasePath,
        TimeProvider clock,
        IIdentitySecurityAudit audit,
        FakeAdministrationStore? administrationStore = null,
        FakeAntiforgery? antiforgery = null,
        FakeRateLimiter? rateLimiter = null,
        IPasswordHasher<ApplicationUser>? passwordHasher = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(IdentityAdministrationEndpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:" + AvailablePortForStepUp());
        builder.Services.AddAuthentication(HarnessAdminAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HarnessAdminAuthenticationHandler>(
                HarnessAdminAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddIdentityAdministrationDirectory();
        builder.Services.RemoveAll<IIdentityAdministrationStore>();
        builder.Services.AddSingleton<IIdentityAdministrationStore>(administrationStore ?? new FakeAdministrationStore());
        builder.Services.AddSingleton<IIdentitySecurityAudit>(audit);
        builder.Services.AddSingleton<Microsoft.AspNetCore.Antiforgery.IAntiforgery>(antiforgery ?? new FakeAntiforgery(false));
        builder.Services.AddSingleton<IIdentityRateLimiter>(rateLimiter ?? new FakeRateLimiter());
        builder.Services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>>(
            passwordHasher ?? new PasswordHasher<ApplicationUser>());
        builder.Services.AddSingleton(clock);
        builder.Services.AddDataProtection().SetApplicationName("Task10.StepUp.Harness");
        var options = new DbContextOptionsBuilder<ApplicationIdentityDbContext>()
            .UseSqlite("Data Source=" + databasePath).Options;
        builder.Services.AddScoped(_ => new ApplicationIdentityDbContext(options));
        builder.Services.AddScoped<AdminStepUpService>();
        builder.Services.AddScoped<AdminStepUpFilter>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<RequiredPasswordChangeFilter>();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception) when (!context.Response.HasStarted)
            {
                await IdentityHttpResults.WriteErrorAsync(context,
                    StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                    "El servicio no está disponible temporalmente.");
            }
        });
        var result = new StepUpHttpHarness(app, new Uri(app.Urls.FirstOrDefault() ?? "http://127.0.0.1"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        app.MapPost("/api/admin/test-mutation", () =>
        {
            result.MutationCalls++;
            return Results.NoContent();
        }).RequireAuthorization("AdministerUsers").RequireAdminStepUp("admin-users-create");
        app.MapGet("/api/admin/test-read", () => Results.NoContent())
            .RequireAuthorization("AdministerUsers");
        app.MapIdentityAdministrationEndpoints();
        await app.StartAsync();
        result.BaseAddress = new Uri(app.Urls.Single());

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        await database.Database.EnsureCreatedAsync();
        var role = new IdentityRole<Guid>("administrador")
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            NormalizedName = "ADMINISTRADOR"
        };
        var user = new ApplicationUser
        {
            Id = result._adminId,
            UserName = "http.harness",
            NormalizedUserName = "HTTP.HARNESS",
            DisplayName = "HTTP Harness",
            SecurityStamp = "task-10-security-stamp"
        };
        database.Roles.Add(role);
        database.Users.Add(user);
        database.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
        await database.SaveChangesAsync();
        return result;
    }

    public async Task<string> IssueCookieAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == _adminId);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<AdminStepUpService>().Issue(context, user);
        var header = context.Response.Headers.SetCookie.Single()!;
        return header.Split(';', 2)[0].Split('=', 2)[1];
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static int AvailablePortForStepUp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

sealed class RealMutationHttpHarness : IAsyncDisposable
{
    private const string TargetCookieScheme = "Task12TargetCookie";
    private readonly WebApplication _app;
    private readonly Guid _adminId;
    public Uri BaseAddress { get; private set; }

    private RealMutationHttpHarness(WebApplication app, Uri baseAddress, Guid adminId)
    {
        _app = app;
        BaseAddress = baseAddress;
        _adminId = adminId;
    }

    public static async Task<RealMutationHttpHarness> StartAsync(
        string controlConnection,
        IIdentityAdministrationStore store,
        IIdentityRateLimiter rateLimiter,
        IIdentitySecurityAudit audit,
        TimeProvider clock)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(IdentityAdministrationEndpoints).Assembly.GetName().Name
        });
        builder.WebHost.UseUrls("http://127.0.0.1:" + AvailablePortForRealMutation());
        builder.Services.AddAuthentication("Task12Selector")
            .AddScheme<AuthenticationSchemeOptions, HarnessAdminAuthenticationHandler>(
                HarnessAdminAuthenticationHandler.SchemeName, _ => { })
            .AddCookie(TargetCookieScheme, options =>
            {
                options.Cookie.Name = "GDS.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.Path = "/";
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = false;
                options.Events.OnRedirectToLogin = context => IdentityHttpResults.WriteErrorAsync(
                    context.HttpContext, StatusCodes.Status401Unauthorized,
                    "authentication_required", "Se requiere una sesión activa.");
                options.Events.OnValidatePrincipal = async context =>
                {
                    var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var stamp = context.Principal?.FindFirstValue(SessionAuthentication.StampClaim);
                    var database = context.HttpContext.RequestServices
                        .GetRequiredService<ApplicationIdentityDbContext>();
                    var user = Guid.TryParseExact(id, "D", out var userId)
                        && !string.IsNullOrEmpty(stamp)
                        ? await database.Users.AsNoTracking().SingleOrDefaultAsync(
                            item => item.Id == userId, context.HttpContext.RequestAborted)
                        : null;
                    if (user is not { IsActive: true }
                        || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
                        context.RejectPrincipal();
                    else
                        context.ShouldRenew = false;
                };
            })
            .AddPolicyScheme("Task12Selector", null, options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey("X-Harness-Role")
                        ? HarnessAdminAuthenticationHandler.SchemeName
                        : TargetCookieScheme;
            });
        builder.Services.AddIdentityAdministrationDirectory();
        builder.Services.RemoveAll<IIdentityAdministrationStore>();
        builder.Services.AddSingleton<IIdentityAdministrationStore>(store);
        builder.Services.AddSingleton<IIdentityRateLimiter>(rateLimiter);
        builder.Services.AddSingleton<IIdentitySecurityAudit>(audit);
        builder.Services.AddSingleton<Microsoft.AspNetCore.Antiforgery.IAntiforgery>(
            new FakeAntiforgery(false));
        builder.Services.AddSingleton(clock);
        builder.Services.AddDataProtection().SetApplicationName("Task12.RealMutation.Harness");
        builder.Services.AddDbContext<ApplicationIdentityDbContext>(options =>
            options.UseNpgsql(controlConnection));
        builder.Services.AddScoped<AdminStepUpService>();
        builder.Services.AddScoped<AdminStepUpFilter>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<RequiredPasswordChangeFilter>();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception) when (!context.Response.HasStarted)
            {
                await IdentityHttpResults.WriteErrorAsync(context,
                    StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                    "El servicio no está disponible temporalmente.");
            }
        });
        app.MapGet("/api/harness/session-probe", () => Results.NoContent())
            .RequireAuthorization();
        app.MapIdentityAdministrationEndpoints();
        var result = new RealMutationHttpHarness(
            app, new Uri(app.Urls.FirstOrDefault() ?? "http://127.0.0.1"),
            Guid.Parse("00000000-0000-4000-8000-000000000001"));
        await app.StartAsync();
        result.BaseAddress = new Uri(app.Urls.Single());
        return result;
    }

    public async Task<string> IssueStepUpCookieAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == _adminId);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        scope.ServiceProvider.GetRequiredService<AdminStepUpService>().Issue(context, user);
        return CookieValue(context.Response.Headers.SetCookie.Single()!, AdminStepUpService.CookieName);
    }

    public async Task<string> IssueTargetSessionCookieAsync(Guid targetId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>();
        var user = await database.Users.AsNoTracking().SingleAsync(item => item.Id == targetId);
        var role = await (from membership in database.UserRoles
                          join item in database.Roles on membership.RoleId equals item.Id
                          where membership.UserId == targetId
                          select item.Name!).SingleAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim(ClaimTypes.Role, role),
            new Claim(SessionAuthentication.DisplayNameClaim, user.DisplayName),
            new Claim(SessionAuthentication.StampClaim, user.SecurityStamp!),
            new Claim(SessionAuthentication.MustChangePasswordClaim, "false")
        }, TargetCookieScheme));
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        await context.SignInAsync(TargetCookieScheme, principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
        return CookieValue(context.Response.Headers.SetCookie.Single()!, "GDS.Session");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string CookieValue(string header, string name)
    {
        var pair = header.Split(';', 2)[0].Split('=', 2);
        if (pair.Length != 2 || pair[0] != name) throw new InvalidOperationException("Unexpected test cookie.");
        return pair[1];
    }

    private static int AvailablePortForRealMutation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
