using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Notes.Api.Data;
using Notes.Api.Database;
using Notes.Api.Models;

namespace Notes.Api.Import;

/// <summary>
/// Deployment-only legacy import adapter. Every source record is archived and
/// normalized in one transaction; runtime credentials are intentionally unable
/// to access the protected import tables.
/// </summary>
public sealed class PostgresLegacyImportSink : ILegacyImportSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource dataSource;

    public PostgresLegacyImportSink(PostgresDataSources database) : this(database.Primary) { }

    public PostgresLegacyImportSink(NpgsqlDataSource dataSource) =>
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task EnsureBatchAsync(LegacyImportBatch batch, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await RequireDeploymentRoleAndApprover(connection, transaction, batch.ApprovedByUserId, cancellationToken);
        await RequireActiveOwners(connection, transaction, batch.OwnerMapping, cancellationToken);

        await using (var insert = Command("""
            INSERT INTO app.import_batches
                (id, source, source_file_name, source_sha256, source_size_bytes, status, analysis,
                 owner_mapping, time_zone_mapping, record_count, approved_at, approved_by_user_id)
            VALUES
                (@id, @source, @file, @hash, @bytes, 'running', @analysis, @owners, @zones,
                 @records, now(), @approver)
            ON CONFLICT (source, source_sha256) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", batch.Id);
            insert.Parameters.AddWithValue("source", batch.SourceName);
            insert.Parameters.AddWithValue("file", batch.SourceFileName);
            insert.Parameters.AddWithValue("hash", batch.SourceHash);
            insert.Parameters.AddWithValue("bytes", batch.SourceBytes);
            insert.Parameters.Add("analysis", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.SafeReport, Json);
            insert.Parameters.Add("owners", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.OwnerMapping, Json);
            insert.Parameters.Add("zones", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.TimeZoneMapping, Json);
            insert.Parameters.AddWithValue("records", batch.RecordCount);
            insert.Parameters.AddWithValue("approver", batch.ApprovedByUserId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var existing = Command("""
            SELECT id, source_file_name, source_size_bytes, record_count, approved_by_user_id,
                   analysis=@analysis, owner_mapping=@owners, time_zone_mapping=@zones
            FROM app.import_batches
            WHERE source=@source AND source_sha256=@hash
            FOR UPDATE
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("source", batch.SourceName);
            existing.Parameters.AddWithValue("hash", batch.SourceHash);
            existing.Parameters.Add("analysis", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.SafeReport, Json);
            existing.Parameters.Add("owners", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.OwnerMapping, Json);
            existing.Parameters.Add("zones", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(batch.TimeZoneMapping, Json);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetGuid(0) != batch.Id
                || reader.GetString(1) != batch.SourceFileName
                || reader.GetInt64(2) != batch.SourceBytes
                || reader.GetInt32(3) != batch.RecordCount
                || reader.GetGuid(4) != batch.ApprovedByUserId
                || !reader.GetBoolean(5)
                || !reader.GetBoolean(6)
                || !reader.GetBoolean(7))
                throw new InvalidOperationException("El lote existente no coincide con el contexto aprobado; reutiliza su UUID y archivos de correspondencia originales.");
        }

        await using (var update = Command("""
            UPDATE app.import_batches
            SET status='running', completed_at=NULL
            WHERE id=@id AND status NOT IN ('completed','completed_with_errors')
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", batch.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LegacyImportDisposition> ImportRecordAsync(
        LegacyImportBatch batch,
        LegacyRecordAnalysis record,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (var recordLock = Command(
            "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0))", connection, transaction))
        {
            recordLock.Parameters.AddWithValue(
                "identity", LegacyImportPostgresPolicy.SourceLockIdentity(batch.SourceName, record.SourceId));
            await recordLock.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? matchingLegacyRecordId = null;
        string? matchingLegacyDisposition = null;
        await using (var prior = Command("""
            SELECT id, source_hash, disposition
            FROM app.legacy_records
            WHERE source=@source AND source_record_id=@source_id
            ORDER BY created_at, id
            """, connection, transaction))
        {
            prior.Parameters.AddWithValue("source", batch.SourceName);
            prior.Parameters.AddWithValue("source_id", record.SourceId);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(1) == record.SourceHash)
                {
                    matchingLegacyRecordId = reader.GetGuid(0);
                    matchingLegacyDisposition = reader.GetString(2);
                    break;
                }
            }
        }

        Guid legacyRecordId;
        if (matchingLegacyRecordId is { } existingLegacyRecordId)
        {
            legacyRecordId = existingLegacyRecordId;
        }
        else
        {
            legacyRecordId = Guid.NewGuid();
            await InsertLegacyRecord(connection, transaction, legacyRecordId, batch, record, cancellationToken);
            matchingLegacyDisposition = "pending";
        }

        var membership = await LinkBatchRecord(
            connection, transaction, batch.Id, record.SourceIndex, legacyRecordId, cancellationToken);
        if (!membership.Created && membership.Disposition != "pending")
        {
            await transaction.CommitAsync(cancellationToken);
            return LegacyImportDisposition.AlreadyProcessed;
        }

        await transaction.SaveAsync("normalize_record", cancellationToken);

        if (record.RequiresQuarantine)
        {
            var detail = string.Join(',', record.Issues
                .Where(issue => issue.Severity == LegacyIssueSeverity.Blocking)
                .Select(issue => issue.Code)
                .Distinct());
            if (matchingLegacyDisposition != "imported")
                await MarkReconciliation(connection, transaction, legacyRecordId, "analysis-blocking", detail, cancellationToken);
            await MarkBatchReconciliation(
                connection, transaction, batch.Id, record.SourceIndex, "analysis-blocking", detail, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return LegacyImportDisposition.Quarantined;
        }

        if (matchingLegacyDisposition == "imported")
        {
            await MarkBatchImported(connection, transaction, batch.Id, record.SourceIndex, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return LegacyImportDisposition.AlreadyProcessed;
        }

        if (await HasEarlierVersion(connection, transaction, batch.SourceName, record.SourceId, record.SourceHash, cancellationToken))
        {
            await MarkReconciliation(connection, transaction, legacyRecordId, "source-record-changed",
                "Existe otra huella para el mismo registro de origen; se requiere conciliación explícita.", cancellationToken);
            await MarkBatchReconciliation(
                connection, transaction, batch.Id, record.SourceIndex, "source-record-changed",
                "Existe otra huella para el mismo registro de origen; se requiere conciliación explícita.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return LegacyImportDisposition.Quarantined;
        }

        try
        {
            var normalized = Normalize(batch, record);

            foreach (var identity in LegacyImportPostgresPolicy.TargetLockIdentities(record.InternalId, record.PublicId))
            {
                await using var targetLock = Command(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0))", connection, transaction);
                targetLock.Parameters.AddWithValue("identity", identity);
                await targetLock.ExecuteNonQueryAsync(cancellationToken);
            }

            if (await IncidentExists(connection, transaction, record.InternalId, record.PublicId, cancellationToken))
            {
                await MarkReconciliation(connection, transaction, legacyRecordId, "target-conflict",
                    "El UUID interno o ID público ya pertenece a otra incidencia.", cancellationToken);
                await MarkBatchReconciliation(
                    connection, transaction, batch.Id, record.SourceIndex, "target-conflict",
                    "El UUID interno o ID público ya pertenece a otra incidencia.", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return LegacyImportDisposition.Quarantined;
            }

            await InsertIncident(connection, transaction, batch, record, normalized, cancellationToken);
            foreach (var phase in normalized.Phases)
            {
                var phaseId = await PhaseId(connection, transaction, record.InternalId, phase.Stage, cancellationToken);
                await PostgresNoteRepository.SavePhaseDataAsync(
                    connection, transaction, phaseId, phase.Stage, phase.Data,
                    null, record.ResolvedTimeZoneId, cancellationToken);
            }
            await InsertHistory(connection, transaction, record, normalized, cancellationToken);
            await InsertLegacyValidations(connection, transaction, record, normalized, cancellationToken);
            await InsertMapping(connection, transaction, legacyRecordId, record, cancellationToken);
            await MarkImported(connection, transaction, legacyRecordId, cancellationToken);
            await MarkBatchImported(connection, transaction, batch.Id, record.SourceIndex, cancellationToken);
            await InsertAudit(connection, transaction, batch, record, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableRecordFailure(exception))
        {
            await transaction.RollbackAsync("normalize_record", cancellationToken);
            await MarkReconciliation(connection, transaction, legacyRecordId, "normalization-failed",
                "El registro no pudo persistirse sin pérdida; revise el original protegido.", cancellationToken);
            await MarkBatchReconciliation(
                connection, transaction, batch.Id, record.SourceIndex, "normalization-failed",
                "El registro no pudo persistirse sin pérdida; revise el original protegido.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return LegacyImportDisposition.Quarantined;
        }

        await transaction.CommitAsync(cancellationToken);
        return LegacyImportDisposition.Imported;
    }

    public async Task<LegacyImportResult> CompleteBatchAsync(
        LegacyImportBatch batch,
        LegacyImportAttemptResult attempt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        int declaredRecords;
        await using (var batchLock = Command(
                         "SELECT record_count FROM app.import_batches WHERE id=@id FOR UPDATE",
                         connection, transaction))
        {
            batchLock.Parameters.AddWithValue("id", batch.Id);
            var value = await batchLock.ExecuteScalarAsync(cancellationToken);
            if (value is null)
                throw new InvalidOperationException("No se encontró el lote de importación aprobado.");
            declaredRecords = (int)value;
        }
        if (declaredRecords != batch.RecordCount)
            throw new InvalidOperationException("El conteo declarado del lote no coincide con el análisis aprobado.");

        int linked;
        int imported;
        int quarantined;
        int pending;
        await using (var totals = Command("""
            SELECT
                count(br.source_index)::integer,
                count(*) FILTER (WHERE br.disposition='imported')::integer,
                count(*) FILTER (WHERE br.disposition IN ('rejected','reconciliation_required'))::integer,
                count(*) FILTER (WHERE br.disposition='pending')::integer
            FROM app.legacy_batch_records br
            JOIN app.legacy_records r ON r.id=br.legacy_record_id
            WHERE br.batch_id=@id
            """, connection, transaction))
        {
            totals.Parameters.AddWithValue("id", batch.Id);
            await using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("No se pudieron calcular los totales del lote.");
            linked = reader.GetInt32(0);
            imported = reader.GetInt32(1);
            quarantined = reader.GetInt32(2);
            pending = reader.GetInt32(3);
        }

        var completion = LegacyImportPostgresPolicy.EvaluateBatchCompletion(
            declaredRecords, linked, imported, quarantined, pending);
        await using (var update = Command("""
            UPDATE app.import_batches
            SET imported_count=@imported,
                rejected_count=@quarantined,
                status=@status,
                completed_at=CASE
                    WHEN @complete THEN COALESCE(completed_at, now())
                    ELSE NULL
                END
            WHERE id=@id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", batch.Id);
            update.Parameters.AddWithValue("imported", imported);
            update.Parameters.AddWithValue("quarantined", quarantined);
            update.Parameters.AddWithValue("status", completion.Status);
            update.Parameters.AddWithValue("complete", completion.CanComplete);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("No se encontró el lote de importación aprobado.");
        }
        await transaction.CommitAsync(cancellationToken);

        if (!completion.CanComplete)
            throw new InvalidOperationException(
                $"El lote permanece incompleto: {linked} de {declaredRecords} registros vinculados y {pending} pendientes.");

        return new LegacyImportResult(
            attempt,
            imported,
            quarantined,
            linked,
            declaredRecords,
            completion.Status);
    }

    private static async Task RequireDeploymentRoleAndApprover(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid approver, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT pg_has_role(current_user, 'gds_db_deployer', 'member'),
                   EXISTS (
                       SELECT 1
                       FROM public."AspNetUsers" u
                       JOIN public."AspNetUserRoles" ur ON ur."UserId"=u."Id"
                       JOIN public."AspNetRoles" r ON r."Id"=ur."RoleId"
                       WHERE u."Id"=@approver AND u."IsActive" AND r."NormalizedName"='ADMINISTRADOR'
                   )
            """, connection, transaction);
        command.Parameters.AddWithValue("approver", approver);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0))
            throw new InvalidOperationException("La importación requiere una conexión miembro de gds_db_deployer.");
        if (!reader.GetBoolean(1))
            throw new InvalidOperationException("La aprobación requiere un usuario administrador activo.");
    }

    private static async Task RequireActiveOwners(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyDictionary<string, Guid> ownerMapping, CancellationToken cancellationToken)
    {
        var ownerIds = LegacyImportPostgresPolicy.DistinctOwnerIds(ownerMapping);
        if (ownerIds.Length == 0) return;

        await using var command = Command("""
            SELECT count(*) = @expected
            FROM public."AspNetUsers"
            WHERE "Id" = ANY(@owners) AND "IsActive"
            """, connection, transaction);
        command.Parameters.AddWithValue("expected", ownerIds.Length);
        command.Parameters.Add("owners", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = ownerIds;
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false)) return;

        throw new InvalidOperationException(
            "Todos los custodios del mapa de propietarios deben existir y permanecer activos.");
    }

    private static async Task InsertLegacyRecord(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, LegacyImportBatch batch,
        LegacyRecordAnalysis record, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO app.legacy_records
                (id, batch_id, source, source_record_id, source_hash, original_payload_json, parsed_payload, disposition)
            VALUES (@id,@batch,@source,@source_id,@hash,@original,@parsed,'pending')
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("batch", batch.Id);
        command.Parameters.AddWithValue("source", batch.SourceName);
        command.Parameters.AddWithValue("source_id", record.SourceId);
        command.Parameters.AddWithValue("hash", record.SourceHash);
        command.Parameters.AddWithValue("original", record.OriginalJson);
        command.Parameters.Add("parsed", NpgsqlDbType.Jsonb).Value =
            LegacyImportPostgresPolicy.CanStoreParsedPayload(record.OriginalJson)
                ? record.OriginalJson
                : DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LegacyBatchMembership> LinkBatchRecord(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        int sourceIndex,
        Guid legacyRecordId,
        CancellationToken cancellationToken)
    {
        var created = false;
        await using (var insert = Command("""
            INSERT INTO app.legacy_batch_records(batch_id, source_index, legacy_record_id)
            VALUES (@batch,@source_index,@record)
            ON CONFLICT (batch_id, source_index) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("batch", batchId);
            insert.Parameters.AddWithValue("source_index", sourceIndex);
            insert.Parameters.AddWithValue("record", legacyRecordId);
            created = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        await using var linked = Command("""
            SELECT legacy_record_id, disposition
            FROM app.legacy_batch_records
            WHERE batch_id=@batch AND source_index=@source_index
            """, connection, transaction);
        linked.Parameters.AddWithValue("batch", batchId);
        linked.Parameters.AddWithValue("source_index", sourceIndex);
        await using var reader = await linked.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != legacyRecordId)
            throw new InvalidOperationException(
                "El índice del lote ya está vinculado a otro registro; reutiliza el análisis aprobado original.");
        return new LegacyBatchMembership(created, reader.GetString(1));
    }

    private static bool IsRecoverableRecordFailure(Exception exception)
    {
        if (exception is PostgresException postgres)
            return LegacyImportPostgresPolicy.IsRecoverablePostgresSqlState(postgres.SqlState);
        return exception is JsonException or InvalidDataException or FormatException or OverflowException;
    }

    private static async Task<bool> HasEarlierVersion(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string source, string sourceId,
        string currentHash, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT EXISTS (
                SELECT 1 FROM app.legacy_records
                WHERE source=@source AND source_record_id=@source_id AND source_hash<>@hash
            )
            """, connection, transaction);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("source_id", sourceId);
        command.Parameters.AddWithValue("hash", currentHash);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<bool> IncidentExists(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string publicId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT EXISTS (SELECT 1 FROM app.incidents WHERE id=@id OR public_id=@public_id)", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("public_id", publicId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task InsertIncident(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LegacyImportBatch batch,
        LegacyRecordAnalysis record, NormalizedRecord value, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO app.incidents
                (id, public_id, owner_user_id, legacy_user_id, title, content, area_of_interest,
                 latitude, longitude, status_code, revision, created_at, updated_at)
            VALUES
                (@id,@public_id,@owner,@legacy_user,@title,@content,@area,@latitude,@longitude,
                 'open',@revision,@created,@updated)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", record.InternalId);
        command.Parameters.AddWithValue("public_id", record.PublicId);
        command.Parameters.AddWithValue("owner", record.OwnerUserId!.Value);
        AddText(command, "legacy_user", record.LegacyUserId);
        command.Parameters.AddWithValue("title", value.Title);
        command.Parameters.AddWithValue("content", value.Content);
        AddText(command, "area", value.Area);
        AddDouble(command, "latitude", value.Latitude);
        AddDouble(command, "longitude", value.Longitude);
        command.Parameters.AddWithValue("revision", value.Revision);
        command.Parameters.AddWithValue("created", value.CreatedAt);
        command.Parameters.AddWithValue("updated", value.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> PhaseId(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid incidentId, string stage,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            "SELECT id FROM app.incident_phases WHERE incident_id=@incident AND stage_code=@stage", connection, transaction);
        command.Parameters.AddWithValue("incident", incidentId);
        command.Parameters.AddWithValue("stage", stage);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("No se inicializó el conjunto canónico de fases."));
    }

    private static async Task InsertHistory(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LegacyRecordAnalysis record,
        NormalizedRecord normalized, CancellationToken cancellationToken)
    {
        foreach (var item in normalized.History)
        {
            await using var command = Command("""
                INSERT INTO app.incident_events
                    (id, incident_id, client_id, occurred_at, actor_user_id, legacy_actor_name,
                     event_text, incident_revision, origin, metadata)
                VALUES (@id,@incident,@client,@at,NULL,@actor,@text,@revision,'legacy','{}'::jsonb)
                ON CONFLICT (incident_id, client_id) DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("incident", record.InternalId);
            AddText(command, "client", item.ClientId);
            command.Parameters.AddWithValue("at", item.At);
            AddText(command, "actor", item.Actor);
            command.Parameters.AddWithValue("text", item.Text);
            command.Parameters.AddWithValue("revision", normalized.Revision);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var imported = Command("""
            INSERT INTO app.incident_events
                (id, incident_id, client_id, occurred_at, actor_user_id, legacy_actor_name,
                 event_text, incident_revision, origin, metadata)
            VALUES (@id,@incident,@client,now(),NULL,@actor,
                    'Incidencia importada desde una fuente legada aprobada.',@revision,'legacy','{}'::jsonb)
            """, connection, transaction);
        var eventId = Guid.NewGuid();
        imported.Parameters.AddWithValue("id", eventId);
        imported.Parameters.AddWithValue("incident", record.InternalId);
        imported.Parameters.AddWithValue("client", $"legacy-import-{record.SourceHash}");
        AddText(imported, "actor", record.LegacyUserId ?? "Importación legada");
        imported.Parameters.AddWithValue("revision", normalized.Revision);
        await imported.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLegacyValidations(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LegacyRecordAnalysis record,
        NormalizedRecord normalized, CancellationToken cancellationToken)
    {
        foreach (var phase in normalized.Phases
                     .Where(item => item.ValidatedAt is not null)
                     .OrderBy(item => Array.IndexOf(WorkflowRules.Stages, item.Stage)))
        {
            var phaseId = await PhaseId(connection, transaction, record.InternalId, phase.Stage, cancellationToken);
            await using var command = Command("""
                INSERT INTO app.phase_validations
                    (id, phase_id, validated_at, validated_by_user_id, legacy_validator_name,
                     validated_revision, rules_version, snapshot, origin)
                VALUES (@id,@phase,@at,NULL,@validator,@revision,'legacy-import-v1',@snapshot,'legacy')
                """, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("phase", phaseId);
            command.Parameters.AddWithValue("at", phase.ValidatedAt!.Value);
            command.Parameters.AddWithValue("validator", phase.ValidatedBy!);
            command.Parameters.AddWithValue("revision", normalized.Revision);
            command.Parameters.Add("snapshot", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(new { data = phase.Data }, Json);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertMapping(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid legacyRecordId,
        LegacyRecordAnalysis record, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO app.legacy_id_mappings
                (legacy_record_id, entity_type, source_id, target_id, target_public_id,
                 legacy_user_id, mapping_status)
            VALUES (@record,'incident',@source_id,@target_id,@public_id,@legacy_user,'mapped')
            """, connection, transaction);
        command.Parameters.AddWithValue("record", legacyRecordId);
        command.Parameters.AddWithValue("source_id", record.SourceId);
        command.Parameters.AddWithValue("target_id", record.InternalId);
        command.Parameters.AddWithValue("public_id", record.PublicId);
        AddText(command, "legacy_user", record.LegacyUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAudit(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LegacyImportBatch batch,
        LegacyRecordAnalysis record, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            INSERT INTO app.audit_events
                (id, actor_user_id, action, outcome, entity_type, entity_id, changes, security_context)
            VALUES (@id,@actor,'legacy.import','succeeded','incident',@entity,
                    jsonb_build_object('batchId',@batch,'sourceHash',@hash),
                    jsonb_build_object('origin','approved-legacy-import'))
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("actor", batch.ApprovedByUserId);
        command.Parameters.AddWithValue("entity", record.InternalId.ToString());
        command.Parameters.AddWithValue("batch", batch.Id);
        command.Parameters.AddWithValue("hash", record.SourceHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkImported(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(
            """
            UPDATE app.legacy_records
            SET disposition='imported', error_code=NULL, error_detail=NULL, processed_at=now()
            WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkBatchImported(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        int sourceIndex,
        CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.legacy_batch_records
            SET disposition='imported', error_code=NULL, error_detail=NULL, processed_at=now()
            WHERE batch_id=@batch AND source_index=@source_index
            """, connection, transaction);
        command.Parameters.AddWithValue("batch", batchId);
        command.Parameters.AddWithValue("source_index", sourceIndex);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("No se encontró la ocurrencia vinculada al lote.");
    }

    private static async Task MarkBatchReconciliation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        int sourceIndex,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.legacy_batch_records
            SET disposition='reconciliation_required', error_code=@code,
                error_detail=@detail, processed_at=now()
            WHERE batch_id=@batch AND source_index=@source_index
            """, connection, transaction);
        command.Parameters.AddWithValue("batch", batchId);
        command.Parameters.AddWithValue("source_index", sourceIndex);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("detail", detail);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("No se encontró la ocurrencia vinculada al lote.");
    }

    private static async Task MarkReconciliation(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string code, string detail,
        CancellationToken cancellationToken)
    {
        await using var command = Command("""
            UPDATE app.legacy_records
            SET disposition='reconciliation_required', error_code=@code, error_detail=@detail, processed_at=now()
            WHERE id=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("detail", detail);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NormalizedRecord Normalize(LegacyImportBatch batch, LegacyRecordAnalysis record)
    {
        using var document = JsonDocument.Parse(record.OriginalJson);
        var root = document.RootElement;
        var notes = batch.SourceKind == LegacySourceKind.Notes;
        var title = notes ? Text(root, "title") : Text(root, "code");
        title = string.IsNullOrWhiteSpace(title) ? record.PublicId : title.Trim();
        var content = notes ? Text(root, "content") ?? "" : "Expediente local de detección importado para conciliación operativa.";
        var area = notes ? Text(root, "state") : Text(root, "municipality") ?? Text(root, "state");
        if (title.Length > 120 || content.Length > 10_000 || area?.Length > 100)
            throw new InvalidDataException("Los textos exceden el esquema de destino.");
        if (record.OwnerUserId is null) throw new InvalidDataException("No existe usuario custodio.");

        var latitude = Number(root, notes ? "altitude" : "latitude");
        var longitude = Number(root, "longitude");
        var createdAt = Instant(root, "createdAt", record.ResolvedTimeZoneId) ?? DateTimeOffset.UtcNow;
        var updatedAt = Instant(root, "updatedAt", record.ResolvedTimeZoneId) ?? createdAt;
        var revision = Integer(root, "revision") ?? 0;
        if (revision < 0) throw new InvalidDataException("La revisión no puede ser negativa.");

        var phases = new List<NormalizedPhase>();
        var history = new List<NormalizedHistory>();
        if (notes && root.TryGetProperty("workflow", out var workflow) && workflow.ValueKind == JsonValueKind.Object)
        {
            if (workflow.TryGetProperty("phases", out var phaseArray) && phaseArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var phase in phaseArray.EnumerateArray())
                {
                    var stage = Text(phase, "stage") ?? throw new InvalidDataException("Fase sin código.");
                    if (!WorkflowRules.Stages.Contains(stage, StringComparer.Ordinal)) throw new InvalidDataException("Fase desconocida.");
                    var data = phase.TryGetProperty("data", out var dataElement)
                        ? dataElement.Deserialize<PhaseData>(Json)
                        : null;
                    data = NormalizePhaseData(data);
                    var validatedAt = Instant(phase, "validatedAt", record.ResolvedTimeZoneId);
                    var validatedBy = Text(phase, "validatedBy")?.Trim();
                    if (validatedAt is not null && string.IsNullOrWhiteSpace(validatedBy))
                        throw new InvalidDataException("Validación legada sin nombre.");
                    if (validatedBy?.Length > 200) throw new InvalidDataException("Nombre de validación demasiado largo.");
                    phases.Add(new NormalizedPhase(stage, data, validatedAt, validatedBy));
                }
            }
            if (workflow.TryGetProperty("history", out var historyArray) && historyArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in historyArray.EnumerateArray())
                {
                    var text = Text(item, "text")?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    history.Add(new NormalizedHistory(
                        Text(item, "id"),
                        Instant(item, "at", record.ResolvedTimeZoneId) ?? createdAt,
                        Text(item, "actor")?.Trim(),
                        text));
                }
            }
        }
        return new NormalizedRecord(title, content, area, latitude, longitude, revision,
            createdAt, updatedAt, phases, history);
    }

    private static PhaseData NormalizePhaseData(PhaseData? data) => new(
        data?.Review ?? new PhaseReview(),
        string.IsNullOrWhiteSpace(data?.Soil) ? "unknown" : data.Soil,
        string.IsNullOrWhiteSpace(data?.Season) ? "unknown" : data.Season,
        data?.TerrainNotes ?? "",
        data?.Evidence ?? [],
        data?.Indications ?? PhaseOneIntake.Empty(),
        data?.Reconnaissance ?? ReconnaissanceIntake.Empty(),
        data?.Surveillance ?? SurveillanceIntake.Empty(),
        data?.Mitigation ?? MitigationIntake.Empty());

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) ? number : null;

    private static int? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number) ? number : null;

    private static DateTimeOffset? Instant(JsonElement element, string name, string? timeZoneId)
    {
        var value = Text(element, name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (HasExplicitOffset(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var instant)) return instant.ToUniversalTime();
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
            throw new InvalidDataException("Fecha ambigua sin zona horaria aprobada.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            throw new InvalidDataException("La hora local es ambigua o inexistente para la zona aprobada.");
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static bool HasExplicitOffset(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return true;
        var separator = trimmed.IndexOf('T');
        return separator >= 0 && (trimmed.IndexOf('+', separator) >= 0 || trimmed.IndexOf('-', separator) >= 0);
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        new(sql, connection, transaction) { CommandTimeout = 30 };

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void AddDouble(NpgsqlCommand command, string name, double? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Double).Value = value.HasValue ? value.Value : DBNull.Value;

    private sealed record NormalizedRecord(
        string Title, string Content, string? Area, double? Latitude, double? Longitude, int Revision,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<NormalizedPhase> Phases,
        IReadOnlyList<NormalizedHistory> History);

    private sealed record NormalizedPhase(
        string Stage, PhaseData Data, DateTimeOffset? ValidatedAt, string? ValidatedBy);

    private sealed record NormalizedHistory(string? ClientId, DateTimeOffset At, string? Actor, string Text);

    private sealed record LegacyBatchMembership(bool Created, string Disposition);
}
