-- Ejecutar conectado a mine_app como propietario de app.identity_rate_limit_key_state
-- o como administrador con privilegios equivalentes. No contiene secretos.
BEGIN;
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

COMMIT;
