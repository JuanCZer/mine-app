using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Notes.Api.Catalogs;
using Notes.Api.Database;
using Notes.Api.Models;

namespace Notes.Api.Data;

public sealed class PostgresNoteRepository(
    PostgresDataSources database,
    IConfiguration configuration,
    CatalogService catalogs,
    ILogger<PostgresNoteRepository> logger) : INoteRepository, IMapRepository
{
    private readonly int _maximumListResults = Math.Clamp(configuration.GetValue<int?>("Notes:MaximumListResults") ?? 100, 1, 500);
    private readonly int _maximumHistoryResults = Math.Clamp(configuration.GetValue<int?>("Notes:MaximumHistoryResults") ?? 500, 1, 2000);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Note>> List(string userId, CancellationToken cancellationToken = default) =>
        await Database(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var ids = new List<string>();
            await using (var command = OwnedCommand("""
                SELECT public_id
                FROM app.incidents
                WHERE deleted_at IS NULL AND ((@owner_id IS NOT NULL AND owner_user_id = @owner_id) OR legacy_user_id = @user_id)
                ORDER BY updated_at DESC, id
                LIMIT @limit
                """, connection, transaction, userId))
            {
                command.Parameters.AddWithValue("limit", _maximumListResults);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
            }

            var notes = new List<Note>(ids.Count);
            foreach (var id in ids)
                if (await LoadAsync(connection, transaction, userId, id, false, cancellationToken) is { } loaded) notes.Add(loaded.Note);
            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<Note>)notes;
        }, cancellationToken);

    public Task<MapSectors> Sectors(CancellationToken cancellationToken) => ProximityCounter.ExecuteAsync(configuration,
        (maximum, token) => Database(async connection =>
        {
            var notes = new List<(double Latitude, double Longitude)>();
            await using var command = Command("""
                SELECT latitude, longitude
                FROM app.incidents
                WHERE deleted_at IS NULL AND latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180
                ORDER BY public_id COLLATE "C"
                LIMIT @maximum_plus_one
                """, connection, null);
            command.Parameters.AddWithValue("maximum_plus_one", maximum + 1);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) notes.Add((reader.GetDouble(0), reader.GetDouble(1)));
            return notes;
        }, token), cancellationToken);

    public Task<MapIncidentPage> Incidents(MapBounds bounds, string? after, CancellationToken cancellationToken) => Database(async connection =>
    {
        await using var command = Command("""
            SELECT public_id, title, content, area_of_interest, latitude, longitude, status_code, created_at
            FROM app.incidents
            WHERE deleted_at IS NULL AND latitude BETWEEN @south AND @north AND longitude BETWEEN @west AND @east
              AND (@after IS NULL OR public_id COLLATE "C" > @after COLLATE "C")
            ORDER BY public_id COLLATE "C" LIMIT 501
            """, connection, null);
        command.Parameters.AddWithValue("south", bounds.South);
        command.Parameters.AddWithValue("north", bounds.North);
        command.Parameters.AddWithValue("west", bounds.West);
        command.Parameters.AddWithValue("east", bounds.East);
        AddText(command, "after", after);
        var items = new List<MapIncident>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new MapIncident(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetString(6), Timestamp(reader, 7)));
        var hasMore = items.Count > 500;
        if (hasMore) items.RemoveAt(500);
        return new MapIncidentPage(items, hasMore ? items[^1].Id : null);
    }, cancellationToken);

    public Task<Note?> SharedDetail(string id, CancellationToken cancellationToken) => Database(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var result = await LoadAsync(connection, transaction, "", id, false, cancellationToken, shared: true);
        await transaction.CommitAsync(cancellationToken);
        return result?.Note;
    }, cancellationToken);

    public async Task<Note?> Get(string userId, string id, CancellationToken cancellationToken = default) =>
        await Database(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var loaded = await LoadAsync(connection, transaction, userId, id, false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return loaded?.Note;
        }, cancellationToken);

    public async Task<Note> Create(string userId, NoteInput input, CancellationToken cancellationToken = default) =>
        await Database(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var incidentId = Guid.NewGuid();
            var publicId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await using (var command = Command("""
                INSERT INTO app.incidents
                    (id, public_id, owner_user_id, legacy_user_id, title, content, area_of_interest, latitude, longitude,
                     status_code, revision, created_at, updated_at)
                VALUES
                    (@id, @public_id, @owner_id, @legacy_user_id, @title, @content, @area, @latitude, @longitude,
                     'open', 0, @now, @now)
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("id", incidentId);
                command.Parameters.AddWithValue("public_id", publicId);
                AddOwner(command, userId, "owner_id", "legacy_user_id");
                command.Parameters.AddWithValue("title", input.Title?.Trim() ?? "");
                command.Parameters.AddWithValue("content", input.Content?.Trim() ?? "");
                AddText(command, "area", input.State?.Trim());
                AddDouble(command, "latitude", input.Altitude);
                AddDouble(command, "longitude", input.Longitude);
                command.Parameters.AddWithValue("now", now);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertEventAsync(connection, transaction, incidentId, userId, "Incidencia registrada. Fase 1 habilitada.", 0, cancellationToken);
            await InsertAuditAsync(connection, transaction, userId, incidentId, "incident.create", "succeeded", 0, cancellationToken);
            var created = (await LoadAsync(connection, transaction, userId, publicId, false, cancellationToken))!.Note;
            await transaction.CommitAsync(cancellationToken);
            return created;
        }, cancellationToken);

    public Task<Note?> Update(string userId, string id, NoteInput input, CancellationToken cancellationToken = default) =>
        Mutate(userId, id, async (connection, transaction, loaded) =>
        {
            WorkflowRules.CheckRevision(loaded.Note, input.ExpectedRevision);
            if (loaded.Note.Workflow!.Phases.Any(phase => phase.ValidatedAt is not null))
                throw new WorkflowException("El registro inicial está vinculado a una fase validada y es de solo lectura.");
            await using var command = Command("""
                UPDATE app.incidents
                SET title=@title, content=@content, area_of_interest=@area, latitude=@latitude, longitude=@longitude,
                    revision=revision+1, updated_at=now()
                WHERE id=@id AND revision=@expected
                """, connection, transaction);
            command.Parameters.AddWithValue("title", input.Title?.Trim() ?? "");
            command.Parameters.AddWithValue("content", input.Content?.Trim() ?? "");
            AddText(command, "area", input.State?.Trim());
            AddDouble(command, "latitude", input.Altitude);
            AddDouble(command, "longitude", input.Longitude);
            command.Parameters.AddWithValue("id", loaded.InternalId);
            command.Parameters.AddWithValue("expected", loaded.Note.Revision);
            EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
            await InsertAuditAsync(connection, transaction, userId, loaded.InternalId, "incident.update", "succeeded", loaded.Note.Revision + 1, cancellationToken);
        }, cancellationToken);

    public Task<bool> Delete(string userId, string id, int expectedRevision, CancellationToken cancellationToken = default) =>
        Database(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var loaded = await LoadAsync(connection, transaction, userId, id, true, cancellationToken);
            if (loaded is null) { await transaction.RollbackAsync(cancellationToken); return false; }
            WorkflowRules.CheckRevision(loaded.Note, expectedRevision);
            await using var command = Command("""
                UPDATE app.incidents
                SET deleted_at=now(), deleted_by_user_id=@actor_id, revision=revision+1, updated_at=now()
                WHERE id=@id AND deleted_at IS NULL AND revision=@expected
                """, connection, transaction);
            AddGuid(command, "actor_id", UserGuid(userId));
            command.Parameters.AddWithValue("id", loaded.InternalId);
            command.Parameters.AddWithValue("expected", loaded.Note.Revision);
            EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
            await InsertAuditAsync(connection, transaction, userId, loaded.InternalId, "incident.delete", "succeeded", loaded.Note.Revision + 1, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<Note?> SavePhase(string userId, string id, string stage, SavePhaseRequest request, CancellationToken cancellationToken = default) =>
        Mutate(userId, id, async (connection, transaction, loaded) =>
        {
            WorkflowRules.CheckRevision(loaded.Note, request.ExpectedRevision);
            var current = WorkflowRules.ActivePhase(loaded.Note, stage);
            WorkflowRules.CheckDraft(request.Data);
            await catalogs.ValidateSelectionsAsync(
                request.Data,
                stage,
                current.Data,
                (key, code, selectedStage, token) => FindCatalogInTransactionAsync(
                    connection, transaction, key, code, selectedStage, token),
                cancellationToken);
            var phaseId = loaded.PhaseIds[stage];
            await SavePhaseDataAsync(connection, transaction, phaseId, stage, request.Data, UserGuid(userId), null, cancellationToken);
            var revision = await IncrementRevisionAsync(connection, transaction, loaded.InternalId, loaded.Note.Revision, null, cancellationToken);
            var number = Array.IndexOf(WorkflowRules.Stages, stage) + 1;
            await InsertEventAsync(connection, transaction, loaded.InternalId, userId, $"Borrador guardado: fase {number}. No habilita la siguiente fase.", revision, cancellationToken, request.Data.Review.Reviewer);
            await InsertAuditAsync(connection, transaction, userId, loaded.InternalId, "phase.save", "succeeded", revision, cancellationToken, stage);
        }, cancellationToken);

    public Task<Note?> ValidatePhase(string userId, string id, string stage, ValidatePhaseRequest request, CancellationToken cancellationToken = default)
    {
        var actorUserId = UserGuid(userId);
        if (actorUserId is null)
            throw new WorkflowException("Se requiere un usuario autenticado para validar una fase.", 403);

        return Mutate(userId, id, async (connection, transaction, loaded) =>
        {
            WorkflowRules.CheckRevision(loaded.Note, request.ExpectedRevision);
            var phase = WorkflowRules.ActivePhase(loaded.Note, stage);
            WorkflowRules.CheckDraft(phase.Data);
            var catalogSnapshot = await catalogs.ValidateSelectionsAsync(
                phase.Data,
                stage,
                phase.Data,
                (key, code, selectedStage, token) => FindCatalogInTransactionAsync(
                    connection, transaction, key, code, selectedStage, token),
                cancellationToken);
            var issues = WorkflowRules.ValidationIssues(loaded.Note, stage, phase.Data);
            if (issues.Count > 0) throw new WorkflowException("La fase no cumple los requisitos de validación.", 422, issues);

            var phaseId = loaded.PhaseIds[stage];
            var now = DateTimeOffset.UtcNow;
            var revision = loaded.Note.Revision + 1;
            await using (var validation = Command("""
                INSERT INTO app.phase_validations
                    (id, phase_id, validated_at, validated_by_user_id, legacy_validator_name, validated_revision, rules_version, snapshot, origin)
                VALUES (@id, @phase_id, @at, @actor_id, @legacy_name, @revision, 'workflow-v1', @snapshot, @origin)
                """, connection, transaction))
            {
                validation.Parameters.AddWithValue("id", Guid.NewGuid());
                validation.Parameters.AddWithValue("phase_id", phaseId);
                validation.Parameters.AddWithValue("at", now);
                AddGuid(validation, "actor_id", actorUserId);
                AddText(validation, "legacy_name", null);
                validation.Parameters.AddWithValue("revision", revision);
                validation.Parameters.Add("snapshot", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(
                    new PhaseValidationSnapshot(phase.Data, catalogSnapshot), Json);
                validation.Parameters.AddWithValue("origin", "authenticated");
                await validation.ExecuteNonQueryAsync(cancellationToken);
            }

            var status = stage == "mitigation" ? "closed" : stage == "reconnaissance" && phase.Data.Review.Finding == "false-positive" ? "discarded" : "open";
            await IncrementRevisionAsync(connection, transaction, loaded.InternalId, loaded.Note.Revision, status, cancellationToken);
            var number = Array.IndexOf(WorkflowRules.Stages, stage) + 1;
            var text = status == "discarded" ? "Fase 2 validada. Proceso cerrado como falso positivo." : status == "closed" ? "Fase 4 validada. Proceso completo con informe de cierre y TECHINT." : $"Fase {number} validada. Fase {number + 1} habilitada.";
            await InsertEventAsync(connection, transaction, loaded.InternalId, userId, text, revision, cancellationToken, phase.Data.Review.Reviewer);
            await InsertAuditAsync(connection, transaction, userId, loaded.InternalId, "phase.validate", "succeeded", revision, cancellationToken, stage);
        }, cancellationToken);
    }

    private async Task<Note?> Mutate(
        string userId,
        string publicId,
        Func<NpgsqlConnection, NpgsqlTransaction, LoadedNote, Task> action,
        CancellationToken cancellationToken)
    {
        return await Database(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var loaded = await LoadAsync(connection, transaction, userId, publicId, true, cancellationToken);
            if (loaded is null) { await transaction.RollbackAsync(cancellationToken); return null; }
            await action(connection, transaction, loaded);
            await transaction.CommitAsync(cancellationToken);
            return (await LoadAsync(connection, null, userId, publicId, false, cancellationToken))!.Note;
        }, cancellationToken);
    }

    private async Task<T> Database<T>(Func<NpgsqlConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.Primary.OpenConnectionAsync(cancellationToken);
            return await action(connection);
        }
        catch (WorkflowException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            logger.LogWarning(exception, "PostgreSQL incident operation failed.");
            throw new NoteRepositoryUnavailableException(exception);
        }
    }

    private static async Task<int> IncrementRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, int expected, string? status, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.incidents
            SET revision=revision+1, updated_at=now(), status_code=COALESCE(@status, status_code)
            WHERE id=@id AND revision=@expected
            RETURNING revision
            """, connection, transaction);
        AddText(command, "status", status);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("expected", expected);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null) throw Conflict();
        return (int)value;
    }

    private static WorkflowException Conflict() => new("La incidencia cambió. Recarga la versión actual antes de guardar o validar.");
    private static void EnsureOne(int affected) { if (affected != 1) throw Conflict(); }

    private sealed record LoadedNote(Guid InternalId, Note Note, IReadOnlyDictionary<string, Guid> PhaseIds);

    private static NpgsqlCommand OwnedCommand(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, string userId)
    {
        var command = Command(sql, connection, transaction);
        AddGuid(command, "owner_id", UserGuid(userId));
        command.Parameters.AddWithValue("user_id", userId);
        return command;
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction) => new(sql, connection, transaction);
    private static Guid? UserGuid(string userId) => Guid.TryParse(userId, out var id) ? id : null;
    private static object Db(object? value) => value ?? DBNull.Value;
    private static void AddGuid(NpgsqlCommand command, string name, Guid? value) => command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = Db(value);
    private static void AddText(NpgsqlCommand command, string name, string? value) => command.Parameters.Add(name, NpgsqlDbType.Text).Value = Db(value);
    private static void AddDouble(NpgsqlCommand command, string name, double? value) => command.Parameters.Add(name, NpgsqlDbType.Double).Value = Db(value);
    private static void AddInt(NpgsqlCommand command, string name, int? value) => command.Parameters.Add(name, NpgsqlDbType.Integer).Value = Db(value);
    private static void AddTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = Db(value);
    private static void AddOwner(NpgsqlCommand command, string userId, string guidName, string legacyName)
    {
        var guid = UserGuid(userId);
        AddGuid(command, guidName, guid);
        AddText(command, legacyName, guid is null ? userId : null);
    }

    private async Task<LoadedNote?> LoadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string userId,
        string publicId,
        bool forUpdate,
        CancellationToken cancellationToken,
        bool shared = false)
    {
        var sql = """
            SELECT id, public_id, COALESCE(owner_user_id::text, legacy_user_id), title, content, area_of_interest,
                   latitude, longitude, status_code, revision, created_at, updated_at
            FROM app.incidents
            WHERE public_id=@public_id AND deleted_at IS NULL
            """ + (shared ? " AND latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180" : " AND ((@owner_id IS NOT NULL AND owner_user_id=@owner_id) OR legacy_user_id=@user_id)") + (forUpdate ? " FOR UPDATE" : "");
        Guid internalId;
        Note note;
        await using (var command = OwnedCommand(sql, connection, transaction, userId))
        {
            command.Parameters.AddWithValue("public_id", publicId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            internalId = reader.GetGuid(0);
            note = new Note(
                reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Timestamp(reader, 10), Timestamp(reader, 11), null, reader.GetInt32(9));
        }

        var phaseIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var phaseRows = new List<(Guid Id, string Stage, string Soil, string Season, string Terrain, PhaseReview Review, DateTimeOffset? ValidatedAt, string? ValidatedBy, CatalogValidationSnapshot? CatalogSnapshot)>();
        await using (var command = Command("""
            SELECT p.id, p.stage_code, p.soil_code, p.season_code, p.terrain_notes,
                   COALESCE(r.baseline,''), COALESCE(r.comparison,''), COALESCE(r.finding_code,'pending'),
                   COALESCE(r.environment_code,'pending'), COALESCE(r.report,''), COALESCE(r.techint,''),
                   COALESCE(r.reviewer_display_name,''), COALESCE(r.notes,''),
                   v.validated_at, COALESCE(v.legacy_validator_name, v.validated_by_user_id::text),
                   v.snapshot::text
            FROM app.incident_phases p
            LEFT JOIN app.phase_reviews r ON r.phase_id=p.id
            LEFT JOIN app.phase_validations v ON v.phase_id=p.id
            WHERE p.incident_id=@incident_id
            ORDER BY array_position(ARRAY['indications','reconnaissance','surveillance','mitigation']::text[], p.stage_code)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("incident_id", internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stage = reader.GetString(1);
                var phaseId = reader.GetGuid(0);
                phaseIds[stage] = phaseId;
                phaseRows.Add((phaseId, stage, reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    new PhaseReview(reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                        reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12)),
                    reader.IsDBNull(13) ? null : Timestamp(reader, 13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : ReadCatalogSnapshot(reader.GetString(15))));
            }
        }

        var phases = new List<NotePhase>(phaseRows.Count);
        foreach (var row in phaseRows)
        {
            var evidence = await LoadEvidenceAsync(connection, transaction, row.Id, cancellationToken);
            var data = await LoadPhaseDataAsync(connection, transaction, row.Id, row.Stage,
                new PhaseData(row.Review, row.Soil, row.Season, row.Terrain, evidence,
                    PhaseOneIntake.Empty(), ReconnaissanceIntake.Empty(), SurveillanceIntake.Empty(), MitigationIntake.Empty()),
                cancellationToken);
            phases.Add(new NotePhase(row.Stage, data, row.ValidatedAt, row.ValidatedBy, row.CatalogSnapshot));
        }

        var history = new List<WorkflowEvent>();
        await using (var command = Command("""
            SELECT COALESCE(e.client_id, e.id::text), occurred_at,
                   COALESCE(e.legacy_actor_name, e.actor_user_id::text, 'Sistema'), event_text
            FROM app.incident_events e
            WHERE e.incident_id=@incident_id
            ORDER BY e.occurred_at DESC, e.id DESC
            LIMIT @history_limit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("incident_id", internalId);
            command.Parameters.AddWithValue("history_limit", _maximumHistoryResults);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                history.Add(new WorkflowEvent(reader.GetString(0), Timestamp(reader, 1), reader.GetString(2), reader.GetString(3)));
        }
        history.Reverse();

        note = note with { Workflow = new NoteWorkflow(await StatusAsync(connection, transaction, internalId, cancellationToken), phases, history) };
        return new LoadedNote(internalId, note, phaseIds);
    }

    private static async Task<string> StatusAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid incidentId, CancellationToken cancellationToken)
    {
        await using var command = Command("SELECT status_code FROM app.incidents WHERE id=@id", connection, transaction);
        command.Parameters.AddWithValue("id", incidentId);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<PhaseEvidence>> LoadEvidenceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid phaseId, CancellationToken cancellationToken)
    {
        var evidence = new List<PhaseEvidence>();
        await using var command = Command("""
            SELECT client_id, sensor_code, reference, summary, recorded_at_original, recorded_at
            FROM app.phase_evidence
            WHERE phase_id=@phase_id ORDER BY created_at, id
            """, connection, transaction);
        command.Parameters.AddWithValue("phase_id", phaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            evidence.Add(new PhaseEvidence(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                OriginalTimestamp(reader, "recorded_at_original", "recorded_at")));
        return evidence;
    }

    private static async Task<PhaseData> LoadPhaseDataAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid phaseId, string stage, PhaseData data, CancellationToken cancellationToken)
    {
        var table = stage switch
        {
            "indications" => "phase_indications", "reconnaissance" => "phase_reconnaissance",
            "surveillance" => "phase_surveillance", "mitigation" => "phase_mitigation",
            _ => throw new WorkflowException("La fase indicada no existe.", 400)
        };
        var signatures = stage == "indications" ? await LoadCodesAsync(connection, transaction,
            "SELECT signature_code FROM app.phase_indication_signatures WHERE phase_id=@phase_id ORDER BY signature_code", phaseId, cancellationToken) : [];
        var radio = stage == "surveillance" ? await LoadCodesAsync(connection, transaction,
            "SELECT technology_code FROM app.phase_radio_technologies WHERE phase_id=@phase_id ORDER BY technology_code", phaseId, cancellationToken) : [];
        var methods = stage == "surveillance" ? await LoadCodesAsync(connection, transaction,
            "SELECT method_code FROM app.phase_surveillance_methods WHERE phase_id=@phase_id ORDER BY method_code", phaseId, cancellationToken) : [];
        await using var command = Command($"SELECT * FROM app.{table} WHERE phase_id=@phase_id", connection, transaction);
        command.Parameters.AddWithValue("phase_id", phaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return data;
        switch (stage)
        {
            case "indications":
                return data with { Indications = new PhaseOneIntake(
                    S(reader,"platform_code") ?? "", B(reader,"blue_uas_ndaa_compliant"), D(reader,"flight_duration_min"),
                    D(reader,"lidar_point_rate"), D(reader,"payload_lb"), D(reader,"mission_speed_mph"), D(reader,"wind_knots"),
                    S(reader,"terrain_model_reference") ?? "", B(reader,"differential_analysis_completed"), D(reader,"surface_variation_mm"),
                    B(reader,"actionable_alert_generated"), B(reader,"personnel_not_exposed"), B(reader,"coordinate_transferred_to_veles"),
                    OriginalTimestamp(reader,"observed_at_original","observed_at"), S(reader,"collection_source_code") ?? "", signatures,
                    S(reader,"alternative_causes") ?? "", D(reader,"depression_depth_cm")) };
            case "reconnaissance":
                return data with { Reconnaissance = new ReconnaissanceIntake(
                    D(reader,"magnetic_reading_nt"), D(reader,"gpr_depth_cm"), D(reader,"rtk_accuracy_cm"), D(reader,"localization_accuracy_cm"),
                    S(reader,"probability_code") ?? "", S(reader,"priority_code") ?? "", S(reader,"georeferenced_point_reference") ?? "",
                    S(reader,"risk_map_reference") ?? "", S(reader,"ai_classification_reference") ?? "", B(reader,"fusion_completed")) };
            case "surveillance":
                return data with { Surveillance = new SurveillanceIntake(
                    OriginalTimestamp(reader,"observation_started_at_original","observation_started_at"),
                    OriginalTimestamp(reader,"observation_ended_at_original","observation_ended_at"), radio, methods,
                    S(reader,"deployment_code") ?? "", I(reader,"covered_channels"), B(reader,"duplicated_channel_configuration"),
                    I(reader,"active_device_count"), S(reader,"human_activity_code") ?? "", S(reader,"environment_decision_code") ?? "") };
            case "mitigation":
                return data with { Mitigation = new MitigationIntake(
                    B(reader,"coordinate_received"), S(reader,"platform_code") ?? "", S(reader,"protection_level_code") ?? "",
                    B(reader,"personnel_protection_confirmed"), B(reader,"equipment_protection_confirmed"), I(reader,"detection_assets"),
                    I(reader,"interrogation_assets"), B(reader,"gpr_coverage_overlap"), D(reader,"safety_margin_m"), B(reader,"remote_interrogation_completed"),
                    S(reader,"artifact_type") ?? "", S(reader,"orientation") ?? "", S(reader,"activation_mechanism") ?? "",
                    S(reader,"eod_handoff_reference") ?? "", S(reader,"neutralization_result_code") ?? "", S(reader,"route_result_code") ?? "",
                    S(reader,"techint_composition") ?? "", S(reader,"techint_manufacture") ?? "", S(reader,"techint_signature") ?? "",
                    S(reader,"catalog_feedback_reference") ?? "") };
        }
        return data;
    }

    private static async Task<List<string>> LoadCodesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Guid phaseId, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var command = Command(sql, connection, transaction);
        command.Parameters.AddWithValue("phase_id", phaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetString(0));
        return values;
    }

    private static CatalogValidationSnapshot? ReadCatalogSnapshot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("catalogs", out var catalogsElement)) return null;
            return catalogsElement.Deserialize<CatalogValidationSnapshot>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<CatalogOptionRecord?> FindCatalogInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        string code,
        string stage,
        CancellationToken cancellationToken)
    {
        if (!CatalogRegistry.TryGetTable(key, out var table))
            throw new CatalogRequestException($"La clave de catálogo '{key}' no es válida.");

        // The table identifier comes from CatalogRegistry's closed whitelist. FOR SHARE
        // prevents an administrator from changing vigencia/label until this save commits.
        var sql = $"""
            SELECT id, code, label, sort_order, is_active, version, stage_codes
            FROM app.{table}
            WHERE code = @code
              AND (cardinality(stage_codes) = 0 OR @stage = ANY(stage_codes))
            FOR SHARE
            """;
        await using var command = Command(sql, connection, transaction);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("stage", stage);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CatalogOptionRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetBoolean(4),
            reader.GetInt64(5),
            reader.GetFieldValue<string[]>(6).ToHashSet(StringComparer.Ordinal));
    }

    internal static async Task SavePhaseDataAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid phaseId, string stage,
        PhaseData data, Guid? evidenceCreatorUserId, string? legacyTimeZoneId, CancellationToken cancellationToken)
    {
        await using (var phase = Command("""
            UPDATE app.incident_phases
            SET soil_code=@soil, season_code=@season, terrain_notes=@terrain, updated_at=now()
            WHERE id=@phase_id
            """, connection, transaction))
        {
            phase.Parameters.AddWithValue("soil", data.Soil);
            phase.Parameters.AddWithValue("season", data.Season);
            phase.Parameters.AddWithValue("terrain", data.TerrainNotes);
            phase.Parameters.AddWithValue("phase_id", phaseId);
            EnsureOne(await phase.ExecuteNonQueryAsync(cancellationToken));
        }

        await using (var review = Command("""
            INSERT INTO app.phase_reviews
                (phase_id, baseline, comparison, finding_code, environment_code, report, techint, reviewer_display_name, notes)
            VALUES (@phase_id,@baseline,@comparison,@finding,@environment,@report,@techint,@reviewer,@notes)
            ON CONFLICT (phase_id) DO UPDATE SET
                baseline=EXCLUDED.baseline, comparison=EXCLUDED.comparison, finding_code=EXCLUDED.finding_code,
                environment_code=EXCLUDED.environment_code, report=EXCLUDED.report, techint=EXCLUDED.techint,
                reviewer_display_name=EXCLUDED.reviewer_display_name, notes=EXCLUDED.notes
            """, connection, transaction))
        {
            review.Parameters.AddWithValue("phase_id", phaseId);
            review.Parameters.AddWithValue("baseline", data.Review.Baseline);
            review.Parameters.AddWithValue("comparison", data.Review.Comparison);
            review.Parameters.AddWithValue("finding", data.Review.Finding);
            review.Parameters.AddWithValue("environment", data.Review.Environment);
            review.Parameters.AddWithValue("report", data.Review.Report);
            review.Parameters.AddWithValue("techint", data.Review.Techint);
            review.Parameters.AddWithValue("reviewer", data.Review.Reviewer);
            review.Parameters.AddWithValue("notes", data.Review.Notes);
            await review.ExecuteNonQueryAsync(cancellationToken);
        }

        switch (stage)
        {
            case "indications": await SaveIndicationsAsync(connection, transaction, phaseId, data.Indications ?? PhaseOneIntake.Empty(), legacyTimeZoneId, cancellationToken); break;
            case "reconnaissance": await SaveReconnaissanceAsync(connection, transaction, phaseId, data.Reconnaissance ?? ReconnaissanceIntake.Empty(), cancellationToken); break;
            case "surveillance": await SaveSurveillanceAsync(connection, transaction, phaseId, data.Surveillance ?? SurveillanceIntake.Empty(), legacyTimeZoneId, cancellationToken); break;
            case "mitigation": await SaveMitigationAsync(connection, transaction, phaseId, data.Mitigation ?? MitigationIntake.Empty(), cancellationToken); break;
            default: throw new WorkflowException("La fase indicada no existe.", 400);
        }

        await using (var delete = Command("DELETE FROM app.phase_evidence WHERE phase_id=@phase_id", connection, transaction))
        {
            delete.Parameters.AddWithValue("phase_id", phaseId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var item in data.Evidence)
        {
            await using var evidence = Command("""
                INSERT INTO app.phase_evidence
                    (id, phase_id, client_id, sensor_code, reference, summary, recorded_at,
                     recorded_at_original, created_by_user_id)
                VALUES (@id,@phase_id,@client_id,@sensor,@reference,@summary,@recorded_at,
                        @recorded_at_original,@actor_id)
                """, connection, transaction);
            evidence.Parameters.AddWithValue("id", Guid.NewGuid());
            evidence.Parameters.AddWithValue("phase_id", phaseId);
            evidence.Parameters.AddWithValue("client_id", item.Id);
            evidence.Parameters.AddWithValue("sensor", item.Sensor);
            evidence.Parameters.AddWithValue("reference", item.Reference);
            evidence.Parameters.AddWithValue("summary", item.Summary);
            AddTimestamp(evidence, "recorded_at", ParseTimestamp(item.RecordedAt, legacyTimeZoneId));
            AddText(evidence, "recorded_at_original", EmptyToNull(item.RecordedAt));
            AddGuid(evidence, "actor_id", evidenceCreatorUserId);
            await evidence.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SaveIndicationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid phaseId, PhaseOneIntake value, string? legacyTimeZoneId, CancellationToken cancellationToken)
    {
        await using (var command = Command("""
            UPDATE app.phase_indications SET
                platform_code=@platform, blue_uas_ndaa_compliant=@ndaa, flight_duration_min=@duration,
                lidar_point_rate=@lidar, payload_lb=@payload, mission_speed_mph=@speed, wind_knots=@wind,
                terrain_model_reference=@terrain_model, differential_analysis_completed=@differential,
                surface_variation_mm=@surface, actionable_alert_generated=@alert, personnel_not_exposed=@not_exposed,
                coordinate_transferred_to_veles=@transferred, observed_at=@observed_at, observed_at_original=@observed,
                collection_source_code=@source, alternative_causes=@causes, depression_depth_cm=@depth
            WHERE phase_id=@phase_id
            """, connection, transaction))
        {
            AddText(command, "platform", EmptyToNull(value.Platform));
            command.Parameters.AddWithValue("ndaa", value.BlueUasNdaaCompliant);
            AddDouble(command, "duration", value.FlightDurationMin);
            AddDouble(command, "lidar", value.LidarPointRate);
            AddDouble(command, "payload", value.PayloadLb);
            AddDouble(command, "speed", value.MissionSpeedMph);
            AddDouble(command, "wind", value.WindKnots);
            command.Parameters.AddWithValue("terrain_model", value.TerrainModelReference);
            command.Parameters.AddWithValue("differential", value.DifferentialAnalysisCompleted);
            AddDouble(command, "surface", value.SurfaceVariationMm);
            command.Parameters.AddWithValue("alert", value.ActionableAlertGenerated);
            command.Parameters.AddWithValue("not_exposed", value.PersonnelNotExposed);
            command.Parameters.AddWithValue("transferred", value.CoordinateTransferredToVeles);
            AddTimestamp(command, "observed_at", ParseTimestamp(value.ObservedAt, legacyTimeZoneId));
            AddText(command, "observed", EmptyToNull(value.ObservedAt));
            AddText(command, "source", EmptyToNull(value.CollectionSource));
            command.Parameters.AddWithValue("causes", value.AlternativeCauses);
            AddDouble(command, "depth", value.DepressionDepthCm);
            command.Parameters.AddWithValue("phase_id", phaseId);
            EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
        }
        await ReplaceCodesAsync(connection, transaction, "phase_indication_signatures", "signature_code", phaseId, value.Signatures ?? [], cancellationToken);
    }

    private static async Task SaveReconnaissanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid phaseId, ReconnaissanceIntake value, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.phase_reconnaissance SET magnetic_reading_nt=@magnetic, gpr_depth_cm=@gpr,
                rtk_accuracy_cm=@rtk, localization_accuracy_cm=@localization, probability_code=@probability,
                priority_code=@priority, georeferenced_point_reference=@point, risk_map_reference=@risk_map,
                ai_classification_reference=@ai, fusion_completed=@fusion
            WHERE phase_id=@phase_id
            """, connection, transaction);
        AddDouble(command, "magnetic", value.MagneticReadingNt);
        AddDouble(command, "gpr", value.GprDepthCm);
        AddDouble(command, "rtk", value.RtkAccuracyCm);
        AddDouble(command, "localization", value.LocalizationAccuracyCm);
        AddText(command, "probability", EmptyToNull(value.Probability));
        AddText(command, "priority", EmptyToNull(value.Priority));
        command.Parameters.AddWithValue("point", value.GeoreferencedPointReference);
        command.Parameters.AddWithValue("risk_map", value.RiskMapReference);
        command.Parameters.AddWithValue("ai", value.AiClassificationReference);
        command.Parameters.AddWithValue("fusion", value.FusionCompleted);
        command.Parameters.AddWithValue("phase_id", phaseId);
        EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task SaveSurveillanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid phaseId, SurveillanceIntake value, string? legacyTimeZoneId, CancellationToken cancellationToken)
    {
        await using (var command = Command("""
            UPDATE app.phase_surveillance SET observation_started_at=@started_at, observation_started_at_original=@started,
                observation_ended_at=@ended_at, observation_ended_at_original=@ended, deployment_code=@deployment,
                covered_channels=@channels, duplicated_channel_configuration=@duplicated, active_device_count=@devices,
                human_activity_code=@activity, environment_decision_code=@decision
            WHERE phase_id=@phase_id
            """, connection, transaction))
        {
            AddTimestamp(command, "started_at", ParseTimestamp(value.ObservationStartedAt, legacyTimeZoneId));
            AddText(command, "started", EmptyToNull(value.ObservationStartedAt));
            AddTimestamp(command, "ended_at", ParseTimestamp(value.ObservationEndedAt, legacyTimeZoneId));
            AddText(command, "ended", EmptyToNull(value.ObservationEndedAt));
            AddText(command, "deployment", EmptyToNull(value.Deployment));
            AddInt(command, "channels", value.CoveredChannels);
            command.Parameters.AddWithValue("duplicated", value.DuplicatedChannelConfiguration);
            AddInt(command, "devices", value.ActiveDeviceCount);
            AddText(command, "activity", EmptyToNull(value.HumanActivity));
            AddText(command, "decision", EmptyToNull(value.EnvironmentDecision));
            command.Parameters.AddWithValue("phase_id", phaseId);
            EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
        }
        await ReplaceCodesAsync(connection, transaction, "phase_radio_technologies", "technology_code", phaseId, value.RadioTechnologies ?? [], cancellationToken);
        await ReplaceCodesAsync(connection, transaction, "phase_surveillance_methods", "method_code", phaseId, value.Methods ?? [], cancellationToken);
    }

    private static async Task SaveMitigationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid phaseId, MitigationIntake value, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.phase_mitigation SET coordinate_received=@coordinate, platform_code=@platform,
                protection_level_code=@protection, personnel_protection_confirmed=@personnel,
                equipment_protection_confirmed=@equipment, detection_assets=@detection, interrogation_assets=@interrogation,
                gpr_coverage_overlap=@overlap, safety_margin_m=@margin, remote_interrogation_completed=@remote,
                artifact_type=@artifact, orientation=@orientation, activation_mechanism=@activation,
                eod_handoff_reference=@handoff, neutralization_result_code=@neutralization, route_result_code=@route,
                techint_composition=@composition, techint_manufacture=@manufacture, techint_signature=@signature,
                catalog_feedback_reference=@feedback
            WHERE phase_id=@phase_id
            """, connection, transaction);
        command.Parameters.AddWithValue("coordinate", value.CoordinateReceived);
        AddText(command, "platform", EmptyToNull(value.Platform));
        AddText(command, "protection", EmptyToNull(value.ProtectionLevel));
        command.Parameters.AddWithValue("personnel", value.PersonnelProtectionConfirmed);
        command.Parameters.AddWithValue("equipment", value.EquipmentProtectionConfirmed);
        AddInt(command, "detection", value.DetectionAssets);
        AddInt(command, "interrogation", value.InterrogationAssets);
        command.Parameters.AddWithValue("overlap", value.GprCoverageOverlap);
        AddDouble(command, "margin", value.SafetyMarginM);
        command.Parameters.AddWithValue("remote", value.RemoteInterrogationCompleted);
        command.Parameters.AddWithValue("artifact", value.ArtifactType);
        command.Parameters.AddWithValue("orientation", value.Orientation);
        command.Parameters.AddWithValue("activation", value.ActivationMechanism);
        command.Parameters.AddWithValue("handoff", value.EodHandoffReference);
        AddText(command, "neutralization", EmptyToNull(value.NeutralizationResult));
        AddText(command, "route", EmptyToNull(value.RouteResult));
        command.Parameters.AddWithValue("composition", value.TechintComposition);
        command.Parameters.AddWithValue("manufacture", value.TechintManufacture);
        command.Parameters.AddWithValue("signature", value.TechintSignature);
        command.Parameters.AddWithValue("feedback", value.CatalogFeedbackReference);
        command.Parameters.AddWithValue("phase_id", phaseId);
        EnsureOne(await command.ExecuteNonQueryAsync(cancellationToken));
    }

    private static async Task ReplaceCodesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string column, Guid phaseId, IReadOnlyCollection<string> values, CancellationToken cancellationToken)
    {
        // Table and column are selected exclusively by internal callers; request data is always parameterized.
        await using (var delete = Command($"DELETE FROM app.{table} WHERE phase_id=@phase_id", connection, transaction))
        {
            delete.Parameters.AddWithValue("phase_id", phaseId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var value in values)
        {
            await using var insert = Command($"INSERT INTO app.{table} (phase_id,{column}) VALUES (@phase_id,@code)", connection, transaction);
            insert.Parameters.AddWithValue("phase_id", phaseId);
            insert.Parameters.AddWithValue("code", value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static async Task InsertEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid incidentId, string userId,
        string text, int revision, CancellationToken cancellationToken, string? legacyDisplayName = null)
    {
        var actor = UserGuid(userId);
        var eventId = Guid.NewGuid();
        await using var command = Command("""
            INSERT INTO app.incident_events
                (id, incident_id, client_id, occurred_at, actor_user_id, legacy_actor_name, event_text, incident_revision, origin, metadata)
            VALUES (@id,@incident_id,@client_id,now(),@actor_id,@legacy_name,@text,@revision,@origin,'{}'::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("incident_id", incidentId);
        command.Parameters.AddWithValue("client_id", eventId.ToString("N"));
        AddGuid(command, "actor_id", actor);
        AddText(command, "legacy_name", actor is null ? (EmptyToNull(legacyDisplayName) ?? userId) : null);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("origin", actor is null ? "legacy" : "authenticated");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string userId, Guid incidentId,
        string action, string outcome, int revision, CancellationToken cancellationToken, string? stage = null)
    {
        await using var command = Command("""
            INSERT INTO app.audit_events
                (id, occurred_at, actor_user_id, action, outcome, entity_type, entity_id, changes, security_context)
            VALUES (@id,now(),@actor_id,@action,@outcome,'incident',@entity_id,@changes,'{}'::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        AddGuid(command, "actor_id", UserGuid(userId));
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("entity_id", incidentId.ToString());
        command.Parameters.Add("changes", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new { revision, stage }, Json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? S(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static bool B(NpgsqlDataReader reader, string name) => !reader.IsDBNull(reader.GetOrdinal(name)) && reader.GetBoolean(reader.GetOrdinal(name));
    private static double? D(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : Convert.ToDouble(reader.GetValue(reader.GetOrdinal(name)));
    private static int? I(NpgsqlDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
    private static DateTimeOffset Timestamp(NpgsqlDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DateTimeOffset value => value,
        DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        var value => DateTimeOffset.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture)
    };
    private static string OriginalTimestamp(NpgsqlDataReader reader, string originalName, string timestampName)
    {
        var original = S(reader, originalName);
        if (original is not null) return original;
        var ordinal = reader.GetOrdinal(timestampName);
        return reader.IsDBNull(ordinal) ? "" : Timestamp(reader, ordinal).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseTimestamp(string? value, string? legacyTimeZoneId)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('T');
        var hasOffset = trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || separator >= 0 && (trimmed.IndexOf('+', separator) >= 0 || trimmed.IndexOf('-', separator) >= 0);
        if (hasOffset && DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)) return parsed.ToUniversalTime();
        if (string.IsNullOrWhiteSpace(legacyTimeZoneId)
            || !DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var local)) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(legacyTimeZoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return null;
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
