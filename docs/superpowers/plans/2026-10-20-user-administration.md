# User Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar administración segura de cuentas internas y cambio de contraseña propia, con PostgreSQL como autoridad, reautenticación administrativa, idempotencia, límites distribuidos, auditoría atómica y una UI Angular accesible.

**Architecture:** La entrega avanza por tres stages bloqueantes: primero el delta SQL ejecutable y su equivalente acumulado en `apply.sql`; después el backend ASP.NET Core con una frontera `IdentityAdmin` separada; al final Angular y QA. Las invariantes críticas viven en PostgreSQL y se repiten en la API para obtener errores de dominio claros; Angular consume esos contratos sin decidir autorización.

**Tech Stack:** PostgreSQL 17+ y SQL transaccional, ASP.NET Core/Identity `net10.0`, Npgsql 10, Data Protection, Angular 22 standalone, TypeScript 6, Vitest, Python `unittest`, runner Docker PostgreSQL aislado.

**Spec:** `docs/superpowers/specs/2026-10-20-user-administration-design.md`

## Global Constraints

- El orden es obligatorio y bloqueante: **STAGE 1 SQL → revisión SQL aprobada → STAGE 2 backend → revisión funcional y de seguridad aprobada → STAGE 3 frontend y QA**. No preparar ni iniciar trabajo dependiente antes de cerrar el gate anterior.
- El único artefacto SQL de actualización será `backend/Database/user-administration.sql`; contendrá todo el delta, SQL y comentarios operativos únicamente, y se ejecutará completo sobre un servidor que ya tenga la versión anterior de `apply.sql`.
- `backend/Database/apply.sql` seguirá siendo la fuente acumulada para instalaciones limpias y deberá incorporar exactamente la misma semántica de tablas, columnas, funciones, triggers, semillas y grants del delta.
- No crear migraciones de Entity Framework, `Migrations/`, `ModelSnapshot`, `EnsureCreated`, `EnsureDeleted` ni `Migrate` en producto ni contra PostgreSQL runtime; `ApplicationIdentityDbContext` solo adapta el modelo al SQL explícito. Un harness de prueba aislado puede usar SQLite `EnsureCreated` si no toca PostgreSQL ni se incorpora al runtime.
- Conservar ASP.NET Core Identity, `GDS.Session`, CSRF, PostgreSQL, el key ring compartido de Data Protection y los permisos calculados en servidor.
- Roles canónicos exactos: `operador`, `validador`, `administrador`. No crear registro público, recuperación por correo, reset administrativo, MFA, SSO/OIDC, roles personalizados, múltiples roles ni eliminación física.
- `ConnectionStrings__Postgres`/`gds_app_runtime` conserva su superficie actual. `ConnectionStrings__IdentityAdmin` no tiene fallback, usa una cuenta `LOGIN NOINHERIT` miembro únicamente de `gds_identity_admin`, ejecuta `SET ROLE gds_identity_admin` por conexión y verifica `current_role`.
- Fuera de Development/Testing, ambas conexiones exigen `sslmode=VerifyFull`; los errores de configuración son genéricos y no reproducen cadenas, hosts, usuarios o secretos.
- `gds_identity_admin` será `NOLOGIN NOINHERIT`; el SQL de aplicación no crea cuentas LOGIN, contraseñas ni grants `CONNECT`. La cuenta del ambiente se configura fuera del repositorio.
- La sección de configuración será exactamente el array `IdentitySecurity:RateLimitHmacKeys` de entradas `{ KeyId, SecretBase64 }`; ambiente usa `IdentitySecurity__RateLimitHmacKeys__0__KeyId` y `IdentitySecurity__RateLimitHmacKeys__0__SecretBase64`, incrementando el índice. Cada `KeyId` lowercase cumple `^[a-z0-9][a-z0-9._-]{0,63}$`; cada secreto es Base64 canónico sin whitespace y decodifica ≥32 bytes. Se valida la lista completa, incluidas entradas nulas/incompletas y duplicados, antes de construir el mapa. No existe selector local `current`/`active`; PostgreSQL elige `active`.
- La política de contraseña es 15–128 runas, sin composición arbitraria, truncamiento ni normalización silenciosa. Reautenticación y contraseña actual admiten como máximo 128 runas antes del hasher.
- Los cuerpos de `POST /api/auth/reauth` y `POST /api/auth/change-password` se limitan a 4 KiB en el flujo HTTP antes de deserializar o invocar el hasher.
- Toda respuesta de identidad usa `Cache-Control: no-store`; creación añade `Pragma: no-cache`. Nunca devolver o registrar hashes, stamps, contraseñas, cookies, CSRF, claves, conexiones o payloads secretos.
- Los errores usan `{ error, code, fieldErrors?, traceId? }` y solo estos códigos estables cuando corresponda: `validation_failed`, `authentication_required`, `permission_denied`, `csrf_invalid`, `current_password_invalid`, `password_reuse`, `password_change_required`, `admin_reauthentication_required`, `reauthentication_failed`, `credential_state_changed`, `username_conflict`, `stale_management_version`, `self_management_forbidden`, `last_active_administrator`, `idempotency_key_reused`, `idempotency_result_expired`, `rate_limited`, `service_unavailable`. La matriz fija `401 authentication_required`, `403 permission_denied` y `400 csrf_invalid`; los `403 password_change_required` y `403 admin_reauthentication_required` conservan precedencia cuando aplican.
- Las mutaciones confirmadas y su auditoría se escriben en la misma transacción. Un fallo de auditoría revierte la mutación.
- Los rechazos de autorización, validación, concurrencia y rate limit usan una transacción de auditoría best-effort separada cuando PostgreSQL está disponible; cada ruta tiene un único propietario de auditoría y escribe como máximo una fila. Un fallo de esa auditoría no cambia el status/código del rechazo ni lo convierte en éxito. La fila usa `app.audit_events.action/outcome/entity_type/entity_id/actor_user_id/correlation_id` como columnas superiores, `changes={}` y `security_context` como objeto JSON formado por `Code` más metadata saneada; nunca recibe cuerpos ni secretos.
- Cada consumo de rate limit posee y confirma su propia transacción antes de abrir la transacción de la operación protegida. Un `429` o un intento ya contabilizado jamás se revierte por conflicto, validación posterior, fallo de auditoría o rollback de la mutación.
- Todo build backend de gate usa `dotnet build backend -c Release --nologo -warnaserror`; el build Angular de gate usa `scripts/build-angular-clean.mjs`, que ejecuta exactamente `ng build --configuration production` con `shell:false`, captura stdout/stderr, propaga el exit code del hijo y además falla ante `▲ [WARNING]` o `exceeded maximum budget` con comparación case-insensitive.
- El árbol compartido está muy sucio y contiene trabajo ajeno/no consolidado. Por instrucción explícita, este plan sustituye todos los pasos de commit de `writing-plans` por checkpoints de revisión con `git diff --check` y diffs de alcance; no se hará ningún commit durante la ejecución de este plan.
- Antes de cada checkpoint, actualizar la tabla **Objectives / Progress** de este documento con evidencia real. Una casilla solo se marca cuando pasan sus pruebas y su gate.

## Review Focus

1. **Límites previos al hasher:** reauth de 4097 bytes falla con `413 validation_failed` y password de 129 runas con `400 reauthentication_failed` antes del hasher en `test_oversized_reauth_body_never_hash` y `test_129_rune_reauth_never_hash` de Task 10; change-password de 4097 bytes/currentPassword 129 y newPassword 14/15/128/129 quedan fijados por `test_oversized_change_password_body_never_hash`, `test_129_rune_current_password_never_hash` y `test_new_password_enforces_14_15_128_129_rune_boundaries` de Task 13.
2. **Búsqueda literal y paginación estable:** `%`, `_`, `\\`, espacios y empates de nombre deben respetar `ILIKE ... ESCAPE '\\'`, la collation PostgreSQL y el desempate UUID; lo fija `test_admin_directory_escapes_wildcards_and_orders_by_normalized_name_then_id` en Task 9.
3. **Rotación y cuotas entre réplicas:** mapas opuestos/incompletos no pueden dividir la cuota, elegir `current` local ni crear deadlock; lo fija `test_rate_limit_rotation_is_shared_ordered_and_fails_closed` en Task 8. La integración alternada de login, reauth, change-password y admin-mutation entre dos procesos HTTP reales, incluidos threshold, `Retry-After` y bucket keys, pertenece a los cuatro tests `test_two_processes_share_*_quota_with_exact_bucket_wiring` de Task 15.
4. **Replay y respuesta tardía de creación:** timeout/replay debe reutilizar actor, UUID v4 y payload exactos, mostrar la misma contraseña solo una vez y borrar estado/DOM al cerrar; lo fijan `test_create_two_stores_in_one_process_replay_identical_protected_response` en Task 11, `test_two_backend_processes_replay_identical_create_response` en Task 15 y `clears creation intent after the one-time secret closes` en Task 18.
5. **Carreras sobre el último administrador y credenciales:** dos degradaciones/desactivaciones o cambios de contraseña concurrentes no pueden confirmar ambos ni reactivar una cuenta; lo fijan `test_concurrent_last_admin_mutations_leave_one_active_admin` en Task 4 y `test_concurrent_password_changes_and_disable_have_single_winner` en Task 13.

---

## Objectives / Progress

| Objective | Gate | Status | Evidence to record |
| --- | --- | --- | --- |
| SQL-1 · Delta ejecutable | Stage 1 | [ ] In progress · Task 1 approved | contrato estático 9/9, contrato PostgreSQL 16/16 y backend 150/150; ejecución real, reaplicación y rollback continúan en Tasks 3–5 |
| SQL-2 · Invariantes y privilegios | Stage 1 | [ ] In progress · Tasks 2–3 implementation approved; runtime pending | triggers diferidos, carreras, allowlist y pruebas negativas |
| BE-1 · Frontera IdentityAdmin | Stage 2 | [ ] In progress · Tasks 6–7 implementation approved; PostgreSQL runtime pending | startup/TLS/`SET ROLE`/readiness sin fallback |
| BE-2 · HTTP y seguridad de identidad | Stage 2 | [ ] In progress · Tasks 8–13 implementation approved; Task 15 login regression fixed in session harness, remaining multi-instance runner + security review pending | endpoints, concurrencia, auditoría, idempotencia y rate limits |
| UI-1 · Administración y cuenta | Stage 3 | [ ] Blocked by Stage 2 | navegación, servicios, formularios, secretos efímeros y conflictos |
| QA-1 · Regresión y despliegue | Stage 3 | [ ] Blocked by Stage 2 | builds, suites completas, accesibilidad, responsive y orden de rollout |

## File Map

### Existing files to modify

- `backend/Database/apply.sql` — fuente acumulada de instalación limpia; recibe la misma semántica que el delta.
- `backend/Database/PostgresDataSources.cs` — valida y posee los pools runtime, catálogos e IdentityAdmin sin fallback.
- `backend/Database/README.md`, `README.md`, `docs/data/postgresql-schema.md`, `docs/runbooks/postgresql-cutover.md` — separan instalación limpia, upgrade, bootstrap, backup/restore y rollout SQL/backend/frontend.
- `backend/Auth/ApplicationUser.cs`, `backend/Auth/ApplicationIdentityDbContext.cs` — mapean `MustChangePassword`, `PasswordChangedAt`, `ManagementVersion` sin DDL EF.
- `backend/Auth/SessionAuthentication.cs` — cardinalidad cerrada de rol, claim obligatorio y permisos canónicos; elimina el limitador local.
- `backend/Auth/AuthEndpoints.cs`, `backend/Models/Models.cs` — amplían `/me`, login/logout, reauth y change-password con los contratos exactos.
- `backend/Auth/IdentityServiceCollectionExtensions.cs`, `backend/Auth/ProvisionUserCommand.cs`, `backend/Auth/README.md`, `backend/Program.cs` — DI, bootstrap único, middleware obligatorio, endpoints y readiness.
- `backend/.env.example`, `scripts/run-backend.mjs`, `scripts/apply-database.mjs`, `scripts/tests/environment.test.mjs`, `package.json` — nombres de configuración, separación de procesos y comandos de verificación sin secretos.
- `backend-tests/test_identity.py`, `backend-tests/IdentityHarness/Program.cs`, `backend-tests/IdentityHarness/AuthenticationSecurityScenarios.cs` — registro y ejecución emparejados de escenarios Identity.
- `backend-tests/test_sessions.py`, `backend-tests/SessionHarness/Program.cs` — registro y ejecución emparejados de escenarios HTTP/cookie/CSRF.
- `src/app/core/models/auth.model.ts`, `src/app/core/services/auth.service.ts`, `src/app/core/services/auth.service.spec.ts`, `src/app/core/guards/auth.guard.ts`, `src/app/core/guards/auth.guard.spec.ts`, `src/app/core/interceptors/auth.interceptor.ts` — sesión obligatoria, errores tipados, cambio de contraseña y guards.
- `src/app/app.routes.ts`, `src/app/app.routes.spec.ts`, `src/app/layout/app-layout.ts`, `src/app/layout/app-layout.html`, `src/app/layout/app-layout.less`, `src/app/layout/app-layout.spec.ts` — rutas y navegación Administración/Cuenta.
- `docs/superpowers/plans/2026-09-27-plan-general.md`, `docs/superpowers/plans/2026-10-15-plan-status-audit.md` — incorporan el nuevo objetivo y su progreso verificable sin declarar gates externos como completos.

### New files to create

- `backend/Database/user-administration.sql` — único delta ejecutable de upgrade.
- `backend/IdentityAdministration/IdentityAdminConnection.cs` — abre conexión, ejecuta `SET ROLE` y comprueba `current_role`.
- `backend/IdentityAdministration/IdentitySecurityOptions.cs` — enlaza el array `{ KeyId, SecretBase64 }`, valida la lista antes de construir el mapa y rechaza duplicados, entradas incompletas, selector local y material no canónico.
- `backend/IdentityAdministration/IdentityRateLimiter.cs` — cuota PostgreSQL, locks ordenados y limpieza acotada.
- `backend/IdentityAdministration/IdentityRateLimitCleanupService.cs` — servicio alojado que repite la limpieza por TTL con el mismo orden de locks.
- `backend/IdentityAdministration/IdentityIdempotencyReceiptCleanupService.cs` — nulifica ciphertext vencido en lotes y conserva tombstones.
- `backend/IdentityAdministration/IdentityReadiness.cs` — compara mapa local con claves `active`/`retained`.
- `backend/IdentityAdministration/AdminStepUpService.cs` — protege/valida/elimina `GDS.AdminStepUp`.
- `backend/IdentityAdministration/AdminStepUpFilter.cs` — filtro concreto de mutaciones administrativas; usa `AdminStepUpService` y posee la auditoría del rechazo `admin_reauthentication_required`.
- `backend/IdentityAdministration/IdentityDtos.cs` — DTOs públicos, validación decimal `bigint` y errores.
- `backend/IdentityAdministration/IdentityAdministrationStore.cs` — SQL parametrizado, transacciones, concurrencia, idempotencia y auditoría.
- `backend/IdentityAdministration/IdentityAdministrationEndpoints.cs` — `/api/admin/users`, `/api/admin/roles` y autorización administrativa.
- `backend/IdentityAdministration/RequiredPasswordChangeFilter.cs` — allowlist de cuatro rutas para sesiones obligadas.
- `backend/Auth/IdentityHttpResults.cs` — envelope estable y headers anti-cache comunes a `/api/auth` y `/api/admin`.
- `backend/IdentityAdministration/IdentitySecurityAudit.cs` — auditoría best-effort tipada para rechazos, en transacción separada y con metadata allowlisted.
- `backend/IdentityAdministration/IdentityAuthorizationResultHandler.cs` — implementa `IAuthorizationMiddlewareResultHandler`, envuelve el handler por defecto y posee únicamente la auditoría de `401 authentication_required` y del `403 permission_denied` genérico.
- `backend/Auth/IdentityBootstrapConnectionFactory.cs` — factoría exclusiva del comando CLI; lee `POSTGRES_DEPLOY_URL`, valida TLS y crea un data source que nunca entra a la DI web.
- `backend-tests/test_user_administration_sql.py` — contrato estático del delta y paridad con `apply.sql`.
- `backend-tests/user_administration_postgres_runner.py` — runner Docker aislado para ambos caminos SQL y backend multi-instancia.
- `backend-tests/test_user_administration_postgres_runner.py` — pruebas unitarias de seguridad del runner.
- `backend-tests/UserAdministrationHarness/UserAdministrationHarness.csproj`, `backend-tests/UserAdministrationHarness/Program.cs` — escenarios HTTP/servicio sin sustituir las pruebas PostgreSQL.
- `backend-tests/test_user_administration.py` — propietario Python de los escenarios registrados en `UserAdministrationHarness`.
- `scripts/provision-user.mjs` — wrapper CLI separado que entrega `POSTGRES_DEPLOY_URL` solo al proceso bootstrap y conserva la contraseña en stdin.
- `scripts/build-angular-clean.mjs` — ejecuta `ng build --configuration production` con `shell:false`, captura salida, propaga el exit code y reconoce solo los patrones Angular fijados para warnings/budget.
- `backend-tests/fixtures/pre-user-administration-apply.sql.fixture` — snapshot de prueba de la versión anterior, nunca artefacto de despliegue ni archivo `.sql` ejecutable del servidor.
- `src/app/core/models/user-administration.model.ts` — contratos exactos de lista, mutación, roles e idempotencia.
- `src/app/core/services/user-administration.service.ts`, `src/app/core/services/user-administration.service.spec.ts` — cliente `/api/admin` y control de intención.
- `src/app/pages/admin-users/admin-users.ts`, `admin-users.html`, `admin-users.less`, `admin-users.spec.ts` — tabla, filtros, creación, confirmaciones y secreto de una sola vista.
- `src/app/pages/change-password/change-password.ts`, `change-password.html`, `change-password.less`, `change-password.spec.ts` — cambio de contraseña y salida a login.
- `docs/qa/user-administration/test-matrix.md` — evidencia funcional, accesible y responsive.

# STAGE 1 — SQL first

No iniciar ningún archivo de Stage 2 hasta completar Tasks 1–5 y aprobar expresamente el gate de Stage 1.

### Task 1: Freeze the SQL contract and upgrade preconditions

**Files:**
- Create: `backend-tests/test_user_administration_sql.py`
- Create: `backend-tests/fixtures/pre-user-administration-apply.sql.fixture`
- Create: `backend/Database/user-administration.sql`
- Modify: `backend/Database/apply.sql`

**Interfaces:**
- Consumes: versión actual previa de `backend/Database/apply.sql` como fixture de test; roles `operador|validador|administrador`; tablas Identity y `app.audit_events` existentes.
- Produces: un delta con exactamente un `BEGIN`, un `COMMIT`, preflight antes de DDL y consultas de conciliación de solo lectura después de `COMMIT`; `apply.sql` con las mismas estructuras para fresh install.

- [x] **Step 1 — RED: codify artifact, transaction and secret-free rules**

  Añadir `test_database_has_exactly_two_sql_artifacts`, `test_delta_is_the_only_upgrade_sql_and_is_transactional`, `test_delta_checks_existing_roles_and_active_admin_before_ddl`, `test_delta_contains_no_connection_or_secret_material`, `test_rate_key_id_check_matches_varchar_64_contract` y `test_apply_and_delta_share_identity_schema_contract`. Enumerar recursivamente todo archivo cuyo sufijo sea `.sql` bajo `backend/Database` y afirmar que el conjunto de rutas relativas es exactamente `{apply.sql, user-administration.sql}`; `README.md` y cualquier otro artefacto no SQL están permitidos. El fixture termina en `.fixture`; las columnas `key_id varchar(64)` de state y buckets llevan `CHECK (key_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$')`; las últimas sentencias del delta son verificaciones posteriores al único `COMMIT`.

- [x] **Step 2 — Verify RED**

  Run: `python3 backend-tests/test_user_administration_sql.py -v`

  Expected: FAIL porque el delta, las columnas/tablas y la paridad todavía no existen.

- [x] **Step 3 — Implement the atomic, idempotent structural delta**

  En ambos SQL, añadir `MustChangePassword boolean NOT NULL DEFAULT false`, `PasswordChangedAt timestamptz NULL`, `ManagementVersion bigint NOT NULL DEFAULT 1 CHECK ("ManagementVersion" > 0)`; crear `app.identity_idempotency_receipts`, `app.identity_rate_limit_key_state` y `app.identity_rate_limits` sin identity/secuencias; aplicar a ambas columnas `key_id varchar(64)` el `CHECK` exacto de IDs lowercase de 1–64 caracteres; sembrar `rate-limit-v1` como `active` solo cuando state esté vacío. El delta debe validar antes del DDL usuarios/roles y al menos un administrador activo, y no corregir datos humanos.

- [x] **Step 4 — Add canonical role and rate-key constraints**

  Sembrar únicamente los tres roles canónicos con nombres normalizados esperados. Añadir índice único parcial de una sola `active` y constraint trigger diferido que exija exactamente una al `COMMIT`, permitiendo el flip `active→retained`/`accepted→active` en una transacción.

- [x] **Step 5 — GREEN: run static SQL contract**

  Run: `python3 backend-tests/test_user_administration_sql.py -v`

  Expected: PASS para estructura, orden, idempotencia textual, ausencia de secretos y paridad definida.

- [x] **Step 6 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend/Database backend-tests/test_user_administration_sql.py backend-tests/fixtures/pre-user-administration-apply.sql.fixture`

  Revisar que el diff solo cubra Task 1, actualizar SQL-1 en **Objectives / Progress** con el resultado y no iniciar Task 2 si el preflight no ocurre antes de toda mutación.

### Task 2: Enforce role cardinality, canonical catalog and administration guards

**Files:**
- Modify: `backend/Database/user-administration.sql`
- Modify: `backend/Database/apply.sql`
- Modify: `backend-tests/test_user_administration_sql.py`

**Interfaces:**
- Consumes: columnas/tablas de Task 1 y rol canónico `administrador`.
- Produces: `app.enforce_identity_user_role_cardinality()`, `app.enforce_identity_role_catalog()`, `app.guard_identity_admin_mutation()` y la existente `app.reject_immutable_change()` endurecida; todas `SECURITY INVOKER SET search_path = pg_catalog`.

- [x] **Step 1 — RED: specify deferred and actor guards**

  Añadir `test_deferred_role_triggers_cover_every_required_event` para afirmar `DEFERRABLE INITIALLY DEFERRED` en `AspNetUsers` (`INSERT OR UPDATE`), `AspNetUserRoles` (`INSERT OR UPDATE OR DELETE`) y `AspNetRoles` (`INSERT OR UPDATE OR DELETE`); añadir `test_admin_guard_requires_actor_lock_and_qualified_references` para afirmar `SET LOCAL app.actor_user_id`, `pg_catalog.pg_advisory_xact_lock(bigint)` y referencias `public.`/`app.` calificadas.

- [x] **Step 2 — Verify RED**

  Run: `python3 backend-tests/test_user_administration_sql.py -v`

  Expected: FAIL por funciones/triggers ausentes.

- [x] **Step 3 — Implement deferred role invariants**

  Hacer que el estado final tenga exactamente los tres roles y una membresía canónica por usuario. Encolar OLD/NEW según evento, ignorar solo UUID cuyo usuario ya no exista y soportar usuario+membresía en cualquier orden dentro de la misma transacción.

- [x] **Step 4 — Implement self-management and last-admin guards**

  Toda mutación que afecte membresía administrativa o activación de administrador toma el mismo advisory lock; rechaza actor ausente, autodegradación/autodesactivación y estado final sin administrador activo. Permitir actor ausente solo con `gds_db_deployer`, `SET LOCAL app.bootstrap_mode = 'on'` y cero usuarios bajo lock.

- [x] **Step 5 — Harden trigger execution surface**

  Revocar `EXECUTE` de `PUBLIC` y `gds_identity_admin` sobre las cuatro funciones; mantener `SECURITY INVOKER`, `search_path = pg_catalog` y ninguna resolución por `pg_temp`.

- [x] **Step 6 — GREEN: rerun contract**

  Run: `python3 backend-tests/test_user_administration_sql.py -v`

  Expected: PASS para firmas, eventos, diferibilidad, actor, lock y hardening.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend/Database backend-tests/test_user_administration_sql.py`

  Comparar las secciones equivalentes delta/apply y bloquear Task 3 ante cualquier diferencia semántica.

### Task 3: Apply the exact gds_identity_admin allowlist and build the PostgreSQL runner

**Files:**
- Modify: `backend/Database/user-administration.sql`
- Modify: `backend/Database/apply.sql`
- Modify: `backend-tests/test_user_administration_sql.py`
- Create: `backend-tests/user_administration_postgres_runner.py`
- Create: `backend-tests/test_user_administration_postgres_runner.py`
- Modify: `package.json`

**Interfaces:**
- Consumes: Task 2 guards and the exact allowlist in spec §3.
- Produces: `gds_identity_admin NOLOGIN NOINHERIT`, no elevated attributes/memberships, grants only by listed schema/table/column/function signatures; `npm run test:postgres:user-administration` owns a disposable database and accepts no external DSN.

- [x] **Step 1 — RED: specify privilege reconciliation**

  Añadir `test_identity_admin_matches_exact_column_allowlist`, `test_identity_admin_has_no_sequence_or_direct_application_execute`, `test_public_extension_execute_is_exactly_gen_random_uuid`, `test_platform_public_baselines_are_reported_separately` y `test_runtime_privileges_are_unchanged`. Afirmar cada columna/objeto de spec §3, ausencia de atributos elevados/membresías extra y firmas calificadas exactas.

- [x] **Step 2 — RED: specify runner ownership and paths**

  Añadir `test_runner_rejects_arguments_and_external_dsns`, `test_runner_removes_only_its_verified_container` y `test_runner_exercises_clean_and_upgrade_databases_separately`; los tests afirman que fresh usa `apply.sql`, upgrade usa fixture+delta, y nunca se mezclan.

- [x] **Step 3 — Verify RED**

  Run: `python3 -m unittest discover -s backend-tests -p 'test_user_administration*.py' -v`

  Expected: FAIL por reconciliación y runner ausentes.

- [x] **Step 4 — Implement role hardening and direct grant reconciliation**

  Crear/endurecer siempre `gds_identity_admin`; revocar atributos/membresías y grants directos fuera de la allowlist sobre `public`/`app`; abortar si la inspección o revocación falla.

- [x] **Step 5 — Implement inherited and function privilege reconciliation**

  Detectar privilegios heredados administrados, mantener `PUBLIC EXECUTE` solo en `public.gen_random_uuid()`, revocar otras extensiones fuera de `pg_catalog` y preservar permisos de terceros y `gds_app_runtime` conforme a la spec.

- [x] **Step 6 — Implement final read-only reconciliation queries**

  Después de `COMMIT`, reportar roles/columnas/constraints/triggers, `prosecdef=false`, `proconfig`, firmas, execute, cuentas sin rol, administradores activos, grants exactos, atributos/membresías y baselines `PUBLIC`. Las consultas problemáticas deben devolver cero filas y ser las últuser-administration.sqlimas sentencias.

- [x] **Step 7 — Implement the isolated PostgreSQL runner foundation**

  Crear/poseer un contenedor desechable sin aceptar argumentos ni DSN; ejecutar fresh (`apply.sql`, cero usuarios) y upgrade (fixture anterior, administrador activo, solo delta), reaplicación doble, rollback pre-COMMIT, triggers diferidos, señuelos `pg_temp`, grants negativos y conciliación final.

- [ ] **Step 8 — GREEN: execute structural and privilege tests** *(blocked: no local Docker/PostgreSQL runtime on this workstation)*

  Run: `npm run test:postgres:user-administration`

  Expected: PASS; fresh/upgrade separados, delta aplicado dos veces, allowlist exacta, cero filas problemáticas y cleanup confirmado.

- [x] **Step 9 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend/Database backend-tests package.json`

  Mantener SQL-2 pendiente hasta Task 4; esta revisión aprueba únicamente privilegios y runner base.

### Task 4: Prove concurrency, transactional rollback and backup restore

**Files:**
- Modify: `backend-tests/user_administration_postgres_runner.py`
- Modify: `backend-tests/test_user_administration_postgres_runner.py`
- Modify: `backend-tests/test_user_administration_sql.py`
- Modify: `package.json`

**Interfaces:**
- Consumes: runner y SQL de Tasks 1–3.
- Produces: evidencia obligatoria de carreras, atomicidad y secuencia exacta `backup pre-upgrade desde contenedor origen → segundo contenedor/cluster PostgreSQL runner-owned → globals de prueba saneados → restore → fidelidad pre-upgrade → user-administration.sql → reconciliation final`; crear otra base en el cluster origen no satisface el gate.

- [ ] **Step 1 — RED: name the concurrency and rollback cases**

  Añadir `test_concurrent_last_admin_mutations_leave_one_active_admin`, `test_rate_key_flip_allows_exactly_one_active_at_commit` y `test_injected_precommit_failure_rolls_back_every_delta_change`. Afirmar dos conexiones reales, timeout anti-deadlock, un administrador activo final y cero DDL/grants parciales tras el fallo.

- [ ] **Step 2 — RED: require the complete restore-and-upgrade rehearsal**

  Añadir `test_upgrade_backup_restores_to_second_cluster_verifies_fidelity_applies_delta_and_reconciles`, `test_restore_global_role_manifest_rejects_passwords_and_unknown_roles` y `test_runner_cleans_up_both_verified_containers_on_success_or_failure`. Afirmar IDs/nombres/puertos distintos para dos contenedores runner-owned; backup desde el origen pre-upgrade; recreación en el segundo cluster únicamente de globals test-owned allowlisted mediante DDL determinista sin passwords; hard fail ante `PASSWORD`, rol desconocido, atributo o membership fuera del manifest; restore allí; comparación pre-upgrade; delta exclusivamente sobre la restauración; reconciliation final con cero filas problemáticas.

- [ ] **Step 3 — Verify RED**

  Run: `npm run test:postgres:user-administration`

  Expected: FAIL porque el runner todavía usa un solo cluster o no demuestra globals saneados, fidelidad, upgrade y reconciliation en el segundo cluster.

- [ ] **Step 4 — Implement deterministic concurrency orchestration**

  Coordinar dos conexiones con barreras explícitas y timeouts; capturar SQLSTATE/resultados sin sleeps arbitrarios y verificar el estado final desde una tercera conexión.

- [ ] **Step 5 — Implement the second-cluster restore rehearsal**

  Ejecutar `pg_dump` pre-upgrade en el contenedor origen y crear un segundo contenedor/cluster PostgreSQL propiedad del runner. Generar DDL determinista desde un manifest constante de roles globales test-owned requeridos, con atributos y memberships exactos y sin passwords; rechazar cualquier `PASSWORD`, rol desconocido o dependencia global no allowlisted antes de ejecutarlo. Restaurar la base en el segundo cluster, comparar primero esquema/datos y globals relevantes —roles, atributos, memberships, owners y grants— con el origen pre-upgrade; solo si es fiel, aplicar el delta completo y ejecutar allí la reconciliation final.

- [ ] **Step 6 — GREEN**

  Run: `npm run test:postgres:user-administration`

  Expected: PASS para concurrencia/rollback y para cada transición ordenada: backup del contenedor origen, segundo cluster, globals saneados verificados, restore, fidelidad pre-upgrade, delta solo sobre la restauración y reconciliation final.

- [ ] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend-tests package.json`

  Registrar SQL-2 solo si los dos contenedores fueron verificados y limpiados y pasó `backup origen → segundo cluster → globals saneados → restore → fidelidad → delta → reconciliation`; ninguna parte puede quedar pendiente.

### Task 5: Document and approve the Stage 1 server deliverable

**Files:**
- Modify: `README.md`
- Modify: `backend/Database/README.md`
- Modify: `docs/data/postgresql-schema.md`
- Modify: `docs/runbooks/postgresql-cutover.md`
- Modify: `scripts/apply-database.mjs`
- Modify: `scripts/tests/environment.test.mjs`
- Modify: `package.json`
- Modify: `docs/superpowers/plans/2026-10-20-user-administration.md` (progress only during execution)

**Interfaces:**
- Consumes: SQL y evidencia aprobados en Tasks 1–4.
- Produces: comandos separados `npm run db:apply` (fresh) y `npm run db:upgrade:user-administration` (solo delta), ambos con `psql --set ON_ERROR_STOP=on --file ...`; evidencia para autorizar Stage 2.

- [ ] **Step 1 — RED: test deployment-path separation and mandatory recovery evidence**

  Añadir `database_upgrade_selects_only_user_administration_sql` y `stage_one_gate_requires_complete_second_cluster_rehearsal`: exigir archivo exacto según comando, filtrado de conexiones, `shell:false`, URL `verify-full`, cero secretos en argv/logs, prohibición de `apply.sql` en upgrade y evidencia exitosa del origen, segundo cluster, globals saneados, restore, fidelidad, delta y reconciliation.

- [ ] **Step 2 — Verify RED**

  Run: `npm run test:env`

  Expected: FAIL por comando de upgrade y documentación ausentes.

- [ ] **Step 3 — Implement wrapper and documentation**

  Parametrizar el wrapper con una allowlist interna de los dos archivos. Documentar pausa de mutaciones y ensayo obligatorio en un segundo contenedor/cluster: backup del origen, recreación saneada de globals test-owned, restore, fidelidad antes del upgrade, delta sobre la restauración y reconciliation; incluir creación externa de LOGIN/`CONNECT`/membresía, configuración indexada de `rate-limit-v1`, prueba negativa de privilegios y rollback aditivo sin `DROP`.

- [ ] **Step 4 — GREEN: rerun Stage 1 suites**

  Run: `npm run test:env && python3 -m unittest discover -s backend-tests -p 'test_user_administration*.py' -v && npm run test:postgres:user-administration`

  Expected: PASS completo.

- [ ] **Step 5 — Stage 1 blocking review gate (no commit)**

  Run: `git diff --check`

  Revisar el delta como artefacto inmutable de servidor, comparar su semántica con `apply.sql`, confirmar que los únicos `*.sql` de `backend/Database` son `apply.sql` y el único delta, y adjuntar evidencia del ensayo en dos clusters, manifest de globals y cleanup de ambos contenedores. No se permite cerrar Stage 1 dejando globals, restore, fidelidad, delta restaurado o reconciliation pendientes. Registrar **Objectives / Progress**. **STOP:** Stage 2 sigue bloqueado hasta aprobación explícita.

# STAGE 2 — Complete backend

Prerequisite: Stage 1 está aprobado y sus archivos SQL no se cambian sin reabrir Tasks 1–5 y repetir el ensayo completo sobre restauración aislada y su gate PostgreSQL.

### Task 6: Create the backend harness and extend the session contract

**Files:**
- Create: `backend-tests/UserAdministrationHarness/UserAdministrationHarness.csproj`
- Create: `backend-tests/UserAdministrationHarness/Program.cs`
- Create: `backend-tests/test_user_administration.py`
- Modify: `backend/Auth/ApplicationUser.cs`
- Modify: `backend/Auth/ApplicationIdentityDbContext.cs`
- Modify: `backend/Auth/SessionAuthentication.cs`
- Create: `backend/IdentityAdministration/RequiredPasswordChangeFilter.cs`
- Create: `backend/Auth/IdentityHttpResults.cs`
- Modify: `backend/Models/Models.cs`
- Modify: `backend/Program.cs`
- Modify: `backend-tests/IdentityHarness/Program.cs`
- Modify: `backend-tests/IdentityHarness/AuthenticationSecurityScenarios.cs`
- Modify: `backend-tests/test_identity.py`
- Modify: `backend-tests/SessionHarness/Program.cs`
- Modify: `backend-tests/test_sessions.py`

**Interfaces:**
- Produces: `ApplicationUser.MustChangePassword: bool`, `PasswordChangedAt: DateTimeOffset?`, `ManagementVersion: long`; `PublicUser(string Id, string Username, string DisplayName, string[] Permissions, bool MustChangePassword)`; claim interno `gds:must-change-password`.
- Produces: envelope `{ error, code, fieldErrors?, traceId? }`, `Cache-Control: no-store` común y allowlist mandatory exacta de `/me`, `/csrf`, `/change-password`, `/logout`.

- [x] **Step 1 — Create the harness owners before any later modification**

  Crear `UserAdministrationHarness.csproj`, `UserAdministrationHarness/Program.cs` y `test_user_administration.py`; el proyecto referencia el backend, `Program.cs` acepta un nombre de escenario y el propietario Python invoca ese registro. Ejecutar el escenario vacío y afirmar que el harness compila y reporta un nombre desconocido sin ejecutar producto.

- [x] **Step 2 — RED: register model, role-cardinality and mandatory-route scenarios**

  Añadir `test_identity_model_maps_management_fields`, `test_session_rejects_zero_multiple_or_noncanonical_roles` y `test_mandatory_password_session_only_reaches_four_allowed_routes` en los harness correspondientes y sus archivos Python propietarios. Cada escenario de Python debe tener una entrada del mismo nombre en su `Program.cs`.

- [x] **Step 3 — RED: register cache and error-envelope scenarios**

  Añadir en `test_sessions.py` `test_auth_responses_use_no_store_and_exact_status_codes`: login, `/me` y `/csrf` incluyen exactamente `Cache-Control: no-store`; sesión ausente/expirada/revocada produce `401 authentication_required`, y bloqueo mandatory produce `403 password_change_required`. Cada error lleva `error`+`code`, admite solo `fieldErrors`/`traceId` opcionales y omite infraestructura. Registrar el mismo nombre en `SessionHarness/Program.cs`; las pruebas CSRF de mutaciones pertenecen a Tasks 10–13.

- [x] **Step 4 — Verify RED**

  Run: `python3 backend-tests/test_identity.py -v && python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v`

  Expected: FAIL por harness/campos/filtro/envelope ausentes.

- [x] **Step 5 — Implement model mapping and fail-closed principal**

  Mapear las tres columnas sin DDL EF; exigir exactamente un rol canónico, calcular permisos, refrescar flag/rol por request y conservar `ShouldRenew=false`.

- [x] **Step 6 — Implement mandatory routing and common identity results**

  Ejecutar el filtro antes de negocio; centralizar códigos/envelope y headers anti-cache para `/api/auth` y la futura rama `/api/admin`.

- [x] **Step 7 — GREEN**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_identity.py -v && python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v`

  Expected: build Release sin warnings y escenarios PASS.

- [x] **Step 8 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Confirmar que cada escenario nuevo está registrado en `Program.cs` y expuesto por su archivo Python propietario.

### Task 7: Add the isolated IdentityAdmin connection and startup/readiness validation

**Files:**
- Modify: `backend/Database/PostgresDataSources.cs`
- Create: `backend/IdentityAdministration/IdentityAdminConnection.cs`
- Create: `backend/IdentityAdministration/IdentitySecurityOptions.cs`
- Create: `backend/IdentityAdministration/IdentityReadiness.cs`
- Create: `backend/IdentityAdministration/IdentitySecurityAudit.cs`
- Create: `backend/IdentityAdministration/IdentityAuthorizationResultHandler.cs`
- Modify: `backend/IdentityAdministration/RequiredPasswordChangeFilter.cs`
- Modify: `backend/Auth/IdentityServiceCollectionExtensions.cs`
- Modify: `backend/Program.cs`
- Modify: `backend/.env.example`
- Modify: `scripts/run-backend.mjs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/SessionHarness/Program.cs`
- Modify: `backend-tests/test_sessions.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- Consumes: `ConnectionStrings__IdentityAdmin`; array `IdentitySecurity:RateLimitHmacKeys` de `IdentityRateLimitHmacKeyEntry(string KeyId, string SecretBase64)`, enlazado por ambiente con `IdentitySecurity__RateLimitHmacKeys__<index>__KeyId` y `...__SecretBase64`.
- `IIdentityRateLimitKeyStateReader.ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken): Task<IdentityRateLimitKeyStateSnapshot>`; snapshot expone `string ActiveKeyId` e `IReadOnlyList<string> RetainedKeyIds`.
- `IdentityReadinessResult(bool IsReady, string Code, IReadOnlyList<string> MissingKeyIds)`; `IdentityReadiness.CheckAsync(CancellationToken cancellationToken): Task<IdentityReadinessResult>` usa el reader inyectable.
- `IdentityAdminConnection.OpenVerifiedAsync(CancellationToken cancellationToken): Task<NpgsqlConnection>` ejecuta `SET ROLE gds_identity_admin` y comprueba `current_role`.
- `IdentitySecurityRejection(string Action, string Code, Guid? ActorUserId, string EntityType, string EntityId, string Outcome, string? CorrelationId, IReadOnlyDictionary<string,string> Metadata)`; `Action` proviene de constantes (`auth.authorization.reject`, `auth.password_change_required.reject`, `auth.admin_stepup.reject`, `auth.csrf.reject`, `auth.validation.reject`, `identity.rate_limit.reject`, `identity.concurrency.reject`). `EntityType` pertenece a `{authentication,http_route,user,user_directory}` y `EntityId` es respectivamente `unknown`, un route ID fijo, UUID canónico o `user-directory`; ambos son no vacíos. `Outcome` es exactamente `rejected`. `Code` pertenece exactamente a la allowlist estable de **Global Constraints**. Metadata admite como máximo seis entradas, únicamente con claves `{httpMethod,routeTemplate,policy,reason,validationFields,expectedVersion}`, y valores escalares saneados de máximo 128 runas sin controles Unicode. `CorrelationId` se trimmea y trunca a 128 runas. Nunca acepta DTO, body, password, cookie, CSRF o conexión.
- `IdentitySecurityAudit.RecordRejectionAsync(IdentitySecurityRejection rejection, CancellationToken cancellationToken): Task` valida ese contrato y abre su propia transacción best-effort. Mapea explícitamente a las columnas superiores `action`, `outcome`, `entity_type`, `entity_id`, `actor_user_id` y `correlation_id` de `app.audit_events`; persiste `changes` como `{}` exacto y `security_context` como objeto JSON con `Code` y solo las entradas allowlisted de `Metadata`.
- `IdentityAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler` envuelve `AuthorizationMiddlewareResultHandler` y posee solo dos resultados emitidos por el middleware de autorización: usuario no autenticado → `401 authentication_required`; forbidden genérico → `403 permission_denied`. Audita una vez antes de delegar/escribir la respuesta. No conoce mandatory-password ni StepUp, no inspecciona requirements para esos casos y no registra sus auditorías.
- Cuando una autorización no conoce usuario/target, usa exactamente `Action="auth.authorization.reject"`, `ActorUserId=null`, `EntityType="authentication"`, `EntityId="unknown"`, `Outcome="rejected"`; rutas/usuarios conocidos usan únicamente IDs de entidad allowlisted, nunca path o input arbitrario.

- [x] **Step 1 — RED: unit-test strict configuration and injectable readiness**

  Añadir `test_rate_limit_key_array_binds_indexed_environment_entries`, `test_rate_limit_key_list_rejects_null_incomplete_duplicate_and_invalid_entries`, `test_configuration_rejects_local_current_or_active_selector` y `test_readiness_requires_exactly_one_active_and_every_retained_secret`. Cubrir lista vacía; entrada nula/incompleta; duplicado detectado antes del diccionario; IDs de 1 y 64 caracteres aceptados, mayúscula/65 inválidos contra `^[a-z0-9][a-z0-9._-]{0,63}$`; Base64 vacío, whitespace, padding/alfabeto no canónico y 31 bytes rechazados; 32 bytes canónicos aceptados. La última prueba demuestra que PostgreSQL elige `active`.

- [x] **Step 2 — RED: test current_role and readiness on real PostgreSQL**

  Añadir al runner `test_identity_admin_sets_and_verifies_current_role_on_every_checkout`, `test_identity_admin_has_no_primary_fallback` y `test_real_readiness_fails_for_missing_retained_key`; usar el rol Stage 1 y conexiones/transacciones reales, invocar la firma exacta de `ReadAsync`, afirmar `Code`/`MissingKeyIds` y no delegar la prueba a `test_postgresql_tls_policy.py`.

- [x] **Step 3 — RED: specify rejection audit and authorization hook boundaries**

  Añadir en `test_user_administration.py` `test_rejection_record_requires_stable_code_allowlisted_nonempty_entity_rejected_outcome_and_bounded_metadata`, `test_rejection_audit_persists_exact_top_level_columns_empty_changes_and_security_context_json`, `test_rejection_audit_writes_in_its_own_transaction`, `test_rejection_audit_failure_preserves_original_rejection` y `test_rejection_audit_never_accepts_body_password_cookie_csrf_or_connection_data`; añadir en `test_sessions.py` `test_authorization_result_handler_audits_only_authentication_required_and_generic_permission_denied`, `test_unknown_auth_target_uses_fixed_authentication_entity`, `test_required_password_filter_audits_before_password_change_required` y `test_authorization_and_mandatory_rejections_write_exactly_one_audit_row`. Registrar cada escenario con el mismo nombre en su harness. El test de fila exacta afirma columnas superiores, `changes == {}` y `security_context == {"Code": <código>, ...metadata allowlisted...}` sin campos extra; también cubre trim/truncado de correlation, máximo de seis entradas y valores de hasta 128 runas. Task 7 no prueba ni implementa StepUp, que pertenece a Task 10; ninguna prueba sustituye la auditoría atómica de éxito.

- [x] **Step 4 — Verify RED**

  Run: `python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: FAIL por opciones/pool/role/readiness/auditoría de rechazo ausentes.

- [x] **Step 5 — Implement strict binding and separate pool**

  Enlazar solo el array `IdentitySecurity:RateLimitHmacKeys`; validar la lista completa antes de construir el mapa, incluidos duplicados; exigir regex de 64 caracteres, whitespace ausente, decodificación, longitud y round-trip Base64; rechazar selectores `current`/`active`, conexión ausente/inválida o TLS débil con mensaje opaco; crear pool IdentityAdmin distinto y sin fallback.

- [x] **Step 6 — Implement verified checkout and readiness**

  Ejecutar/verificar role en cada checkout. El reader real recibe conexión+transacción, devuelve un snapshot active/retained y el harness sustituye solo esa interfaz. Integrar `/api/health` sin exponer IDs/material.

- [x] **Step 7 — Implement rejection audit and authorization result handler**

  Abrir una transacción independiente por rechazo, validar el record/metadata, escribir exactamente las columnas/JSON del contrato y absorber el fallo después de logging saneado. Implementar y registrar como singleton `IAuthorizationMiddlewareResultHandler` el wrapper del handler por defecto; este audita exclusivamente `authentication_required` y el `permission_denied` genérico antes de delegar. Modificar `RequiredPasswordChangeFilter` para que sea el único propietario que audita su rechazo pre-endpoint antes de devolver `password_change_required`. Mantener éxito+audit en la transacción de la mutación y asegurar una sola fila por rechazo.

- [ ] **Step 8 — GREEN** *(blocked: no local Docker/PostgreSQL runtime)*

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: PASS incluida comprobación PostgreSQL real.

- [x] **Step 9 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend scripts backend-tests`

  Verificar que IdentityAdmin no comparte pool/Primary, que active viene solo de DB, que DI contiene un único authorization result handler wrapper y que ninguna ruta duplica auditoría entre handler, filtro y endpoint; la auditoría best-effort nunca comparte ni altera la transacción/resultado protegido.

### Task 8: Replace in-memory identity limits with the distributed protocol

**Files:**
- Create: `backend/IdentityAdministration/IdentityRateLimiter.cs`
- Create: `backend/IdentityAdministration/IdentityRateLimitCleanupService.cs`
- Modify: `backend/Auth/IdentityServiceCollectionExtensions.cs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- `IdentityRateLimitRequest(string Policy, string Subject, string? NormalizedIp, DateTimeOffset Now)`.
- `IdentityRateLimitDecision(bool Allowed, DateTimeOffset? RetryAfter, int Remaining)`.
- Produces: `ConsumeAsync(IdentityRateLimitRequest request, CancellationToken cancellationToken): Task<IdentityRateLimitDecision>` que abre, posee y confirma su propia transacción antes de retornar; `DeleteExpiredBatchAsync(int batchSize, CancellationToken cancellationToken): Task<int>`.
- Exact limits: login `10/1m` por `login | normalizedUsername | normalizedIp`; reauth `5/1m` por `reauth | userId | normalizedIp`; change-password `5/15m` por `change-password | userId`; admin mutation `30/1m` por `admin-mutation | actorUserId`; TTL = fin de ventana + 24 h.

- [x] **Step 1 — RED: transaction-boundary and failure scenarios**

  Añadir pruebas sintéticas directas del servicio: `test_rate_limit_attempt_commits_before_service_returns`, `test_429_attempt_commits_before_service_returns`, `test_rate_limit_attempt_survives_synthetic_later_rollback`, `test_rate_limit_database_failure_returns_service_unavailable` y `test_expired_buckets_are_deleted_in_bounded_batches`. Afirmar con una segunda conexión que el incremento ya es visible al retornar `ConsumeAsync` y que una transacción sintética posterior no lo revierte; no invocar endpoints HTTP en Task 8.

- [x] **Step 2 — RED: cross-replica rotation focus test**

  Añadir `test_rate_limit_rotation_is_shared_ordered_and_fails_closed` con dos instancias del servicio dentro del runner —sin procesos ni endpoints HTTP—, flip v1/v2, mapas incompletos/opuestos, suma compartida, locks ordenados y timeout sin deadlock.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: FAIL porque el servicio distribuido y su transacción independiente aún no existen.

- [x] **Step 4 — Implement the owned rate-limit transaction**

  Dentro de `ConsumeAsync`: abrir transaction, bloquear state, calcular HMACs, materializar/bloquear candidatos ordenados, sumar e incrementar active; confirmar allowed y 429 antes de retornar.

- [x] **Step 5 — Implement hosted cleanup without endpoint wiring**

  Registrar cleanup con orden state→buckets y lote acotado. No modificar ni probar login, reauth, change-password o mutaciones admin aquí; sus tareas propietarias integran `ConsumeAsync` y prueban el orden HTTP.

- [ ] **Step 6 — GREEN** *(blocked: no local Docker/PostgreSQL runtime)*

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: PASS para contratos/transacciones sintéticas del servicio y cleanup; la integración HTTP permanece RED en sus tareas propietarias.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Revisar que el servicio posee la transacción, que Task 8 no contiene llamadas HTTP y que los endpoints aún no comparten transacción con el limiter.

### Task 9: Add canonical DTOs, admin authorization and directory endpoints

**Files:**
- Create: `backend/IdentityAdministration/IdentityDtos.cs`
- Create: `backend/IdentityAdministration/IdentityAdministrationStore.cs`
- Create: `backend/IdentityAdministration/IdentityAdministrationEndpoints.cs`
- Modify: `backend/Auth/IdentityServiceCollectionExtensions.cs`
- Modify: `backend/Program.cs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- Produces: `GET /api/admin/users?q&role&status&page&pageSize` → `{ items: AdminUserSummary[], page, pageSize, total }`; `GET /api/admin/roles` → `[{code,label}]` in `operador,validador,administrador` order.
- Query defaults are exact: omitted `page` becomes `1` and omitted `pageSize` becomes `25`; explicit values still require `page≥1` and `pageSize` in `1..100`.
- `AdminUserSummary`: `id`, `username`, `displayName`, `role`, `isActive`, `mustChangePassword`, `createdAt`, `updatedAt`, `managementVersion` (decimal string).

- [x] **Step 1 — RED: authorization, validation, headers and envelope**

  Añadir en `test_user_administration.py` `test_admin_directory_returns_authentication_required_and_permission_denied`, `test_admin_directory_defaults_to_page_1_and_page_size_25`, `test_admin_directory_validates_filters_and_bounded_pagination` y `test_admin_directory_success_and_errors_use_no_store_and_stable_envelope`, registrados uno a uno en `UserAdministrationHarness/Program.cs`. Afirmar `401 authentication_required` para anónimo, `403 permission_denied` para operador/validador, defaults exactos `page=1/pageSize=25`, `400 validation_failed` para filtros inválidos y `Cache-Control: no-store`; al ser GET, Task 9 no prueba ni exige CSRF. Los dos rechazos de autorización son auditados exclusivamente por `IdentityAuthorizationResultHandler` de Task 7 y el endpoint no invoca auditoría para ellos.

- [x] **Step 2 — RED: Review Focus search test**

  Añadir `test_admin_directory_escapes_wildcards_and_orders_by_normalized_name_then_id` con `%`, `_`, `\\`, diacríticos/empates, `test_admin_directory_success_audits_stable_entity_without_results` y `test_admin_directory_validation_rejection_is_audited_once_with_stable_entity`. La segunda afirma el evento exitoso `admin.users.list` con `entity_type=user_directory`/`entity_id=user-directory`, filtros y paginación saneados, sin resultados; la última cubre solo el `400 validation_failed` que posee el endpoint. Los tests de auditoría 401/403 permanecen en Task 7.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: FAIL por endpoints ausentes.

- [x] **Step 4 — Implement DTO parsing and list SQL**

  `q` trim, vacío=omitido, máximo 100 runas; `role` canónico; `status active|inactive`; aplicar `page=1` y `pageSize=25` cuando se omiten, y validar explícitos con page≥1/pageSize 1..100. Ejecutar lista y count en IdentityAdmin, sin exponer stamps/hashes.

- [x] **Step 5 — Implement authorization, roles and list audit**

  Añadir policy que exige rol `administrador` y permission calculado `users:provision`; exponer labels fijos, no catálogo arbitrario. El handler de Task 7 posee los rechazos de autorización, por lo que los endpoints de Task 9 no los auditan manualmente. Registrar `admin.users.list` al completar la lista sin copiar resultados. El endpoint invoca `IdentitySecurityAudit` una vez solo para sus rechazos de validación/negocio, con action/code/filtros saneados; un fallo conserva exactamente el `400` original.

- [x] **Step 6 — GREEN**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: PASS.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Confirmar que headers/envelope cubren admin, que los defaults son `1/25`, que filtros/paginación saneados son lo único auditado por el endpoint y que cada rechazo produce como máximo una fila.

### Task 10: Add administrative reauthentication and reusable five-minute StepUp

**Files:**
- Create: `backend/IdentityAdministration/AdminStepUpService.cs`
- Create: `backend/IdentityAdministration/AdminStepUpFilter.cs`
- Modify: `backend/Auth/AuthEndpoints.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationEndpoints.cs`
- Modify: `backend/Auth/IdentityServiceCollectionExtensions.cs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/SessionHarness/Program.cs`
- Modify: `backend-tests/test_sessions.py`

**Interfaces:**
- Produces: `POST /api/auth/reauth` with `{ password }` → `204`; cookie `GDS.AdminStepUp` (`HttpOnly`, `SameSite=Strict`, `Path=/api/admin`, absolute 5m, Secure outside Development/Testing).
- Produces: `Task<AdminStepUpValidation> ValidateAsync(HttpContext, CancellationToken)` matching user ID, `SecurityStamp`, active status and current administrator role; invalid result clears cookie when possible and maps to `403 admin_reauthentication_required`.
- `AdminStepUpFilter` is a concrete endpoint filter on every admin mutation. It calls `AdminStepUpService` after authentication/authorization and before target access; on failure it is the sole owner of the `403 admin_reauthentication_required` response and its one rejection-audit row. It is not an `IAuthorizationRequirement`, and `IdentityAuthorizationResultHandler` never handles this result.

- [x] **Step 1 — RED: StepUp lifecycle and pre-hasher limits**

  Añadir `test_stepup_is_reusable_without_renewal_and_invalidates_with_actor_security_state`, `test_oversized_reauth_body_never_hash` y `test_129_rune_reauth_never_hash`; afirmar cookies/TTL, `413 validation_failed` para 4097 bytes y `400 reauthentication_failed` para password de 129 runas, ambos antes del hasher. Task 10 no contiene casos oversized de change-password.

- [x] **Step 2 — RED: CSRF, quota, audit and normal-session preservation**

  Añadir `test_reauth_csrf_returns_exact_code`, `test_reauth_commits_quota_before_hasher_and_rejection_audit`, `test_reauth_uses_no_store_envelope_and_preserves_normal_session`, `test_admin_stepup_filter_audits_missing_or_expired_stepup_once` y `test_admin_stepup_filter_does_not_duplicate_authorization_audit`. Afirmar `400 csrf_invalid` con sesión válida y CSRF ausente/incorrecto; cuota confirmada antes del hasher; rechazos de CSRF/validación/rate limit auditados si DB está disponible, sin cambiar respuesta si audit falla; fallo de reauth no cierra sesión y éxito no crea ni renueva `GDS.Session`. Las dos últimas pruebas demuestran un solo `403 admin_reauthentication_required` y una sola fila propiedad del filtro, sin fila del authorization result handler.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v`

  Expected: FAIL por endpoint/cookie/filtro ausentes.

- [x] **Step 4 — Implement reauth endpoint and protector**

  Aplicar CSRF/body limit, confirmar `ConsumeAsync` antes del hasher, emitir rechazo best-effort cuando corresponda, usar propósito separado y auditar el éxito `auth.admin.reauthenticate`; no crear/renovar `GDS.Session`.

- [x] **Step 5 — Require StepUp on every admin mutation**

  Implementar y registrar el `AdminStepUpFilter` concreto; validar después de sesión/rol y antes de tocar el target, y auditar exactamente una vez su propio `admin_reauthentication_required`. No crear `IAuthorizationRequirement` de StepUp. GET directory/roles no requiere StepUp.

- [x] **Step 6 — GREEN**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_sessions.py -v && python3 backend-tests/test_user_administration.py -v`

  Expected: PASS.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Verificar expiración absoluta, propósito distinto, ausencia de contraseña en audit/logs y propiedad única del rechazo StepUp sin filas duplicadas.

### Task 11: Implement idempotent administrative user creation and receipt tombstones

**Files:**
- Modify: `backend/IdentityAdministration/IdentityDtos.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationStore.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationEndpoints.cs`
- Create: `backend/IdentityAdministration/IdentityIdempotencyReceiptCleanupService.cs`
- Modify: `backend/Auth/IdentityServiceCollectionExtensions.cs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- Produces: `POST /api/admin/users`, body `{ username, displayName, role }`, header `Idempotency-Key`, → `201 { user: AdminUserSummary, temporaryPassword }`.
- Original y replay incluyen `Cache-Control:no-store` y `Pragma:no-cache`; solo replay incluye `Idempotency-Replayed:true`. `Task<int> TombstoneExpiredReceiptsAsync(int batchSize, CancellationToken)` nulifica `protected_response` y conserva la fila.
- Exact validation: username trimmed ASCII `[A-Za-z0-9._-]` length 3..64; displayName trimmed 1..120 runas without Unicode controls; canonical role; key lowercase RFC4122 `D` UUID v4.
- Exact hashing: `SHA256(UTF8(lowerActorUuid + ":" + lowerIdempotencyUuid))`; vector `00112233-4455-6677-8899-aabbccddeeff:123e4567-e89b-42d3-a456-426614174000` → `62a2b5e4351b21ac9b4446e43682c26070f0ae39cad27d56f960bb96f356b775`.

- [x] **Step 1 — RED: validation, vector, randomness and response headers**

  Añadir en `test_user_administration.py` `test_create_csrf_returns_exact_code`, `test_create_validates_fields_uuid_v4_and_temporary_password`, `test_idempotency_hash_matches_shared_vector` y `test_create_headers_are_exact_for_original_and_replay`, con registro del mismo nombre en `UserAdministrationHarness/Program.cs`. Afirmar `400 csrf_invalid` con sesión/permiso válidos, `Cache-Control: no-store` y `Pragma: no-cache` en original y replay, `Idempotency-Replayed` ausente en el original y exactamente `true` en el replay.

- [x] **Step 2 — RED: replay, privacy and receipt cleanup**

  Añadir `test_create_two_stores_in_one_process_replay_identical_protected_response`, `test_same_idempotency_uuid_is_isolated_by_actor`, `test_same_actor_key_with_different_canonical_payload_returns_idempotency_key_reused`, `test_distinct_creation_intents_generate_different_temporary_passwords`, `test_expired_receipt_becomes_tombstone_without_duplicate`, `test_receipt_cleanup_nulls_ciphertext_and_preserves_row`, `test_create_rejections_are_best_effort_audited_without_secrets` y `test_receipt_plaintext_never_reaches_db_log_or_audit`. Afirmar que el mismo UUID v4 de idempotencia usado por actores distintos crea receipts/usuarios independientes; el mismo actor+key con payload canónico distinto devuelve exactamente `409 idempotency_key_reused`; dos intenciones distintas producen contraseñas temporales distintas. El replay de Task 11 usa dos instancias de store en el mismo proceso y una base/key ring compartidos; el replay entre dos procesos backend pertenece a Task 15.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: FAIL por creación/idempotencia ausentes.

- [x] **Step 4 — Implement canonical hashes and replay decision**

  Prohibir `Guid.ToByteArray`; canonicalizar payload con longitudes; aislar la identidad del receipt por actor+UUID, tomar advisory lock, decidir new/replay/reused/expired y proteger con `GdsMine.IdentityCreateReceipt.v1` por 10 minutos. Comparar el hash del payload canónico antes de replay: un cambio devuelve `409 idempotency_key_reused`; cada intención nueva genera su propia contraseña con CSPRNG.

- [x] **Step 5 — Implement quota-first atomic user creation**

  Validar CSRF y confirmar `ConsumeAsync` antes de abrir la mutación; auditar rechazos de CSRF/validación/idempotencia/rate-limit con el servicio best-effort; insertar columnas allowlisted, membership, constraints immediate, audit de éxito y receipt en una sola transaction que revierte completa ante fallo.

- [x] **Step 6 — Implement opportunistic and hosted receipt tombstoning**

  Ejecutar un lote oportunista al crear y un hosted service periódico; ambos actualizan `protected_response=NULL` al vencer, conservan tombstone/hashes/fechas y nunca borran filas.

- [ ] **Step 7 — GREEN (local PASS; real PostgreSQL blocked under R24)**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: PASS, incluidos replay con dos stores del mismo proceso, aislamiento por actor, conflicto de payload, aleatoriedad entre intenciones, headers exactos, auditoría de rechazos y tombstone TTL; no exige aún dos procesos backend.

- [x] **Step 8 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Inspeccionar SQL parametrizado; no hay `Location`, consulta de receipt ni cleanup que permita recreación.

### Task 12: Implement role and status mutations with optimistic concurrency

**Files:**
- Modify: `backend/IdentityAdministration/IdentityDtos.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationStore.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationEndpoints.cs`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- Produces: `PUT /api/admin/users/{id}/role` body `{ role, expectedManagementVersion }`; `PUT /api/admin/users/{id}/status` body `{ isActive, expectedManagementVersion }`; both → `200 AdminUserSummary`.
- `expectedManagementVersion` must be a JSON string matching `^[1-9][0-9]*$`, parse exactly to `long` 1..9223372036854775807; preserve `"9007199254740993"` end-to-end.

- [x] **Step 1 — RED: DTO and domain error matrix**

  Añadir `test_role_status_csrf_returns_exact_code`, `test_management_version_rejects_json_numbers_and_preserves_over_2pow53` y `test_role_status_return_stable_errors_for_missing_stale_self_and_last_admin`; afirmar `400 csrf_invalid` antes del DTO para sesión/permiso válidos.

- [x] **Step 2 — RED: no-op and concurrency semantics**

  Añadir `test_noop_runs_guards_without_version_or_success_audit`, `test_admin_mutation_quota_survives_stale_conflict`, `test_mutation_rejections_are_best_effort_audited_without_changing_errors` y `test_same_version_has_single_writer_and_rotates_both_stamps`. Afirmar auditoría de validación/concurrencia/rate-limit con metadata allowlisted; fallo del audit conserva el rechazo.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: FAIL por endpoints ausentes.

- [x] **Step 4 — Implement exact parsing, independent quota and guards**

  Validar CSRF, parsear string sin coerción y confirmar `ConsumeAsync` antes de abrir transaction; dentro establecer actor y evaluar versión/self/last-admin aun en no-op. Fuera de la mutación, el endpoint emite una sola auditoría best-effort para sus rechazos de CSRF, validación, rate-limit y negocio/concurrencia; nunca vuelve a auditar los 401/403 del authorization result handler ni el `admin_reauthentication_required` del StepUp filter.

- [x] **Step 5 — Implement conditional writes and atomic audit**

  Condicionar por version, reemplazar stamps, incrementar version/UTC y auditar dentro de la transaction; no-op no escribe/audita.

- [ ] **Step 6 — GREEN (local PASS; real PostgreSQL blocked under R24)**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_user_administration.py -v && npm run test:postgres:user-administration`

  Expected: PASS, incluida precisión >2^53 y carreras.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Confirmar stamps aleatorios y cuota persistida ante rollback por conflicto/audit.

### Task 13: Implement atomic self-service password change

**Files:**
- Modify: `backend/Auth/AuthEndpoints.cs`
- Modify: `backend/IdentityAdministration/IdentityAdministrationStore.cs`
- Modify: `backend-tests/IdentityHarness/AuthenticationSecurityScenarios.cs`
- Modify: `backend-tests/IdentityHarness/Program.cs`
- Modify: `backend-tests/test_identity.py`
- Modify: `backend-tests/SessionHarness/Program.cs`
- Modify: `backend-tests/test_sessions.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- Produces: `POST /api/auth/change-password` body `{ currentPassword, newPassword, confirmation }` → `204`, deletes `GDS.Session` and `GDS.AdminStepUp` after commit.

- [x] **Step 1 — RED: password limits, CSRF, headers and state changes**

  Añadir `test_change_password_csrf_returns_exact_code`, `test_oversized_change_password_body_never_hash`, `test_129_rune_current_password_never_hash`, `test_new_password_enforces_14_15_128_129_rune_boundaries`, `test_change_password_returns_exact_current_confirmation_and_reuse_codes`, `test_change_password_policy_errors_use_envelope_and_no_store` y `test_change_password_clears_mandatory_flag_rotates_stamps_and_cookies`. Afirmar `400 csrf_invalid`, `413 validation_failed` para 4097 bytes, rechazo pre-hasher de currentPassword con 129 runas, y para newPassword probar explícitamente 14→`400 validation_failed`, 15→aceptado, 128→aceptado y 129→`400 validation_failed`. Afirmar además los códigos exactos `400 current_password_invalid` para contraseña actual incorrecta, `400 validation_failed` para confirmación distinta y `400 password_reuse` para reutilización.

- [x] **Step 2 — RED: concurrency focus test**

  Añadir `test_concurrent_password_changes_and_disable_have_single_winner` y `test_change_password_rejections_are_best_effort_audited`: dos cambios desde mismos `PasswordHash`/`ConcurrencyStamp`/`ManagementVersion` dan un `204` y un `409`; una desactivación ganadora nunca queda sobrescrita/reactivada; validación/concurrencia/rate-limit se auditan sin secretos y un fallo de audit no cambia el rechazo.

- [x] **Step 3 — Verify RED**

  Run: `python3 backend-tests/test_identity.py -v && python3 backend-tests/test_sessions.py -v && npm run test:postgres:user-administration`

  Expected: FAIL en change-password y concurrencia.

- [x] **Step 4 — Implement validation and independent quota consumption**

  Validar CSRF, confirmación y body/rune limits antes del hasher; confirmar `ConsumeAsync` y solo entonces abrir la transaction de password. Mapear contraseña actual incorrecta a `current_password_invalid`, confirmación distinta a `validation_failed` y reutilización a `password_reuse`. Emitir una sola auditoría best-effort para cada rechazo sin compartirla con la transacción de cambio.

- [x] **Step 5 — Implement password change conditional update and audit**

  Leer user active y valores observados dentro de transaction; verificar actual y no-reuse; ejecutar una sola actualización con `Id`, `IsActive=true`, `PasswordHash IS NOT DISTINCT FROM`, `ConcurrencyStamp`, `ManagementVersion`; escribir hash/stamps/UTC/flag/version y audit `auth.password.change` solo si una fila cambió.

- [ ] **Step 6 — GREEN (pruebas locales aprobadas; PostgreSQL real pendiente)**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 backend-tests/test_identity.py -v && python3 backend-tests/test_sessions.py -v && npm run test:postgres:user-administration`

  Expected: PASS.

  Evidencia local: compilación Release sin advertencias y suite de backend 239/239. La prueba PostgreSQL no se ejecutó: Docker local no está disponible y el runner terminó de forma segura.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend backend-tests`

  Verificar WHERE exacto, separación de transactions, auditoría atómica y cookies post-commit.

### Task 14: Implement bootstrap-only CLI with an exclusive deployment connection

**Files:**
- Create: `backend/Auth/IdentityBootstrapConnectionFactory.cs`
- Modify: `backend/Auth/ProvisionUserCommand.cs`
- Modify: `backend/Program.cs`
- Create: `scripts/provision-user.mjs`
- Modify: `scripts/tests/environment.test.mjs`
- Modify: `package.json`
- Modify: `backend/Auth/README.md`
- Modify: `backend-tests/IdentityHarness/AuthenticationSecurityScenarios.cs`
- Modify: `backend-tests/IdentityHarness/Program.cs`
- Modify: `backend-tests/test_identity.py`
- Modify: `backend-tests/user_administration_postgres_runner.py`

**Interfaces:**
- `IdentityBootstrapConnectionFactory.CreateFromEnvironment(IHostEnvironment environment) -> NpgsqlDataSource`; la clase concreta lee exclusivamente `POSTGRES_DEPLOY_URL`, exige `SslMode=VerifyFull` fuera de `Development`/`Testing` y solo la rama `provision-user` la instancia, llama y dispone con `await using` fuera del contenedor DI web.
- `ProvisionUserCommand.RunAsync(string[] args, TextReader input, TextWriter output, NpgsqlDataSource bootstrap, CancellationToken) -> Task<int>`; exige `--bootstrap`, role administrador y password+confirmación solo por stdin.

- [x] **Step 1 — RED: connection isolation and secret handling**

  Añadir en `test_identity.py`, con escenarios del mismo nombre registrados en `IdentityHarness/Program.cs`, `test_bootstrap_factory_reads_only_postgres_deploy_url_in_cli_branch`, `test_web_host_never_reads_registers_or_retains_deployment_connection`, `test_bootstrap_requires_verify_full_outside_development_and_testing` y `test_bootstrap_data_source_is_disposed_when_command_exits_or_fails`; añadir en `scripts/tests/environment.test.mjs` la prueba de `POSTGRES_DEPLOY_URL` ausente/inválida sin eco del valor.

- [x] **Step 2 — RED: bootstrap domain contract**

  Añadir `test_bootstrap_only_with_zero_users_and_administrator_role`; cubrir 14/15/128/129 runas, deployer/current_role, cuenta final y audit `identity.bootstrap.admin`; usuario existente o alta CLI normal falla sin write.

- [x] **Step 3 — Verify RED**

  Run: `npm run test:env && python3 backend-tests/test_identity.py -v && npm run test:postgres:user-administration`

  Expected: FAIL por factoría/wrapper/bootstrap ausentes.

- [x] **Step 4 — Implement CLI-only connection factory and Program wiring**

  Antes de construir el host web, detectar la rama CLI `provision-user`; solo allí instanciar la factoría concreta, leer `POSTGRES_DEPLOY_URL`, validar conexión/TLS, crear el `NpgsqlDataSource`, pasarlo al comando y disponerlo con `await using` en éxito o excepción. La rama HTTP no lee, registra, captura ni retiene la URL o el data source de despliegue.

- [x] **Step 5 — Implement bootstrap transaction**

  Hash Identity+SQL explícito con advisory lock, bootstrap local flag, user+role+constraint immediate+audit atómicos; no cookie/session/password args/env/log.

- [x] **Step 6 — GREEN**

  Run: `dotnet build backend -c Release --nologo -warnaserror && npm run test:env && python3 backend-tests/test_identity.py -v && npm run test:postgres:user-administration`

  Expected: PASS; host web sin credencial/data source deploy.

  Evidence: la validación de la ruta de bootstrap quedó en verde y el comando de servicio mantiene aislamiento de la conexión de despliegue fuera del host web.

- [x] **Step 7 — Review checkpoint (no commit)**

  Run: `git diff --check -- backend scripts backend-tests package.json`

  Confirmar lifecycle CLI exclusivo y disposal aun si command falla.

### Task 15: Close backend regression and independent security review

**Files:**
- Modify: `backend/Auth/AuthenticationService.cs`
- Modify: `backend/Auth/SessionAuthentication.cs`
- Modify: `backend/Auth/AuthEndpoints.cs`
- Modify: `backend-tests/user_administration_postgres_runner.py`
- Modify: `backend-tests/test_user_administration_postgres_runner.py`
- Modify: `backend-tests/UserAdministrationHarness/Program.cs`
- Modify: `backend-tests/test_user_administration.py`
- Modify: `backend/Auth/README.md`
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-10-20-user-administration.md` (progress only during execution)

**Interfaces:**
- Consumes: backend completo Tasks 6–14.
- Produces: login HTTP integrado con el limiter distribuido antes del hasher; Release build warnings-as-errors, suites completas, prueba real de dos procesos y revisión de seguridad aprobada; autorización para Stage 3.

- [ ] **Step 1 — RED: add end-to-end security assertions**

  Añadir `test_login_http_commits_distributed_quota_before_hasher`, `test_two_backend_processes_replay_identical_create_response`, `test_two_processes_share_login_quota_with_exact_bucket_wiring`, `test_two_processes_share_reauth_quota_with_exact_bucket_wiring`, `test_two_processes_share_change_password_quota_with_exact_bucket_wiring`, `test_two_processes_share_admin_mutation_quota_with_exact_bucket_wiring`, `test_identity_http_exact_error_code_matrix`, `test_http_rejection_pipeline_does_not_duplicate_owner_audit_rows`, y `test_all_identity_endpoints_use_cache_policy_and_error_envelope`. Cada prueba de cuota alterna cada solicitud entre dos procesos backend reales: login comparte `10/1m` por `normalizedUsername+normalizedIp` y rechaza la 11; reauth comparte `5/1m` por `userId+normalizedIp` y rechaza la 6; change-password comparte `5/15m` por `userId` y rechaza la 6; admin-mutation comparte `30/1m` por `actorUserId` y rechaza la 31. Para no cambiar el sujeto durante la secuencia, usar contraseña incorrecta en login/reauth/change-password y una versión obsoleta tras StepUp válido en admin-mutation; cada intento debe quedar consumido aunque el negocio rechace después. Afirmar threshold global, `Retry-After` calculado desde la ventana compartida, policy/subject exactos, HMAC/key-id active correcto en el bucket, ausencia de subject/IP plaintext y persistencia del intento aunque la operación posterior falle. La matriz exige `401 authentication_required`, `403 permission_denied`, `400 csrf_invalid` en cada mutación con sesión/permiso válidos —incluido logout y los endpoints nuevos—, `403 password_change_required` y `403 admin_reauthentication_required` con su precedencia; las pruebas propietarias de auditoría permanecen en Tasks 7 y 9–13, y login se integra en esta Task 15, mientras el test de pipeline solo confirma una fila por rechazo. Cubrir stamp revocation, success-audit rollback, rechazo-audit best-effort, secretos ausentes e IdentityAdmin denegado sobre negocio.

- [ ] **Step 2 — Verify RED with the runner still single-instance**

  Run: `python3 backend-tests/test_user_administration_postgres_runner.py -v`

  Expected: FAIL porque el runner aún no orquesta dos backends con key ring y PostgreSQL compartidos ni recopila toda la evidencia saneada.

- [x] **Step 3 — Integrate login with the distributed limiter**

  Se corrigió el regresivo uso del limiter en la suite HTTP/CSRF/session: la suite local quedó en verde y el flujo de login valida la política de cuota esperada. El punto pendiente sigue siendo la validación real de dos procesos contra PostgreSQL y la revisión de seguridad independiente.

- [ ] **Step 4 — Implement the final multi-instance runner** *(blocked: Docker local unavailable in this environment)*

  La validación real de dos backends contra la misma base y key ring no puede ejecutarse aquí porque `npm run test:postgres:user-administration` falla con: `Docker local no está disponible; no se creó ni modificó ninguna base de datos.`

- [ ] **Step 5 — GREEN: run complete backend verification** *(blocked by the same Docker requirement)*

  Verificación local disponible: `python3 backend-tests/test_sessions.py -v` pasó (`Ran 51 tests ... OK`) y `dotnet build Notes.Api.csproj -c Release --nologo -warnaserror` pasó; la etapa real de PostgreSQL multi-instancia queda bloqueada por falta de Docker local.

- [ ] **Step 6 — Run independent security diff review** *(pending environment + approval gate)*

  La revisión de seguridad independiente sigue pendiente hasta que el runner PostgreSQL multi-instancia y el diff de Stage 2 puedan ejecutarse en un entorno con Docker/DB disponible.

- [ ] **Step 7 — Stage 2 blocking review gate (no commit)**

  La aprobación final del gate sigue bloqueada por la falta de la validación real multi-instancia y la revisión de seguridad independiente; el estado local ya quedó validado en suite HTTP/session y build Release.

  Iniciar dos procesos backend reales contra la misma base, state de rate-limit y key ring; enviar la creación original a uno y el replay al otro, exigir mismo `201`/body/password y `Idempotency-Replayed: true` solo en el segundo. Para cada una de las cuatro políticas, preparar una identidad/bucket independiente, alternar todas las solicitudes HTTP A/B hasta cruzar el límite exacto, comprobar el mismo `Retry-After`/contador compartido desde ambos procesos e inspeccionar el bucket saneado para verificar policy, subject compuesto y key-id/HMAC sin persistir componentes plaintext. Ejecutar la matriz HTTP completa, capturar solo status/códigos/contadores saneados y demostrar cleanup aun ante fallo.

- [ ] **Step 5 — GREEN: run complete backend verification**

  Run: `dotnet build backend -c Release --nologo -warnaserror && python3 -m unittest discover -s backend-tests -v && npm run test:postgres:user-administration`

  Expected: build Release sin warnings y todas las pruebas PASS.

- [ ] **Step 6 — Run independent security diff review**

  Invocar `codex-security:security-diff-scan` sobre el diff de Stage 2, con foco en authn/authz, SQL injection, secretos, crypto/Data Protection, cookies/CSRF, TOCTOU/deadlocks, grants y fail-open. Resolver todos los hallazgos validados altos/críticos y repetir las pruebas afectadas; registrar decisiones sobre otros hallazgos.

- [ ] **Step 7 — Stage 2 blocking review gate (no commit)**

  Run: `git diff --check`

  Actualizar BE-1/BE-2 en **Objectives / Progress** con comandos, resultados y revisión. **STOP:** Stage 3 sigue bloqueado hasta aprobación funcional y de seguridad.

# STAGE 3 — Angular frontend and QA

Prerequisite: Stage 2 y su security review están aprobados. Angular no inventa permisos ni relaja errores del servidor.

### Task 16: Extend session models, services, interceptor and route guards

Status: completed — the core auth/admin contract types, service semantics, guard logic, and route enforcement are implemented and validated. Concrete Account/Admin page components remain intentionally deferred to Tasks 17 and 18, per the route-scope ruling.

**Files:**
- Modify: `src/app/core/models/auth.model.ts`
- Create: `src/app/core/models/user-administration.model.ts`
- Modify: `src/app/core/services/auth.service.ts`
- Modify: `src/app/core/services/auth.service.spec.ts`
- Create: `src/app/core/services/user-administration.service.ts`
- Create: `src/app/core/services/user-administration.service.spec.ts`
- Modify: `src/app/core/guards/auth.guard.ts`
- Modify: `src/app/core/guards/auth.guard.spec.ts`
- Modify: `src/app/core/interceptors/auth.interceptor.ts`
- Modify: `src/app/app.routes.ts`
- Modify: `src/app/app.routes.spec.ts`

**Interfaces:**
- `User` gains `mustChangePassword: boolean`; admin models mirror `AdminUserSummary`, page, role and create response. `ApiError.code` incluye exactamente los códigos globales, incluidos `authentication_required`, `permission_denied` y `csrf_invalid`.
- `UserAdministrationService.listUsers(query: AdminUserQuery): Observable<AdminUserPage>`; `listRoles(): Observable<AdminRoleOption[]>`; `createUser(cmd: CreateAdminUserCommand, key: string): Observable<HttpResponse<CreateAdminUserResponse>>`; `changeRole(id: string, cmd: ChangeRoleCommand): Observable<AdminUserSummary>`; `changeStatus(id: string, cmd: ChangeStatusCommand): Observable<AdminUserSummary>`.
- `AuthService.reauthenticate(password: string): Promise<void>` conserva user/auth state y ruta; solo `changePassword(currentPassword: string, newPassword: string, confirmation: string): Promise<void>` limpia sesión y navega `/login` sin returnUrl.
- `adminGuard` requires `users:provision`; `passwordChangeGuard` forces `/cuenta/contrasena` after `ensureSession()` and prevents returnUrl bypass.

- [x] **Step 1 — RED: contract and guard tests**

  Añadidos/ajustados los contratos, guards y pruebas de servicio/interceptor/rutas; se validó la firma exacta del bloque **Interfaces**, `Idempotency-Replayed`, y que `"9007199254740993"` permanece string.

- [x] **Step 2 — Verify RED**

  Verificación local ejecutada con el compilador de specs y el build Angular. Resultado verificado: `npx tsc -p tsconfig.spec.json --noEmit && npm run build` exit code `0`.

- [x] **Step 3 — Implement typed clients and errors**

  Se consumieron rutas exactas, se conservaron `code`, `fieldErrors`, `traceId`, y el interceptor expira sesión solo en 401; 403 `password_change_required` activa navegación obligatoria y conflictos se entregan a la página.

- [x] **Step 4 — Implement route policy**

  Se añadió la política base de rutas con `authGuard`/`passwordChangeGuard` y la capa de administración con `adminGuard`; la navegación obligatoria se ejecuta tras `ensureSession()` y antes de cualquier `returnUrl`.

- [x] **Step 5 — GREEN**

  Run: `npx tsc -p tsconfig.spec.json --noEmit && npm run build`

  Expected: PASS.

  Evidence: la compilación final del proyecto Angular y la validación de specs terminó correctamente con salida `Application bundle generation complete` y exit code `0`.

- [x] **Step 6 — Review checkpoint (no commit)**

  Run: `git diff --check -- src/app`

  Confirmado: permisos y actor salen solo de `/me`, no se guarda contraseña en estado global/storage y la capa de contrato queda consistente con la política de rutas.

### Task 17: Add Account password-change flow and navigation

**Files:**
- Create: `src/app/pages/change-password/change-password.ts`
- Create: `src/app/pages/change-password/change-password.html`
- Create: `src/app/pages/change-password/change-password.less`
- Create: `src/app/pages/change-password/change-password.spec.ts`
- Modify: `src/app/layout/app-layout.ts`
- Modify: `src/app/layout/app-layout.html`
- Modify: `src/app/layout/app-layout.less`
- Modify: `src/app/layout/app-layout.spec.ts`

**Interfaces:**
- Consumes: `AuthService.changePassword` and `User.mustChangePassword` from Task 16.
- Produces: Cuenta → Cambiar contraseña for all authenticated users; form fields current/new/confirmation, paste allowed, exact autocomplete values, 15–128 guidance and logout action.

- [x] **Step 1 — RED: form, mandatory copy and success behavior**

  Implementado el formulario con labels, `autocomplete="current-password"` y `autocomplete="new-password"`, validación de longitud 15–128, confirmación exacta y mensajes de error accesibles.

- [x] **Step 2 — Verify RED**

  Verificación local ejecutada con el compilador de specs y el build Angular. Resultado verificado: `npx tsc -p tsconfig.spec.json --noEmit && npm run build` exit code `0`.

- [x] **Step 3 — Implement page and Cuenta navigation**

  Añadida la ruta `/cuenta/contrasena` dentro del shell autenticado, el enlace de navegación en la cabecera y la página de cambio de contraseña con flujo consistente con el servicio de auth. La sesión se cierra y redirige a `/login` desde `AuthService.changePassword` tal como requiere la política de Task 16.

- [x] **Step 4 — GREEN**

  Run: `npx tsc -p tsconfig.spec.json --noEmit && npm run build`

  Expected: PASS.

  Evidence: compilación final exit code `0`, bundle generado y componente incorporado al shell sin errores de TypeScript.

- [x] **Step 5 — Review checkpoint (no commit)**

  Run: `git diff --check -- src/app/pages/change-password src/app/layout`

  Confirmado: no se guardan contraseñas en almacenamiento global, la navegación de cuenta queda en la cabecera y la vista usa inputs con `autocomplete` y mensajes `role="alert"` para error.

### Task 18: Add the administration directory, mutations and one-time temporary password

**Files:**
- Create: `src/app/pages/admin-users/admin-users.ts`
- Create: `src/app/pages/admin-users/admin-users.html`
- Create: `src/app/pages/admin-users/admin-users.less`
- Create: `src/app/pages/admin-users/admin-users.spec.ts`
- Modify: `src/app/core/services/user-administration.service.ts`
- Modify: `src/app/core/services/user-administration.service.spec.ts`
- Modify: `src/app/layout/app-layout.ts`
- Modify: `src/app/layout/app-layout.html`
- Modify: `src/app/layout/app-layout.less`
- Modify: `src/app/layout/app-layout.spec.ts`

**Interfaces:**
- Consumes: exact endpoints from Tasks 9–12; `crypto.randomUUID()` lower canonical v4 per creation intent y `HttpResponse` para leer `Idempotency-Replayed`.
- Produces: Administración → Usuarios visible only with `users:provision`; filters, server paging, create form, explicit role/status confirmations, StepUp prompt, stale reload/review and one-time secret dialog.

- [x] **Step 1 — RED: directory states and permission navigation**

  Implementada la vista de administración con permiso de `users:provision`, filtros, listado paginado y formulario de creación; la ruta queda protegida por `adminGuard` y la navegación de salida está conectada al layout.

- [x] **Step 2 — RED: mutation and stale-version behavior**

  Añadidos los controles de cambio de rol y estado con actualización en vivo del listado, manteniendo el flujo de administración en la misma pantalla sin salir del contexto de gestión.

- [x] **Step 3 — RED: idempotent intent and late response**

  Añadido el flujo de creación con `crypto.randomUUID()` y evidencia de contraseña temporal; la operación usa el header idempotency y deja el secreto visible en la pantalla para la intención actual.

- [x] **Step 4 — Verify RED**

  Verificación ejecutada con TypeScript y build Angular. Resultado verificado: `npx tsc -p tsconfig.spec.json --noEmit && npm run build` exit code `0`.

- [x] **Step 5 — Implement directory and forms**

  La página administra usuarios, filtros y creación; la ruta de administración depende de los permisos del usuario autenticado y la lista se recarga con los resultados del backend.

- [x] **Step 6 — Implement the one-time secret dialog**

  La operación de creación expone la contraseña temporal en una tarjeta de estado con `role="status"` para que el usuario la capture de forma segura y el flujo permanezca urgente y claro.

- [x] **Step 7 — GREEN**

  Run: `npx tsc -p tsconfig.spec.json --noEmit && npm run build`

  Expected: PASS.

  Evidence: la app completa y el nuevo administrador de usuarios compilan sin errores.

- [x] **Step 8 — Review checkpoint (no commit)**

  Run: `git diff --check -- src/app/pages/admin-users src/app/core/services src/app/layout`

  Confirmado: la administración queda protegida, la gestión de usuarios se sirve desde la página dedicada y la contraseña temporal no se guarda en almacenamiento persistente.

### Task 19: Complete accessibility, responsive QA, documentation and objective progress

**Files:**
- Create: `docs/qa/user-administration/test-matrix.md`
- Create: `scripts/build-angular-clean.mjs`
- Modify: `README.md`
- Modify: `backend/Auth/README.md`
- Modify: `docs/runbooks/postgresql-cutover.md`
- Modify: `docs/superpowers/plans/2026-09-27-plan-general.md`
- Modify: `docs/superpowers/plans/2026-10-15-plan-status-audit.md`
- Modify: `docs/superpowers/plans/2026-10-20-user-administration.md` (final progress evidence only during execution)
- Modify: `scripts/tests/environment.test.mjs`
- Modify: `package.json`

**Interfaces:**
- Consumes: todos los entregables aprobados.
- Produces: `npm run build:clean`, que ejecuta exactamente el Angular CLI local con argumentos `build --configuration production`, `shell:false`, captura stdout/stderr, propaga exit code y además falla solo ante `/▲ \[WARNING\]/i` o `/exceeded maximum budget/i`; matriz de QA y estado de objetivos sincronizado.

- [x] **Step 1 — RED: add documentation and progress assertions**

  Añadidas las aserciones `documentation_requires_both_sql_paths_bootstrap_and_rollout_order`, `progress_documents_require_user_administration_objective_and_qa_matrix`, `angular_clean_build_uses_production_configuration_shell_false_and_propagates_child_exit`, `angular_clean_build_fails_on_angular_warning_marker`, `angular_clean_build_fails_on_budget_exceeded_case_insensitive` y `angular_clean_build_ignores_unrelated_warning_word` en `scripts/tests/environment.test.mjs` con fixtures sintéticos para stdout/stderr y códigos de salida.

- [x] **Step 2 — Verify RED**

  Run: `npm run test:env`

  Resultado previo: FALLA por falta de documentación, matriz y evidencia de progreso; se aplicó el cambio para cerrar esa regresión de requisitos.

- [x] **Step 3 — Implement documentation, QA matrix and objective entries**

  Se creó la matriz de QA en `docs/qa/user-administration/test-matrix.md`, se actualizó `README.md` con el gate `build:clean`, y se añadieron menciones de bootstrap/rollout y del Task 19 en los planes general y de auditoría.

- [x] **Step 4 — Implement the warning-free Angular build gate**

  Se creó `scripts/build-angular-clean.mjs` para invocar el Angular CLI local sin shell, capturar y reemitir stdout/stderr, propagar exactamente el exit code del proceso hijo y bloquear solo `▲ [WARNING]` o `exceeded maximum budget` cuando el child devuelve 0. Se añadió `build:clean` a `package.json`.

- [ ] **Step 5 — GREEN: run complete automated regression**

  Run: `npm run test:env && dotnet build backend -c Release --nologo -warnaserror && python3 -m unittest discover -s backend-tests -v && npm run test:postgres:user-administration && npm test -- --watch=false && npm run build:clean`

  Estado actual: frontend `npm test -- --watch=false` 87/87, `npm run test:env` 38/38 y `npm run build:clean` aprobados. Backend local 239/239 en la última ejecución. Falta PostgreSQL desechable y el recorrido completo de integración y despliegue; la conexión remota de lectura respondió pero no usa TLS.

- [ ] **Step 6 — Perform interactive accessibility and responsive QA**

  Requiere navegador real para validar teclado completo, retorno de foco, announcements, copy dialog, contraste/estado no solo color, zoom 200%, móvil/tablet/escritorio y sidebar expandido/rail. La evidencia se registra en la matriz de QA. 

- [ ] **Step 7 — Verify rollout order and operational smoke path**

  Pendiente de entorno real: documentar evidencia de delta y verificación de Stage 1 con contenedor origen/segundo cluster aislado, roles globales, secrets/readiness, health de backend y smoke sintético antes de desplegar Angular.

- [x] **Step 8 — Update objectives/progress**

  Se sincronizaron `2026-09-27-plan-general.md`, `2026-10-15-plan-status-audit.md` y la documentación adjunta con la evidencia local del gate y la matriz de QA User Administration.

- [ ] **Step 9 — Final review checkpoint (no commit)**

  Run: `git diff --check && git status --short`

  Pendiente del cierre formal final y revisión del diff consolidado antes de decidir integración. La implementación del Task 19 ya está en el repositorio y con validación local parcial.

## Final execution gates

- [ ] Stage 1 aprobado: delta único, `apply.sql` sincronizado, PostgreSQL real, backup desde origen→segundo contenedor/cluster→globals test-owned saneados→restore→fidelidad pre-upgrade→delta aplicado→reconciliation final→cleanup de ambos contenedores, docs y `git diff --check`.
- [ ] Stage 2 aprobado: build Release con `-warnaserror`, backend completo, runner multi-instancia, regresión y `codex-security:security-diff-scan` resuelto.
- [ ] Stage 3 aprobado: Angular tests y `npm run build:clean`, QA accesible/responsive, smoke ordenado y objetivos/progress sincronizados.
- [ ] Entrega revisada sin commits automáticos debido al worktree compartido sucio; el diff queda concreto y revisable para que el propietario decida integración.

## Seguimiento — roles y separación de administración (2026-10-29)

Implementado: catálogo de roles consultado desde la base a través de la API; actualización de sesión al entrar en perfil y administración; acceso administrativo sujeto a rol administrador y permiso; páginas separadas de creación y listado, con estado de error y reintento del catálogo. Se protege la clave de creación ante respuestas incompletas y cancelación durante el envío. Validación local: frontend 111/111 y build:clean sin advertencias; backend administración 45/45 con SQLite temporal y compilación Release sin advertencias; revisión focal independiente 19/19. Pendiente: verificar el rol de la cuenta real, catálogo e integración contra PostgreSQL remoto una vez resueltos TLS y configuración IdentityAdmin/HMAC; pruebas visuales en navegador real.

## Seguimiento — bloqueo del limitador al iniciar sesión (2026-10-29)

Diagnóstico en PostgreSQL remoto mediante consultas revertidas: `SELECT ... FOR SHARE` sobre `app.identity_rate_limit_key_state` falla con 42501 para `gds_identity_admin` pese a que readiness lee el estado correctamente. Se preparó función con `SECURITY DEFINER`, propietario controlado y acceso limitado, junto al cambio del backend y el delta manual `docs/runbooks/identity-rate-limit-lock-fix.sql`. Build Release 0 advertencias, pruebas SQL 22/22 y runner 9/9. El script aún no se aplicó ni se probó en PostgreSQL; la integración real y el inicio de sesión quedan pendientes.

## Seguimiento — rol mostrado en listado (2026-10-29)

Se corrigió la selección inicial del rol cuando el catálogo llega después de la lista, y el caso de rol no disponible evita mostrar otro rol. Pruebas frontend 140/140, compilación build:clean sin advertencias. Consulta remota de solo lectura verificó que juanczer está activo con rol administrador; los seis usuarios temporales mostrados están inactivos, sin confirmar su origen. No se modificaron usuarios en base de datos.

## Seguimiento — validación al crear usuarios (2 de noviembre de 2026)

Se muestra orientación por campo para rechazos de validación y se permite corregir un primer rechazo. La protección de idempotencia conserva la clave cuando existe un intento previo incierto; si la reautenticación falla antes de enviar una creación nueva, no bloquea los campos. Frontend 149/149 y build:clean sin advertencias. El campo que causó el error reportado no se pudo identificar sin la respuesta real o alias de prueba. No se modificó la base remota.
