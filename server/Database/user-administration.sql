-- Execute this complete file with the deployment account.
-- The environment LOGIN account, CONNECT grant and gds_identity_admin
-- membership are configured securely outside this file.
-- Role invariants, administration guards, exact ACL reconciliation and
-- read-only verification reports are installed below.
-- IMPORTANT: Run this complete file in an exclusive deployment session. The
-- leading ROLLBACK intentionally discards any unfinished transaction in that
-- session so an earlier SQLSTATE 25P02 cannot block this deployment.
ROLLBACK;
BEGIN;

-- Freeze existing identities while checking the upgrade preconditions.
LOCK TABLE public."AspNetUsers", public."AspNetRoles", public."AspNetUserRoles"
    IN SHARE ROW EXCLUSIVE MODE;
DO $preflight$
BEGIN
    IF EXISTS (
        SELECT 1 FROM public."AspNetRoles"
        WHERE "Name" IS NULL
           OR "Name" NOT IN ('operador', 'validador', 'administrador')
           OR "NormalizedName" IS DISTINCT FROM CASE "Name"
               WHEN 'operador' THEN 'OPERADOR'
               WHEN 'validador' THEN 'VALIDADOR'
               WHEN 'administrador' THEN 'ADMINISTRADOR'
           END
    ) OR EXISTS (
        SELECT 1 FROM public."AspNetRoles" GROUP BY "Name" HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'upgrade requires a canonical role catalog; resolve existing roles explicitly';
    END IF;

    IF EXISTS (
        SELECT u."Id"
        FROM public."AspNetUsers" u
        LEFT JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        LEFT JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        GROUP BY u."Id"
        HAVING count(ur."RoleId") <> 1
            OR count(r."Id") <> 1
    ) OR EXISTS (
        SELECT 1 FROM public."AspNetUserRoles" ur
        LEFT JOIN public."AspNetUsers" u ON u."Id" = ur."UserId"
        LEFT JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE u."Id" IS NULL OR r."Id" IS NULL
    ) THEN
        RAISE EXCEPTION 'upgrade requires exactly one canonical role per existing user; resolve memberships explicitly';
    END IF;

    IF EXISTS (SELECT 1 FROM public."AspNetUsers") AND NOT EXISTS (
        SELECT 1 FROM public."AspNetUsers" u
        JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE u."IsActive" AND r."Name" = 'administrador'
          AND r."NormalizedName" = 'ADMINISTRADOR'
    ) THEN
        RAISE EXCEPTION 'upgrade requires at least one active administrator';
    END IF;
END;
$preflight$;

-- User administration: structural contract shared by fresh and upgrade paths.
ALTER TABLE public."AspNetUsers"
    ADD COLUMN IF NOT EXISTS "MustChangePassword" boolean NOT NULL DEFAULT false;
ALTER TABLE public."AspNetUsers"
    ADD COLUMN IF NOT EXISTS "PasswordChangedAt" timestamptz NULL;
ALTER TABLE public."AspNetUsers"
    ADD COLUMN IF NOT EXISTS "ManagementVersion" bigint NOT NULL DEFAULT 1 CHECK ("ManagementVersion" > 0);

-- Only canonical catalog rows are seeded; no account or membership is created.
INSERT INTO public."AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
VALUES
    (md5('identity-role:operador')::uuid, 'operador', 'OPERADOR', NULL),
    (md5('identity-role:validador')::uuid, 'validador', 'VALIDADOR', NULL),
    (md5('identity-role:administrador')::uuid, 'administrador', 'ADMINISTRADOR', NULL)
ON CONFLICT ("NormalizedName") WHERE "NormalizedName" IS NOT NULL DO NOTHING;

CREATE TABLE IF NOT EXISTS app.identity_idempotency_receipts (
    key_sha256 bytea PRIMARY KEY CHECK (octet_length(key_sha256) = 32),
    actor_user_id uuid NOT NULL REFERENCES public."AspNetUsers"("Id") ON DELETE RESTRICT,
    payload_sha256 bytea NOT NULL CHECK (octet_length(payload_sha256) = 32),
    user_id uuid NOT NULL REFERENCES public."AspNetUsers"("Id") ON DELETE RESTRICT,
    protected_response bytea NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    CONSTRAINT ck_identity_receipt_expiry CHECK (expires_at > created_at)
);
CREATE INDEX IF NOT EXISTS ix_identity_receipts_expiry
    ON app.identity_idempotency_receipts(expires_at) WHERE protected_response IS NOT NULL;

CREATE TABLE IF NOT EXISTS app.identity_rate_limit_key_state (
    key_id varchar(64) NOT NULL PRIMARY KEY CHECK (key_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
    state text NOT NULL CHECK (state IN ('accepted', 'active', 'retained', 'retired')),
    changed_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_rate_limit_one_active_key
    ON app.identity_rate_limit_key_state(state) WHERE state = 'active';

-- The partial index enforces at most one; the deferred check enforces at
-- least one at commit while allowing active -> retained, accepted -> active.
CREATE OR REPLACE FUNCTION app.assert_identity_rate_limit_active_key()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $rate_key$
BEGIN
    IF (SELECT count(*) FROM app.identity_rate_limit_key_state WHERE state = 'active') <> 1 THEN
        RAISE EXCEPTION 'identity rate-limit state requires exactly one active key';
    END IF;
    RETURN NULL;
END;
$rate_key$;

DROP TRIGGER IF EXISTS trg_identity_rate_limit_active_key ON app.identity_rate_limit_key_state;
CREATE CONSTRAINT TRIGGER trg_identity_rate_limit_active_key
    AFTER INSERT OR UPDATE OR DELETE ON app.identity_rate_limit_key_state
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.assert_identity_rate_limit_active_key();

-- This nonsecret version identifier must never reset a subsequent rotation.
INSERT INTO app.identity_rate_limit_key_state (key_id, state, changed_at)
SELECT 'rate-limit-v1', 'active', now()
WHERE NOT EXISTS (SELECT 1 FROM app.identity_rate_limit_key_state)
ON CONFLICT (key_id) DO NOTHING;

CREATE TABLE IF NOT EXISTS app.identity_rate_limits (
    key_id varchar(64) NOT NULL REFERENCES app.identity_rate_limit_key_state(key_id) ON DELETE RESTRICT
        CHECK (key_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
    bucket_hmac bytea NOT NULL CHECK (octet_length(bucket_hmac) = 32),
    window_started_at timestamptz NOT NULL,
    window_ends_at timestamptz NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (key_id, bucket_hmac, window_started_at),
    CONSTRAINT ck_identity_rate_limit_window CHECK (window_ends_at > window_started_at),
    CONSTRAINT ck_identity_rate_limit_expiry CHECK (expires_at >= window_ends_at)
);
CREATE INDEX IF NOT EXISTS ix_identity_rate_limits_expiry
    ON app.identity_rate_limits(expires_at);

-- Also validate on reapplication, when no state change enqueues the trigger.
DO $identity_structure$
BEGIN
    IF (SELECT count(*) FROM app.identity_rate_limit_key_state WHERE state = 'active') <> 1 THEN
        RAISE EXCEPTION 'identity rate-limit state requires exactly one active key';
    END IF;
END;
$identity_structure$;

-- Deferred membership FK permits membership/user insertion in either order.
DO $identity_membership_fk$
DECLARE
    v_constraint text;
    v_count integer := 0;
BEGIN
    FOR v_constraint IN
        SELECT c.conname FROM pg_catalog.pg_constraint c
        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.conrelid
            AND a.attname = 'UserId' AND c.conkey = ARRAY[a.attnum]
        JOIN pg_catalog.pg_attribute target ON target.attrelid = c.confrelid
            AND target.attname = 'Id' AND c.confkey = ARRAY[target.attnum]
        WHERE c.contype = 'f' AND c.conrelid = 'public."AspNetUserRoles"'::regclass
          AND c.confrelid = 'public."AspNetUsers"'::regclass
    LOOP
        EXECUTE pg_catalog.format('ALTER TABLE public."AspNetUserRoles" ALTER CONSTRAINT %I DEFERRABLE INITIALLY DEFERRED', v_constraint);
        v_count := v_count + 1;
    END LOOP;
    IF v_count <> 1 THEN
        RAISE EXCEPTION 'expected exactly one membership user foreign key';
    END IF;
END;
$identity_membership_fk$;

CREATE OR REPLACE FUNCTION app.enforce_identity_user_role_cardinality()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $identity_cardinality$
DECLARE
    v_users uuid[] := ARRAY[]::uuid[];
    v_user uuid;
BEGIN
    IF TG_TABLE_NAME = 'AspNetUsers' THEN
        IF TG_OP <> 'INSERT' THEN v_users := pg_catalog.array_append(v_users, OLD."Id"); END IF;
        IF TG_OP <> 'DELETE' THEN v_users := pg_catalog.array_append(v_users, NEW."Id"); END IF;
    ELSE
        IF TG_OP <> 'INSERT' THEN v_users := pg_catalog.array_append(v_users, OLD."UserId"); END IF;
        IF TG_OP <> 'DELETE' THEN v_users := pg_catalog.array_append(v_users, NEW."UserId"); END IF;
    END IF;
    FOREACH v_user IN ARRAY v_users LOOP
        IF NOT EXISTS (SELECT 1 FROM public."AspNetUsers" u WHERE u."Id" = v_user) THEN
            CONTINUE; -- Only deleted users are exempt from final cardinality.
        END IF;
        IF (SELECT count(*) FROM public."AspNetUserRoles" ur WHERE ur."UserId" = v_user) <> 1
           OR (SELECT count(*) FROM public."AspNetUserRoles" ur
               JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
               WHERE ur."UserId" = v_user AND (r."Name", r."NormalizedName") IN
                   (('operador', 'OPERADOR'), ('validador', 'VALIDADOR'),
                    ('administrador', 'ADMINISTRADOR'))) <> 1 THEN
            RAISE EXCEPTION 'identity_user_role_cardinality' USING ERRCODE = '23514';
        END IF;
    END LOOP;
    RETURN NULL;
END;
$identity_cardinality$;

CREATE OR REPLACE FUNCTION app.enforce_identity_role_catalog()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $identity_catalog$
DECLARE
    v_roles uuid[] := ARRAY[]::uuid[];
BEGIN
    -- Canonical semantics remain attached to the IDs installed by the seed,
    -- including legacy Identity UUIDs. Validate each queued mutation as well
    -- as final state: a rename/swap must not move admin authority between IDs.
    IF TG_OP IN ('INSERT', 'DELETE') THEN
        RAISE EXCEPTION 'identity_role_catalog_immutable' USING ERRCODE = '23514';
    END IF;
    IF NEW."Id" IS DISTINCT FROM OLD."Id"
       OR NEW."Name" IS DISTINCT FROM OLD."Name"
       OR NEW."NormalizedName" IS DISTINCT FROM OLD."NormalizedName" THEN
        RAISE EXCEPTION 'identity_role_catalog_immutable' USING ERRCODE = '23514';
    END IF;
    IF TG_OP <> 'INSERT' THEN v_roles := pg_catalog.array_append(v_roles, OLD."Id"); END IF;
    IF TG_OP <> 'DELETE' THEN v_roles := pg_catalog.array_append(v_roles, NEW."Id"); END IF;
    IF (SELECT count(*) FROM public."AspNetRoles") <> 3 OR EXISTS (
        SELECT 1 FROM (VALUES ('operador', 'OPERADOR'), ('validador', 'VALIDADOR'),
                             ('administrador', 'ADMINISTRADOR')) AS expected(name, normalized_name)
        LEFT JOIN public."AspNetRoles" r ON r."Name" = expected.name
            AND r."NormalizedName" = expected.normalized_name
        GROUP BY expected.name HAVING count(r."Id") <> 1
    ) THEN
        RAISE EXCEPTION 'identity_role_catalog' USING ERRCODE = '23514';
    END IF;
    -- Revalidate users attached to either the old or new role identity.
    IF EXISTS (
        SELECT u."Id" FROM public."AspNetUsers" u
        JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        WHERE ur."RoleId" = ANY(v_roles)
        GROUP BY u."Id" HAVING count(*) <> 1
            OR (SELECT count(*) FROM public."AspNetUserRoles" all_roles
                WHERE all_roles."UserId" = u."Id") <> 1
    ) THEN
        RAISE EXCEPTION 'identity_user_role_cardinality' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$identity_catalog$;

-- Callers use SET LOCAL app.actor_user_id = '<authenticated UUID>'.
-- Every sensitive write takes the exact pg_catalog.pg_advisory_xact_lock(bigint)
-- overload before reading actor/bootstrap eligibility or final administrator state.
CREATE OR REPLACE FUNCTION app.guard_identity_admin_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $identity_admin_guard$
DECLARE
    v_actor uuid;
    v_target uuid;
    v_bootstrap boolean := false;
    v_xid text;
BEGIN
    IF TG_TABLE_NAME = 'AspNetUsers' THEN
        IF TG_OP = 'UPDATE' THEN
            IF NEW."Id" IS NOT DISTINCT FROM OLD."Id"
               AND NEW."IsActive" IS NOT DISTINCT FROM OLD."IsActive" THEN
                IF TG_WHEN = 'BEFORE' THEN RETURN NEW; ELSE RETURN NULL; END IF;
            END IF;
        END IF;
        IF TG_OP = 'DELETE' THEN v_target := OLD."Id"; ELSE v_target := NEW."Id"; END IF;
    ELSE
        IF TG_OP = 'DELETE' THEN v_target := OLD."UserId"; ELSE v_target := NEW."UserId"; END IF;
    END IF;

    -- A fixed transaction snapshot could miss a preceding lock holder's commit.
    -- Require fresh statement snapshots for both ordinary writes and bootstrap.
    IF pg_catalog.current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'identity_admin_isolation_unsupported' USING ERRCODE = '23514';
    END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(714205736012345678::bigint);
    v_actor := NULLIF(pg_catalog.current_setting('app.actor_user_id', true), '')::uuid;
    IF v_actor IS NULL THEN
        IF pg_catalog.current_setting('app.bootstrap_mode', true) IS DISTINCT FROM 'on'
           OR NOT pg_catalog.pg_has_role(session_user, 'gds_db_deployer', 'member') THEN
            RAISE EXCEPTION 'identity_actor_required' USING ERRCODE = '23514';
        END IF;
        v_xid := pg_catalog.pg_current_xact_id()::text;
        IF pg_catalog.current_setting('app.bootstrap_xact_id', true) IS DISTINCT FROM v_xid THEN
            IF TG_WHEN <> 'BEFORE' OR TG_OP <> 'INSERT'
               OR NOT (NOT EXISTS (SELECT 1 FROM public."AspNetUsers")) THEN
                RAISE EXCEPTION 'identity_bootstrap_requires_empty_database' USING ERRCODE = '23514';
            END IF;
            -- These local markers are only accepted with the trusted deployment
            -- session above. They bind eligibility and the sole user to this xid.
            PERFORM pg_catalog.set_config('app.bootstrap_xact_id', v_xid, true);
            PERFORM pg_catalog.set_config('app.bootstrap_user_id', v_target::text, true);
        END IF;
        IF TG_OP <> 'INSERT' OR v_target::text IS DISTINCT FROM
                pg_catalog.current_setting('app.bootstrap_user_id', true) THEN
            RAISE EXCEPTION 'identity_bootstrap_requires_single_admin' USING ERRCODE = '23514';
        END IF;
        IF TG_TABLE_NAME = 'AspNetUsers' THEN
            IF NOT NEW."IsActive" OR NEW."MustChangePassword"
               OR NEW."PasswordChangedAt" IS NULL OR NEW."ManagementVersion" <> 1 THEN
                RAISE EXCEPTION 'identity_bootstrap_requires_active_admin' USING ERRCODE = '23514';
            END IF;
        ELSIF NOT EXISTS (SELECT 1 FROM public."AspNetRoles" r
                          WHERE r."Id" = NEW."RoleId" AND r."Name" = 'administrador'
                            AND r."NormalizedName" = 'ADMINISTRADOR') THEN
            RAISE EXCEPTION 'identity_bootstrap_requires_admin_role' USING ERRCODE = '23514';
        END IF;
        v_bootstrap := true;
    ELSIF NOT EXISTS (
        SELECT 1 FROM public."AspNetUsers" u
        JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE u."Id" = v_actor AND u."IsActive"
          AND r."Name" = 'administrador' AND r."NormalizedName" = 'ADMINISTRADOR'
    ) THEN
        RAISE EXCEPTION 'identity_actor_required' USING ERRCODE = '23514';
    END IF;

    IF NOT v_bootstrap AND TG_WHEN = 'BEFORE' THEN
        IF TG_TABLE_NAME = 'AspNetUsers' THEN
            IF TG_OP = 'DELETE' THEN
                IF OLD."Id" = v_actor THEN
                    RAISE EXCEPTION 'identity_self_management_forbidden' USING ERRCODE = '23514';
                END IF;
            ELSIF TG_OP = 'UPDATE' THEN
                IF OLD."Id" = v_actor AND (NOT NEW."IsActive" OR NEW."Id" <> OLD."Id") THEN
                    RAISE EXCEPTION 'identity_self_management_forbidden' USING ERRCODE = '23514';
                END IF;
            END IF;
        ELSIF TG_OP <> 'INSERT' THEN
            IF OLD."UserId" = v_actor THEN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'identity_self_management_forbidden' USING ERRCODE = '23514';
                ELSIF NEW."UserId" IS DISTINCT FROM OLD."UserId"
                   OR NEW."RoleId" IS DISTINCT FROM OLD."RoleId" THEN
                    RAISE EXCEPTION 'identity_self_management_forbidden' USING ERRCODE = '23514';
                END IF;
            END IF;
        END IF;
    END IF;

    IF TG_WHEN = 'AFTER' THEN
        IF v_bootstrap AND (SELECT count(*) FROM public."AspNetUsers") <> 1 THEN
            RAISE EXCEPTION 'identity_bootstrap_requires_single_admin' USING ERRCODE = '23514';
        END IF;
        IF NOT EXISTS (
            SELECT 1 FROM public."AspNetUsers" u
            JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
            JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE u."IsActive" AND r."Name" = 'administrador'
              AND r."NormalizedName" = 'ADMINISTRADOR'
        ) THEN
            RAISE EXCEPTION 'identity_last_active_admin' USING ERRCODE = '23514';
        END IF;
        RETURN NULL;
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$identity_admin_guard$;

CREATE OR REPLACE FUNCTION app.reject_immutable_change()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $immutable_change$
BEGIN
    RAISE EXCEPTION '% rows are append-only', TG_TABLE_NAME USING ERRCODE = '55000';
END;
$immutable_change$;

DROP TRIGGER IF EXISTS trg_identity_user_role_cardinality ON public."AspNetUsers";
CREATE CONSTRAINT TRIGGER trg_identity_user_role_cardinality
    AFTER INSERT OR UPDATE ON public."AspNetUsers"
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.enforce_identity_user_role_cardinality();
DROP TRIGGER IF EXISTS trg_identity_membership_cardinality ON public."AspNetUserRoles";
CREATE CONSTRAINT TRIGGER trg_identity_membership_cardinality
    AFTER INSERT OR UPDATE OR DELETE ON public."AspNetUserRoles"
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.enforce_identity_user_role_cardinality();
DROP TRIGGER IF EXISTS trg_identity_role_catalog ON public."AspNetRoles";
CREATE CONSTRAINT TRIGGER trg_identity_role_catalog
    AFTER INSERT OR UPDATE OR DELETE ON public."AspNetRoles"
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.enforce_identity_role_catalog();
DROP TRIGGER IF EXISTS trg_identity_users_admin_guard ON public."AspNetUsers";
CREATE TRIGGER trg_identity_users_admin_guard
    BEFORE INSERT OR UPDATE OR DELETE ON public."AspNetUsers"
    FOR EACH ROW EXECUTE FUNCTION app.guard_identity_admin_mutation();
DROP TRIGGER IF EXISTS trg_identity_memberships_admin_guard ON public."AspNetUserRoles";
CREATE TRIGGER trg_identity_memberships_admin_guard
    BEFORE INSERT OR UPDATE OR DELETE ON public."AspNetUserRoles"
    FOR EACH ROW EXECUTE FUNCTION app.guard_identity_admin_mutation();
DROP TRIGGER IF EXISTS trg_identity_users_admin_final ON public."AspNetUsers";
CREATE CONSTRAINT TRIGGER trg_identity_users_admin_final
    AFTER INSERT OR UPDATE OR DELETE ON public."AspNetUsers"
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.guard_identity_admin_mutation();
DROP TRIGGER IF EXISTS trg_identity_memberships_admin_final ON public."AspNetUserRoles";
CREATE CONSTRAINT TRIGGER trg_identity_memberships_admin_final
    AFTER INSERT OR UPDATE OR DELETE ON public."AspNetUserRoles"
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.guard_identity_admin_mutation();

REVOKE EXECUTE ON FUNCTION app.enforce_identity_user_role_cardinality() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.enforce_identity_role_catalog() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.guard_identity_admin_mutation() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.reject_immutable_change() FROM PUBLIC;
-- Identity administration privilege reconciliation. Infrastructure LOGIN,
-- CONNECT and environment membership remain deployment responsibilities.
DO $identity_admin_role$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin') THEN
        CREATE ROLE gds_identity_admin NOLOGIN NOINHERIT;
    END IF;
END;
$identity_admin_role$;
ALTER ROLE gds_identity_admin NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1 PASSWORD NULL;

-- The administration role may be granted to environment LOGIN accounts, but
-- it must not inherit from any other role. Each failed inspection/revocation
-- aborts the surrounding transaction.
DO $identity_admin_memberships$
DECLARE
    v_parent name;
BEGIN
    FOR v_parent IN
        SELECT parent.rolname
        FROM pg_catalog.pg_auth_members membership
        JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
        JOIN pg_catalog.pg_roles member ON member.oid = membership.member
        WHERE member.rolname = 'gds_identity_admin'
    LOOP
        EXECUTE pg_catalog.format('REVOKE %I FROM gds_identity_admin', v_parent);
    END LOOP;
END;
$identity_admin_memberships$;

-- Fail closed if a pre-existing privileged routine is owned or granted unexpectedly.
DO $identity_lock_preflight$
DECLARE
    v_owner oid := (SELECT c.relowner FROM pg_catalog.pg_class c
                    WHERE c.oid = 'app.identity_rate_limit_key_state'::pg_catalog.regclass);
    v_function oid := pg_catalog.to_regprocedure('app.lock_identity_rate_limit_key_state()');
    v_current oid := (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user);
BEGIN
    IF v_owner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin')
       OR NOT (v_owner = v_current OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                              WHERE oid = v_current AND rolsuper))
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'USAGE')
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'CREATE')
       OR (NOT pg_catalog.has_table_privilege(v_owner, 'app.identity_rate_limit_key_state', 'SELECT')
           OR NOT pg_catalog.has_table_privilege(v_owner, 'app.identity_rate_limit_key_state', 'UPDATE')) THEN
        RAISE EXCEPTION 'identity_lock_requires_table_owner_or_superuser';
    END IF;
    IF v_function IS NOT NULL AND (
        (SELECT proowner FROM pg_catalog.pg_proc WHERE oid = v_function) <> v_owner
        OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_proc p
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
            WHERE p.oid = v_function AND acl.privilege_type = 'EXECUTE'
              AND acl.grantee NOT IN (v_owner, (SELECT oid FROM pg_catalog.pg_roles
                                               WHERE rolname = 'gds_identity_admin'))
        )) THEN
        RAISE EXCEPTION 'identity_lock_existing_owner_or_acl_untrusted';
    END IF;
END;
$identity_lock_preflight$;

-- Locks key-state rows for the current transaction without granting table UPDATE.
CREATE OR REPLACE FUNCTION app.lock_identity_rate_limit_key_state()
RETURNS TABLE (key_id varchar(64), state text, changed_at timestamptz)
LANGUAGE sql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog
AS $identity_lock$
    SELECT s.key_id, s.state::text, s.changed_at
    FROM app.identity_rate_limit_key_state AS s
    ORDER BY s.key_id
    FOR SHARE
$identity_lock$;
DO $identity_lock_owner$
DECLARE v_owner name;
BEGIN
    SELECT pg_catalog.pg_get_userbyid(c.relowner) INTO STRICT v_owner
    FROM pg_catalog.pg_class c
    WHERE c.oid = 'app.identity_rate_limit_key_state'::pg_catalog.regclass;
    EXECUTE pg_catalog.format('ALTER FUNCTION app.lock_identity_rate_limit_key_state() OWNER TO %I', v_owner);
END;
$identity_lock_owner$;
REVOKE ALL ON FUNCTION app.lock_identity_rate_limit_key_state() FROM PUBLIC;

-- Fail closed if a pre-existing privileged routine is owned or granted unexpectedly.
DO $identity_membership_lock_preflight$
DECLARE
    v_owner oid := (SELECT c.relowner FROM pg_catalog.pg_class c
                    WHERE c.oid = 'public."AspNetUserRoles"'::pg_catalog.regclass);
    v_function oid := pg_catalog.to_regprocedure('app.lock_identity_user_roles(uuid)');
    v_current oid := (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user);
BEGIN
    IF v_owner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin')
       OR NOT (v_owner = v_current OR EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                              WHERE oid = v_current AND rolsuper))
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'USAGE')
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'CREATE')
       OR (NOT pg_catalog.has_table_privilege(v_owner, 'public."AspNetUserRoles"', 'SELECT')
           OR NOT pg_catalog.has_table_privilege(v_owner, 'public."AspNetUserRoles"', 'UPDATE')) THEN
        RAISE EXCEPTION 'identity_membership_lock_requires_table_owner_or_superuser';
    END IF;
    IF v_function IS NOT NULL AND (
        (SELECT proowner FROM pg_catalog.pg_proc WHERE oid = v_function) <> v_owner
        OR EXISTS (
            SELECT 1 FROM pg_catalog.pg_proc p
            CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
            WHERE p.oid = v_function AND acl.privilege_type = 'EXECUTE'
              AND acl.grantee NOT IN (v_owner, (SELECT oid FROM pg_catalog.pg_roles
                                               WHERE rolname = 'gds_identity_admin'))
        )) THEN
        RAISE EXCEPTION 'identity_membership_lock_existing_owner_or_acl_untrusted';
    END IF;
END;
$identity_membership_lock_preflight$;

-- Locks user membership rows for the current transaction without granting table UPDATE.
CREATE OR REPLACE FUNCTION app.lock_identity_user_roles(p_user uuid)
RETURNS SETOF uuid
LANGUAGE sql VOLATILE SECURITY DEFINER
SET search_path = pg_catalog
AS $identity_lock$
    SELECT s."RoleId"
    FROM public."AspNetUserRoles" AS s
    WHERE s."UserId" = p_user
    ORDER BY s."RoleId"
    FOR UPDATE
$identity_lock$;
DO $identity_membership_lock_owner$
DECLARE v_owner name;
BEGIN
    SELECT pg_catalog.pg_get_userbyid(c.relowner) INTO STRICT v_owner
    FROM pg_catalog.pg_class c
    WHERE c.oid = 'public."AspNetUserRoles"'::pg_catalog.regclass;
    EXECUTE pg_catalog.format('ALTER FUNCTION app.lock_identity_user_roles(uuid) OWNER TO %I', v_owner);
END;
$identity_membership_lock_owner$;
REVOKE ALL ON FUNCTION app.lock_identity_user_roles(uuid) FROM PUBLIC;


-- Remove every direct managed grant before rebuilding the exact allowlist.
REVOKE ALL PRIVILEGES ON SCHEMA public, app FROM gds_identity_admin;
REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public, app FROM gds_identity_admin;
REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public, app FROM gds_identity_admin;
REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA public, app FROM gds_identity_admin;
REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA app FROM gds_identity_admin;
REVOKE EXECUTE ON FUNCTION app.enforce_identity_user_role_cardinality() FROM gds_identity_admin;
REVOKE EXECUTE ON FUNCTION app.enforce_identity_role_catalog() FROM gds_identity_admin;
REVOKE EXECUTE ON FUNCTION app.guard_identity_admin_mutation() FROM gds_identity_admin;
REVOKE EXECUTE ON FUNCTION app.reject_immutable_change() FROM gds_identity_admin;

GRANT USAGE ON SCHEMA public, app TO gds_identity_admin;
GRANT SELECT ("Id", "UserName", "NormalizedUserName", "DisplayName", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "IsActive", "CreatedAt", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin;
GRANT INSERT ("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "DisplayName", "IsActive", "CreatedAt", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin;
GRANT UPDATE ("PasswordHash", "SecurityStamp", "ConcurrencyStamp", "IsActive", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin;
GRANT SELECT ("Id", "Name", "NormalizedName") ON public."AspNetRoles" TO gds_identity_admin;
GRANT SELECT ("UserId", "RoleId"), INSERT ("UserId", "RoleId"), DELETE ON public."AspNetUserRoles" TO gds_identity_admin;
GRANT INSERT (actor_user_id, action, outcome, entity_type, entity_id, correlation_id, source_ip_hash, changes, security_context) ON app.audit_events TO gds_identity_admin;
GRANT SELECT (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at), INSERT (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at), UPDATE (protected_response) ON app.identity_idempotency_receipts TO gds_identity_admin;
GRANT SELECT (key_id, state, changed_at) ON app.identity_rate_limit_key_state TO gds_identity_admin;
GRANT EXECUTE ON FUNCTION app.lock_identity_rate_limit_key_state() TO gds_identity_admin;
DO $identity_lock_verify$
DECLARE
    v_function oid := 'app.lock_identity_rate_limit_key_state()'::pg_catalog.regprocedure;
    v_owner oid := (SELECT relowner FROM pg_catalog.pg_class
                    WHERE oid = 'app.identity_rate_limit_key_state'::pg_catalog.regclass);
    v_identity oid := (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin');
BEGIN
    IF (SELECT proowner FROM pg_catalog.pg_proc WHERE oid = v_function) <> v_owner
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'USAGE')
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'CREATE')
       OR (NOT pg_catalog.has_table_privilege(v_owner, 'app.identity_rate_limit_key_state', 'SELECT')
           OR NOT pg_catalog.has_table_privilege(v_owner, 'app.identity_rate_limit_key_state', 'UPDATE'))
       OR NOT pg_catalog.has_function_privilege(v_identity, v_function, 'EXECUTE')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p
                  CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                      p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                  WHERE p.oid = v_function AND acl.privilege_type = 'EXECUTE'
                    AND acl.grantee NOT IN (v_owner, v_identity)) THEN
        RAISE EXCEPTION 'identity_lock_owner_or_acl_invalid';
    END IF;
END;
$identity_lock_verify$;

GRANT EXECUTE ON FUNCTION app.lock_identity_user_roles(uuid) TO gds_identity_admin;
DO $identity_membership_lock_verify$
DECLARE
    v_function oid := 'app.lock_identity_user_roles(uuid)'::pg_catalog.regprocedure;
    v_owner oid := (SELECT relowner FROM pg_catalog.pg_class
                    WHERE oid = 'public."AspNetUserRoles"'::pg_catalog.regclass);
    v_identity oid := (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin');
BEGIN
    IF (SELECT proowner FROM pg_catalog.pg_proc WHERE oid = v_function) <> v_owner
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'USAGE')
       OR NOT pg_catalog.has_schema_privilege(v_owner, 'app', 'CREATE')
       OR (NOT pg_catalog.has_table_privilege(v_owner, 'public."AspNetUserRoles"', 'SELECT')
           OR NOT pg_catalog.has_table_privilege(v_owner, 'public."AspNetUserRoles"', 'UPDATE'))
       OR NOT pg_catalog.has_function_privilege(v_identity, v_function, 'EXECUTE')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p
                  CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
                      p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
                  WHERE p.oid = v_function AND acl.privilege_type = 'EXECUTE'
                    AND acl.grantee NOT IN (v_owner, v_identity)) THEN
        RAISE EXCEPTION 'identity_membership_lock_owner_or_acl_invalid';
    END IF;
END;
$identity_membership_lock_verify$;


GRANT SELECT (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at), INSERT (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at), UPDATE (attempt_count, expires_at), DELETE ON app.identity_rate_limits TO gds_identity_admin;

-- Extension functions are identified by extension dependency, not name.
-- PostgreSQL catalog built-ins are a separately reported platform baseline.
DO $identity_public_extensions$
DECLARE
    v_function regprocedure;
BEGIN
    IF pg_catalog.to_regprocedure('public.gen_random_uuid()') IS NULL THEN
        RAISE EXCEPTION 'required public.gen_random_uuid() extension function is missing';
    END IF;
    FOR v_function IN
        SELECT p.oid::pg_catalog.regprocedure
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_depend dependency ON dependency.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
            AND dependency.objid = p.oid AND dependency.deptype = 'e'
        JOIN pg_catalog.pg_extension extension_object ON extension_object.oid = dependency.refobjid
        WHERE p.pronamespace <> 'pg_catalog'::pg_catalog.regnamespace
          AND p.oid <> 'public.gen_random_uuid()'::pg_catalog.regprocedure
          AND EXISTS (
              SELECT 1
              FROM pg_catalog.aclexplode(COALESCE(
                  p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
              WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'
          )
    LOOP
        EXECUTE pg_catalog.format('REVOKE EXECUTE ON FUNCTION %s FROM PUBLIC', v_function);
    END LOOP;
END;
$identity_public_extensions$;
GRANT EXECUTE ON FUNCTION public.gen_random_uuid() TO PUBLIC;

-- Fail closed before COMMIT if role attributes, membership or the required
-- grants differ. Direct grants were reset above, so these positive and
-- negative checks establish the managed effective surface deterministically.
DO $identity_admin_acl_check$
DECLARE
    v_role pg_catalog.pg_roles%ROWTYPE;
BEGIN
    SELECT * INTO STRICT v_role FROM pg_catalog.pg_roles WHERE rolname = 'gds_identity_admin';
    IF v_role.rolcanlogin OR v_role.rolinherit OR v_role.rolsuper OR v_role.rolcreatedb
       OR v_role.rolcreaterole OR v_role.rolreplication OR v_role.rolbypassrls THEN
        RAISE EXCEPTION 'identity_admin_unexpected_role_attribute';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_auth_members membership
        JOIN pg_catalog.pg_roles member ON member.oid = membership.member
        WHERE member.rolname = 'gds_identity_admin'
    ) THEN
        RAISE EXCEPTION 'identity_admin_unexpected_membership';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_database database_object
        WHERE database_object.datname = pg_catalog.current_database()
          AND database_object.datdba = v_role.oid
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_namespace n
        WHERE n.nspname IN ('public', 'app') AND n.nspowner = v_role.oid
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('public', 'app') AND c.relowner = v_role.oid
        UNION ALL
        SELECT 1 FROM pg_catalog.pg_proc p
        WHERE p.pronamespace IN ('public'::pg_catalog.regnamespace, 'app'::pg_catalog.regnamespace)
          AND p.proowner = v_role.oid
    ) THEN
        RAISE EXCEPTION 'identity_admin_unexpected_owned_object';
    END IF;
    IF NOT pg_catalog.has_schema_privilege('gds_identity_admin', 'public', 'USAGE')
       OR NOT pg_catalog.has_schema_privilege('gds_identity_admin', 'app', 'USAGE')
       OR pg_catalog.has_schema_privilege('gds_identity_admin', 'public', 'CREATE')
       OR pg_catalog.has_schema_privilege('gds_identity_admin', 'app', 'CREATE') THEN
        RAISE EXCEPTION 'identity_admin_unexpected_effective_privilege';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('public', 'app')
          AND CASE
              WHEN c.relkind = 'S' THEN
                  pg_catalog.has_sequence_privilege('gds_identity_admin', c.oid, 'USAGE,SELECT,UPDATE')
              ELSE FALSE
          END
    ) THEN
        RAISE EXCEPTION 'identity_admin_unexpected_effective_privilege';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_proc p
        WHERE p.pronamespace = 'app'::pg_catalog.regnamespace
          AND p.oid <> 'app.lock_identity_rate_limit_key_state()'::pg_catalog.regprocedure
          AND p.oid <> 'app.lock_identity_user_roles(uuid)'::pg_catalog.regprocedure
          AND EXISTS (
              SELECT 1 FROM pg_catalog.aclexplode(p.proacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
              WHERE grantee.rolname = 'gds_identity_admin' AND acl.privilege_type = 'EXECUTE'
          )
    ) THEN
        RAISE EXCEPTION 'identity_admin_unexpected_effective_privilege';
    END IF;
    IF pg_catalog.has_function_privilege('gds_identity_admin', 'app.enforce_identity_user_role_cardinality()', 'EXECUTE')
       OR pg_catalog.has_function_privilege('gds_identity_admin', 'app.enforce_identity_role_catalog()', 'EXECUTE')
       OR pg_catalog.has_function_privilege('gds_identity_admin', 'app.guard_identity_admin_mutation()', 'EXECUTE')
       OR pg_catalog.has_function_privilege('gds_identity_admin', 'app.reject_immutable_change()', 'EXECUTE') THEN
        RAISE EXCEPTION 'identity_admin_unexpected_effective_privilege';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_depend dependency ON dependency.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
            AND dependency.objid = p.oid AND dependency.deptype = 'e'
        WHERE p.pronamespace <> 'pg_catalog'::pg_catalog.regnamespace
          AND p.oid <> 'public.gen_random_uuid()'::pg_catalog.regprocedure
          AND EXISTS (
              SELECT 1 FROM pg_catalog.aclexplode(COALESCE(
                  p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
              WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'
          )
    ) THEN
        RAISE EXCEPTION 'unexpected_public_extension_execute';
    END IF;
END;
$identity_admin_acl_check$;

-- Reapplication must validate existing data too; trigger creation enqueues no rows.
DO $identity_role_structure$
BEGIN
    IF (SELECT count(*) FROM public."AspNetRoles") <> 3 OR EXISTS (
        SELECT 1 FROM (VALUES ('operador', 'OPERADOR'), ('validador', 'VALIDADOR'),
                             ('administrador', 'ADMINISTRADOR')) AS expected(name, normalized_name)
        LEFT JOIN public."AspNetRoles" r ON r."Name" = expected.name
            AND r."NormalizedName" = expected.normalized_name
        GROUP BY expected.name HAVING count(r."Id") <> 1
    ) OR EXISTS (
        SELECT u."Id" FROM public."AspNetUsers" u
        LEFT JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        GROUP BY u."Id" HAVING count(ur."RoleId") <> 1
    ) THEN
        RAISE EXCEPTION 'identity role invariants are not satisfied';
    END IF;
    IF EXISTS (SELECT 1 FROM public."AspNetUsers") AND NOT EXISTS (
        SELECT 1 FROM public."AspNetUsers" u
        JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
        JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
        WHERE u."IsActive" AND r."Name" = 'administrador'
          AND r."NormalizedName" = 'ADMINISTRADOR'
    ) THEN
        RAISE EXCEPTION 'identity_last_active_admin';
    END IF;
END;
$identity_role_structure$;

COMMIT;
-- Machine-readable, read-only identity reconciliation. Every problem query
-- returns zero rows; the runner captures the complete bounded section.
SELECT 'identity_reconciliation_begin' AS report;

SELECT 'identity_problem:role_catalog' AS report, drift.detail
FROM (
    SELECT expected.name || '/' || expected.normalized_name AS detail
    FROM (VALUES ('operador', 'OPERADOR'), ('validador', 'VALIDADOR'),
                 ('administrador', 'ADMINISTRADOR')) expected(name, normalized_name)
    LEFT JOIN public."AspNetRoles" r ON r."Name" = expected.name
        AND r."NormalizedName" = expected.normalized_name
    GROUP BY expected.name, expected.normalized_name HAVING count(r."Id") <> 1
    UNION ALL
    SELECT COALESCE(r."Name", '<null>') || '/' || COALESCE(r."NormalizedName", '<null>')
    FROM public."AspNetRoles" r
    WHERE (r."Name", r."NormalizedName") NOT IN
        (('operador', 'OPERADOR'), ('validador', 'VALIDADOR'), ('administrador', 'ADMINISTRADOR'))
       OR r."Name" IS NULL OR r."NormalizedName" IS NULL
) drift;

SELECT 'identity_problem:user_role_cardinality' AS report, u."Id"::text AS detail
FROM public."AspNetUsers" u
LEFT JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
LEFT JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
GROUP BY u."Id"
HAVING count(ur."RoleId") <> 1
    OR count(*) FILTER (WHERE (r."Name", r."NormalizedName") IN
        (('operador', 'OPERADOR'), ('validador', 'VALIDADOR'),
         ('administrador', 'ADMINISTRADOR'))) <> 1;

SELECT 'identity_problem:active_administrator' AS report, 'missing' AS detail
WHERE EXISTS (SELECT 1 FROM public."AspNetUsers") AND NOT EXISTS (
    SELECT 1 FROM public."AspNetUsers" u
    JOIN public."AspNetUserRoles" ur ON ur."UserId" = u."Id"
    JOIN public."AspNetRoles" r ON r."Id" = ur."RoleId"
    WHERE u."IsActive" AND r."Name" = 'administrador'
      AND r."NormalizedName" = 'ADMINISTRADOR'
);

SELECT 'identity_problem:active_rate_key' AS report, count(*)::text AS detail
FROM app.identity_rate_limit_key_state WHERE state = 'active'
HAVING count(*) <> 1;

SELECT 'identity_problem:identity_columns' AS report, expected.column_name AS detail
FROM (VALUES ('MustChangePassword', 'boolean', 'NO', 'false'),
             ('PasswordChangedAt', 'timestamp with time zone', 'YES', NULL),
             ('ManagementVersion', 'bigint', 'NO', '1'))
    expected(column_name, data_type, is_nullable, column_default)
LEFT JOIN information_schema.columns c
    ON c.table_schema = 'public' AND c.table_name = 'AspNetUsers'
   AND c.column_name = expected.column_name
WHERE c.column_name IS NULL OR c.data_type <> expected.data_type
   OR c.is_nullable <> expected.is_nullable
   OR c.column_default IS DISTINCT FROM expected.column_default;

SELECT 'identity_problem:identity_tables' AS report, expected.table_name AS detail
FROM (VALUES ('identity_idempotency_receipts'), ('identity_rate_limit_key_state'),
             ('identity_rate_limits')) expected(table_name)
WHERE pg_catalog.to_regclass('app.' || expected.table_name) IS NULL;

SELECT 'identity_problem:role_attributes' AS report, r.rolname AS detail
FROM pg_catalog.pg_roles r
WHERE r.rolname = 'gds_identity_admin'
  AND (r.rolcanlogin OR r.rolinherit OR r.rolsuper OR r.rolcreatedb
       OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls);

SELECT 'identity_problem:memberships' AS report, parent.rolname AS detail
FROM pg_catalog.pg_auth_members membership
JOIN pg_catalog.pg_roles parent ON parent.oid = membership.roleid
JOIN pg_catalog.pg_roles member ON member.oid = membership.member
WHERE member.rolname = 'gds_identity_admin';

SELECT 'identity_problem:owned_objects' AS report,
       owned.object_type || ':' || owned.object_name AS detail
FROM (
    SELECT 'database'::text AS object_type, database_object.datname::text AS object_name,
           database_object.datdba AS owner_oid
    FROM pg_catalog.pg_database database_object
    WHERE database_object.datname = pg_catalog.current_database()
    UNION ALL
    SELECT 'schema', n.nspname, n.nspowner FROM pg_catalog.pg_namespace n
    WHERE n.nspname IN ('public', 'app')
    UNION ALL
    SELECT 'relation', n.nspname || '.' || c.relname, c.relowner
    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname IN ('public', 'app')
    UNION ALL
    SELECT 'routine', p.oid::pg_catalog.regprocedure::text, p.proowner
    FROM pg_catalog.pg_proc p
    WHERE p.pronamespace IN ('public'::pg_catalog.regnamespace, 'app'::pg_catalog.regnamespace)
) owned
JOIN pg_catalog.pg_roles r ON r.oid = owned.owner_oid
WHERE r.rolname = 'gds_identity_admin';

-- Exact direct schema, table and column ACL comparison.
SELECT 'identity_problem:managed_acl' AS report,
       drift.disposition || ':' || drift.object_type || ':' || drift.object_name
           || ':' || drift.privilege_type AS detail
FROM (
    SELECT CASE WHEN expected.schema_name IS NULL THEN 'unexpected' ELSE 'missing' END AS disposition,
           'schema'::text AS object_type,
           COALESCE(expected.schema_name, actual.schema_name) AS object_name,
           COALESCE(expected.privilege_type, actual.privilege_type) AS privilege_type
    FROM (VALUES ('public', 'USAGE'), ('app', 'USAGE')) expected(schema_name, privilege_type)
    FULL JOIN (
        SELECT n.nspname AS schema_name, acl.privilege_type
        FROM pg_catalog.pg_namespace n
        JOIN pg_catalog.pg_roles role_object ON role_object.rolname = 'gds_identity_admin'
        CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
            n.nspacl, pg_catalog.acldefault('n', n.nspowner))) acl
        WHERE n.nspname IN ('public', 'app') AND acl.grantee = role_object.oid
    ) actual USING (schema_name, privilege_type)
    WHERE expected.schema_name IS NULL OR actual.schema_name IS NULL
    UNION ALL
    SELECT CASE WHEN expected.table_schema IS NULL THEN 'unexpected' ELSE 'missing' END,
           'table', COALESCE(expected.table_schema, actual.table_schema) || '.'
               || COALESCE(expected.table_name, actual.table_name),
           COALESCE(expected.privilege_type, actual.privilege_type)
    FROM (VALUES ('public', 'AspNetUserRoles', 'DELETE'),
                 ('app', 'identity_rate_limits', 'DELETE'))
         expected(table_schema, table_name, privilege_type)
    FULL JOIN (
        SELECT table_schema, table_name, privilege_type
        FROM information_schema.table_privileges
        WHERE grantee = 'gds_identity_admin' AND table_schema IN ('public', 'app')
    ) actual USING (table_schema, table_name, privilege_type)
    WHERE expected.table_schema IS NULL OR actual.table_schema IS NULL
    UNION ALL
    SELECT CASE WHEN expected.table_schema IS NULL THEN 'unexpected' ELSE 'missing' END,
           'column', COALESCE(expected.table_schema, actual.table_schema) || '.'
               || COALESCE(expected.table_name, actual.table_name) || '.'
               || COALESCE(expected.column_name, actual.column_name),
           COALESCE(expected.privilege_type, actual.privilege_type)
    FROM (
        SELECT specification.table_schema, specification.table_name,
               column_name, specification.privilege_type
        FROM (VALUES
            ('public', 'AspNetUsers', 'SELECT', ARRAY['Id','UserName','NormalizedUserName','DisplayName','PasswordHash','SecurityStamp','ConcurrencyStamp','IsActive','CreatedAt','UpdatedAt','MustChangePassword','PasswordChangedAt','ManagementVersion']::text[]),
            ('public', 'AspNetUsers', 'INSERT', ARRAY['Id','UserName','NormalizedUserName','PasswordHash','SecurityStamp','ConcurrencyStamp','DisplayName','IsActive','CreatedAt','UpdatedAt','MustChangePassword','PasswordChangedAt','ManagementVersion']::text[]),
            ('public', 'AspNetUsers', 'UPDATE', ARRAY['PasswordHash','SecurityStamp','ConcurrencyStamp','IsActive','UpdatedAt','MustChangePassword','PasswordChangedAt','ManagementVersion']::text[]),
            ('public', 'AspNetRoles', 'SELECT', ARRAY['Id','Name','NormalizedName']::text[]),
            ('public', 'AspNetUserRoles', 'SELECT', ARRAY['UserId','RoleId']::text[]),
            ('public', 'AspNetUserRoles', 'INSERT', ARRAY['UserId','RoleId']::text[]),
            ('app', 'audit_events', 'INSERT', ARRAY['actor_user_id','action','outcome','entity_type','entity_id','correlation_id','source_ip_hash','changes','security_context']::text[]),
            ('app', 'identity_idempotency_receipts', 'SELECT', ARRAY['key_sha256','actor_user_id','payload_sha256','user_id','protected_response','created_at','expires_at']::text[]),
            ('app', 'identity_idempotency_receipts', 'INSERT', ARRAY['key_sha256','actor_user_id','payload_sha256','user_id','protected_response','created_at','expires_at']::text[]),
            ('app', 'identity_idempotency_receipts', 'UPDATE', ARRAY['protected_response']::text[]),
            ('app', 'identity_rate_limit_key_state', 'SELECT', ARRAY['key_id','state','changed_at']::text[]),
            ('app', 'identity_rate_limits', 'SELECT', ARRAY['key_id','bucket_hmac','window_started_at','window_ends_at','attempt_count','expires_at']::text[]),
            ('app', 'identity_rate_limits', 'INSERT', ARRAY['key_id','bucket_hmac','window_started_at','window_ends_at','attempt_count','expires_at']::text[]),
            ('app', 'identity_rate_limits', 'UPDATE', ARRAY['attempt_count','expires_at']::text[])
        ) specification(table_schema, table_name, privilege_type, column_names)
        CROSS JOIN LATERAL pg_catalog.unnest(specification.column_names) column_name
    ) expected
    FULL JOIN (
        SELECT table_schema, table_name, column_name, privilege_type
        FROM information_schema.column_privileges
        WHERE grantee = 'gds_identity_admin' AND table_schema IN ('public', 'app')
    ) actual USING (table_schema, table_name, column_name, privilege_type)
    WHERE expected.table_schema IS NULL OR actual.table_schema IS NULL
    UNION ALL
    SELECT 'unexpected', 'sequence', n.nspname || '.' || c.relname, acl.privilege_type
    FROM pg_catalog.pg_class c
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    JOIN pg_catalog.pg_roles role_object ON role_object.rolname = 'gds_identity_admin'
    CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
        c.relacl, pg_catalog.acldefault('S', c.relowner))) acl
    WHERE n.nspname IN ('public', 'app') AND c.relkind = 'S'
      AND acl.grantee = role_object.oid
    UNION ALL
    SELECT 'unexpected', 'routine', p.oid::pg_catalog.regprocedure::text, acl.privilege_type
    FROM pg_catalog.pg_proc p
    JOIN pg_catalog.pg_roles role_object ON role_object.rolname = 'gds_identity_admin'
    CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
    WHERE p.pronamespace IN ('public'::pg_catalog.regnamespace, 'app'::pg_catalog.regnamespace)
      AND acl.grantee = role_object.oid
      AND p.oid <> 'app.lock_identity_rate_limit_key_state()'::pg_catalog.regprocedure
          AND p.oid <> 'app.lock_identity_user_roles(uuid)'::pg_catalog.regprocedure
) drift;

SELECT 'identity_problem:function_metadata' AS report,
       COALESCE(expected.signature, actual.signature) AS detail
FROM (VALUES
    ('app.enforce_identity_user_role_cardinality()', false, ARRAY['search_path=pg_catalog']::text[], false, false),
    ('app.enforce_identity_role_catalog()', false, ARRAY['search_path=pg_catalog']::text[], false, false),
    ('app.guard_identity_admin_mutation()', false, ARRAY['search_path=pg_catalog']::text[], false, false),
    ('app.reject_immutable_change()', false, ARRAY['search_path=pg_catalog']::text[], false, false),
    ('app.lock_identity_rate_limit_key_state()', true, ARRAY['search_path=pg_catalog']::text[], false, true),
    ('app.lock_identity_user_roles(p_user uuid)', true, ARRAY['search_path=pg_catalog']::text[], false, true)
) expected(signature, prosecdef, proconfig, public_execute, identity_execute)
FULL JOIN (
    SELECT pg_catalog.format('%I.%I(%s)', n.nspname, p.proname,
               pg_catalog.pg_get_function_identity_arguments(p.oid)) AS signature,
           p.prosecdef, p.proconfig,
           EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(
               p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
               WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE') AS public_execute,
           pg_catalog.has_function_privilege('gds_identity_admin', p.oid, 'EXECUTE') AS identity_execute
    FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
    WHERE p.oid IN ('app.enforce_identity_user_role_cardinality()'::pg_catalog.regprocedure,
                    'app.enforce_identity_role_catalog()'::pg_catalog.regprocedure,
                    'app.guard_identity_admin_mutation()'::pg_catalog.regprocedure,
                    'app.reject_immutable_change()'::pg_catalog.regprocedure,
                    'app.lock_identity_rate_limit_key_state()'::pg_catalog.regprocedure,
                    'app.lock_identity_user_roles(uuid)'::pg_catalog.regprocedure)
) actual USING (signature, prosecdef, proconfig, public_execute, identity_execute)
WHERE expected.signature IS NULL OR actual.signature IS NULL;

WITH expected(
    relation_schema, relation_name, constraint_name, constraint_type,
    normalized_definition, condeferrable, condeferred, convalidated
) AS (VALUES
    ('public', 'AspNetUsers', 'AspNetUsers_ManagementVersion_check', 'c',
        'CHECK|"ManagementVersion" > 0', false, false, true),
    ('public', 'AspNetUserRoles', 'AspNetUserRoles_UserId_fkey', 'f',
        'FOREIGN KEY|UserId|REFERENCES|public.AspNetUsers|Id|MATCH:SIMPLE|UPDATE:NO ACTION|DELETE:CASCADE', true, true, true),
    ('app', 'identity_idempotency_receipts', 'identity_idempotency_receipts_pkey', 'p',
        'PRIMARY KEY|key_sha256', false, false, true),
    ('app', 'identity_idempotency_receipts', 'identity_idempotency_receipts_key_sha256_check', 'c',
        'CHECK|octet_length(key_sha256) = 32', false, false, true),
    ('app', 'identity_idempotency_receipts', 'identity_idempotency_receipts_actor_user_id_fkey', 'f',
        'FOREIGN KEY|actor_user_id|REFERENCES|public.AspNetUsers|Id|MATCH:SIMPLE|UPDATE:NO ACTION|DELETE:RESTRICT', false, false, true),
    ('app', 'identity_idempotency_receipts', 'identity_idempotency_receipts_payload_sha256_check', 'c',
        'CHECK|octet_length(payload_sha256) = 32', false, false, true),
    ('app', 'identity_idempotency_receipts', 'identity_idempotency_receipts_user_id_fkey', 'f',
        'FOREIGN KEY|user_id|REFERENCES|public.AspNetUsers|Id|MATCH:SIMPLE|UPDATE:NO ACTION|DELETE:RESTRICT', false, false, true),
    ('app', 'identity_idempotency_receipts', 'ck_identity_receipt_expiry', 'c',
        'CHECK|expires_at > created_at', false, false, true),
    ('app', 'identity_rate_limit_key_state', 'identity_rate_limit_key_state_pkey', 'p',
        'PRIMARY KEY|key_id', false, false, true),
    ('app', 'identity_rate_limit_key_state', 'identity_rate_limit_key_state_key_id_check', 'c',
        'CHECK|key_id::text ~ ''^[a-z0-9][a-z0-9._-]{0,63}$''::text', false, false, true),
    ('app', 'identity_rate_limit_key_state', 'identity_rate_limit_key_state_state_check', 'c',
        'CHECK|state = ANY (ARRAY[''accepted''::text, ''active''::text, ''retained''::text, ''retired''::text])', false, false, true),
    ('app', 'identity_rate_limits', 'identity_rate_limits_pkey', 'p',
        'PRIMARY KEY|key_id,bucket_hmac,window_started_at', false, false, true),
    ('app', 'identity_rate_limits', 'identity_rate_limits_key_id_fkey', 'f',
        'FOREIGN KEY|key_id|REFERENCES|app.identity_rate_limit_key_state|key_id|MATCH:SIMPLE|UPDATE:NO ACTION|DELETE:RESTRICT', false, false, true),
    ('app', 'identity_rate_limits', 'identity_rate_limits_key_id_check', 'c',
        'CHECK|key_id::text ~ ''^[a-z0-9][a-z0-9._-]{0,63}$''::text', false, false, true),
    ('app', 'identity_rate_limits', 'identity_rate_limits_bucket_hmac_check', 'c',
        'CHECK|octet_length(bucket_hmac) = 32', false, false, true),
    ('app', 'identity_rate_limits', 'identity_rate_limits_attempt_count_check', 'c',
        'CHECK|attempt_count >= 0', false, false, true),
    ('app', 'identity_rate_limits', 'ck_identity_rate_limit_window', 'c',
        'CHECK|window_ends_at > window_started_at', false, false, true),
    ('app', 'identity_rate_limits', 'ck_identity_rate_limit_expiry', 'c',
        'CHECK|expires_at >= window_ends_at', false, false, true)
),
actual AS (
    SELECT relation_namespace.nspname::text AS relation_schema,
           relation.relname::text AS relation_name,
           constraint_object.conname::text AS constraint_name,
           constraint_object.contype::text AS constraint_type,
           CASE constraint_object.contype
               WHEN 'p' THEN 'PRIMARY KEY|' || pg_catalog.array_to_string(source_columns.names, ',')
               WHEN 'f' THEN 'FOREIGN KEY|' || pg_catalog.array_to_string(source_columns.names, ',')
                    || '|REFERENCES|' || target_namespace.nspname || '.' || target_relation.relname
                    || '|' || pg_catalog.array_to_string(target_columns.names, ',')
                    || '|MATCH:' || CASE constraint_object.confmatchtype
                        WHEN 'f' THEN 'FULL' WHEN 'p' THEN 'PARTIAL' ELSE 'SIMPLE' END
                    || '|UPDATE:' || CASE constraint_object.confupdtype
                        WHEN 'r' THEN 'RESTRICT' WHEN 'c' THEN 'CASCADE'
                        WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' ELSE 'NO ACTION' END
                    || '|DELETE:' || CASE constraint_object.confdeltype
                        WHEN 'r' THEN 'RESTRICT' WHEN 'c' THEN 'CASCADE'
                        WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' ELSE 'NO ACTION' END
               WHEN 'c' THEN 'CHECK|' || pg_catalog.regexp_replace(
                    pg_catalog.pg_get_expr(constraint_object.conbin, constraint_object.conrelid, true),
                    '[[:space:]]+', ' ', 'g')
           END AS normalized_definition,
           constraint_object.condeferrable,
           constraint_object.condeferred,
           constraint_object.convalidated
    FROM pg_catalog.pg_constraint constraint_object
    JOIN pg_catalog.pg_class relation ON relation.oid = constraint_object.conrelid
    JOIN pg_catalog.pg_namespace relation_namespace ON relation_namespace.oid = relation.relnamespace
    LEFT JOIN pg_catalog.pg_class target_relation ON target_relation.oid = constraint_object.confrelid
    LEFT JOIN pg_catalog.pg_namespace target_namespace ON target_namespace.oid = target_relation.relnamespace
    LEFT JOIN LATERAL (
        SELECT pg_catalog.array_agg(attribute.attname::text ORDER BY key_column.ordinality) AS names
        FROM pg_catalog.unnest(constraint_object.conkey) WITH ORDINALITY key_column(attnum, ordinality)
        JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_object.conrelid
            AND attribute.attnum = key_column.attnum
    ) source_columns ON true
    LEFT JOIN LATERAL (
        SELECT pg_catalog.array_agg(attribute.attname::text ORDER BY key_column.ordinality) AS names
        FROM pg_catalog.unnest(constraint_object.confkey) WITH ORDINALITY key_column(attnum, ordinality)
        JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid = constraint_object.confrelid
            AND attribute.attnum = key_column.attnum
    ) target_columns ON true
    WHERE (relation_namespace.nspname = 'app'
           AND relation.relname IN ('identity_idempotency_receipts',
                                    'identity_rate_limit_key_state', 'identity_rate_limits'))
       OR (relation_namespace.nspname = 'public' AND relation.relname = 'AspNetUsers'
           AND constraint_object.conname = 'AspNetUsers_ManagementVersion_check')
       OR (relation_namespace.nspname = 'public' AND relation.relname = 'AspNetUserRoles'
           AND constraint_object.conname = 'AspNetUserRoles_UserId_fkey')
),
drift AS (
    SELECT CASE WHEN expected.constraint_name IS NULL THEN 'unexpected'
                WHEN actual.constraint_name IS NULL THEN 'missing' ELSE 'mismatched' END AS disposition,
           COALESCE(expected.relation_schema, actual.relation_schema) AS relation_schema,
           COALESCE(expected.relation_name, actual.relation_name) AS relation_name,
           COALESCE(expected.constraint_name, actual.constraint_name) AS constraint_name
    FROM expected
    FULL JOIN actual USING (relation_schema, relation_name, constraint_name)
    WHERE expected.constraint_name IS NULL OR actual.constraint_name IS NULL
       OR actual.constraint_type IS DISTINCT FROM expected.constraint_type
       OR actual.normalized_definition IS DISTINCT FROM expected.normalized_definition
       OR actual.condeferrable IS DISTINCT FROM expected.condeferrable
       OR actual.condeferred IS DISTINCT FROM expected.condeferred
       OR actual.convalidated IS DISTINCT FROM expected.convalidated
)
SELECT 'identity_problem:constraint_metadata' AS report,
       disposition || ':' || pg_catalog.format('%I.%I', relation_schema, relation_name)
           || ':' || constraint_name AS detail
FROM drift;

WITH expected(
    relation_schema, relation_name, trigger_name, function_signature,
    is_before, on_insert, on_update, on_delete, on_truncate, for_each_row,
    is_constraint, tgdeferrable, tginitdeferred, tgenabled
) AS (VALUES
    ('app', 'identity_rate_limit_key_state', 'trg_identity_rate_limit_active_key',
        'app.assert_identity_rate_limit_active_key()', false, true, true, true, false, true, true, true, true, 'O'::"char"),
    ('public', 'AspNetUsers', 'trg_identity_user_role_cardinality',
        'app.enforce_identity_user_role_cardinality()', false, true, true, false, false, true, true, true, true, 'O'::"char"),
    ('public', 'AspNetUserRoles', 'trg_identity_membership_cardinality',
        'app.enforce_identity_user_role_cardinality()', false, true, true, true, false, true, true, true, true, 'O'::"char"),
    ('public', 'AspNetRoles', 'trg_identity_role_catalog',
        'app.enforce_identity_role_catalog()', false, true, true, true, false, true, true, true, true, 'O'::"char"),
    ('public', 'AspNetUsers', 'trg_identity_users_admin_guard',
        'app.guard_identity_admin_mutation()', true, true, true, true, false, true, false, false, false, 'O'::"char"),
    ('public', 'AspNetUserRoles', 'trg_identity_memberships_admin_guard',
        'app.guard_identity_admin_mutation()', true, true, true, true, false, true, false, false, false, 'O'::"char"),
    ('public', 'AspNetUsers', 'trg_identity_users_admin_final',
        'app.guard_identity_admin_mutation()', false, true, true, true, false, true, true, true, true, 'O'::"char"),
    ('public', 'AspNetUserRoles', 'trg_identity_memberships_admin_final',
        'app.guard_identity_admin_mutation()', false, true, true, true, false, true, true, true, true, 'O'::"char")
),
actual AS (
    SELECT relation_namespace.nspname::text AS relation_schema,
           relation.relname::text AS relation_name,
           pg_catalog.format('%I.%I', relation_namespace.nspname, relation.relname)
               AS relation_signature,
           trigger_object.tgname::text AS trigger_name,
           pg_catalog.format('%I.%I(%s)', function_namespace.nspname, routine.proname,
               pg_catalog.pg_get_function_identity_arguments(routine.oid)) AS function_signature,
           (trigger_object.tgtype & 2) <> 0 AS is_before,
           (trigger_object.tgtype & 4) <> 0 AS on_insert,
           (trigger_object.tgtype & 16) <> 0 AS on_update,
           (trigger_object.tgtype & 8) <> 0 AS on_delete,
           (trigger_object.tgtype & 32) <> 0 AS on_truncate,
           (trigger_object.tgtype & 1) <> 0 AS for_each_row,
           trigger_object.tgconstraint <> 0 AS is_constraint,
           trigger_object.tgdeferrable,
           trigger_object.tginitdeferred,
           trigger_object.tgenabled
    FROM pg_catalog.pg_trigger trigger_object
    JOIN pg_catalog.pg_class relation ON relation.oid = trigger_object.tgrelid
    JOIN pg_catalog.pg_namespace relation_namespace ON relation_namespace.oid = relation.relnamespace
    JOIN pg_catalog.pg_proc routine ON routine.oid = trigger_object.tgfoid
    JOIN pg_catalog.pg_namespace function_namespace ON function_namespace.oid = routine.pronamespace
    WHERE NOT trigger_object.tgisinternal
      AND trigger_object.tgname LIKE 'trg_identity_%'
),
drift AS (
    SELECT CASE WHEN expected.trigger_name IS NULL THEN 'unexpected'
                WHEN actual.trigger_name IS NULL THEN 'missing' ELSE 'mismatched' END AS disposition,
           COALESCE(expected.relation_schema, actual.relation_schema) AS relation_schema,
           COALESCE(expected.relation_name, actual.relation_name) AS relation_name,
           COALESCE(expected.trigger_name, actual.trigger_name) AS trigger_name
    FROM expected
    FULL JOIN actual USING (relation_schema, relation_name, trigger_name)
    WHERE expected.trigger_name IS NULL OR actual.trigger_name IS NULL
       OR actual.relation_signature IS DISTINCT FROM
          pg_catalog.format('%I.%I', expected.relation_schema, expected.relation_name)
       OR actual.function_signature IS DISTINCT FROM expected.function_signature
       OR actual.is_before IS DISTINCT FROM expected.is_before
       OR actual.on_insert IS DISTINCT FROM expected.on_insert
       OR actual.on_update IS DISTINCT FROM expected.on_update
       OR actual.on_delete IS DISTINCT FROM expected.on_delete
       OR actual.on_truncate IS DISTINCT FROM expected.on_truncate
       OR actual.for_each_row IS DISTINCT FROM expected.for_each_row
       OR actual.is_constraint IS DISTINCT FROM expected.is_constraint
       OR actual.tgdeferrable IS DISTINCT FROM expected.tgdeferrable
       OR actual.tginitdeferred IS DISTINCT FROM expected.tginitdeferred
       OR actual.tgenabled IS DISTINCT FROM expected.tgenabled
)
SELECT 'identity_problem:trigger_metadata' AS report,
       disposition || ':' || pg_catalog.format('%I.%I', relation_schema, relation_name)
           || ':' || trigger_name AS detail
FROM drift;


SELECT 'identity_problem:public_extension_execute' AS report,
       p.oid::pg_catalog.regprocedure::text AS detail
FROM pg_catalog.pg_proc p
JOIN pg_catalog.pg_depend dependency ON dependency.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
    AND dependency.objid = p.oid AND dependency.deptype = 'e'
WHERE p.pronamespace <> 'pg_catalog'::pg_catalog.regnamespace
  AND p.oid <> 'public.gen_random_uuid()'::pg_catalog.regprocedure
  AND EXISTS (SELECT 1 FROM pg_catalog.aclexplode(COALESCE(
      p.proacl, pg_catalog.acldefault('f', p.proowner))) acl
      WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE');

SELECT 'identity_baseline:platform_public_database' AS report,
       pg_catalog.bool_or(acl.privilege_type = 'CONNECT')::text AS public_connect,
       pg_catalog.bool_or(acl.privilege_type = 'TEMPORARY')::text AS public_temporary
FROM pg_catalog.pg_database database_object
CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(
    database_object.datacl, pg_catalog.acldefault('d', database_object.datdba))) acl
WHERE database_object.datname = pg_catalog.current_database() AND acl.grantee = 0;

SELECT 'identity_baseline:platform_pg_catalog_execute' AS report, count(*)::text AS detail
FROM pg_catalog.pg_proc p
WHERE p.pronamespace = 'pg_catalog'::pg_catalog.regnamespace
  AND pg_catalog.has_function_privilege('gds_identity_admin', p.oid, 'EXECUTE');

SELECT 'identity_baseline:runtime_privilege' AS report, r.rolname, r.rolinherit::text, r.rolcanlogin::text
FROM pg_catalog.pg_roles r WHERE r.rolname = 'gds_app_runtime';

SELECT 'identity_reconciliation_manifest' AS report,
       'active_administrator,active_rate_key,constraint_metadata,function_metadata,identity_columns,identity_tables,managed_acl,memberships,owned_objects,public_extension_execute,role_attributes,role_catalog,trigger_metadata,user_role_cardinality' AS problem_reports;

SELECT 'identity_reconciliation_end' AS report;
