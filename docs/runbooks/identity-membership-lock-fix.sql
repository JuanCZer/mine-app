-- Ejecutar conectado a mine_app como propietario de public."AspNetUserRoles"
-- o como administrador con privilegios equivalentes. No contiene secretos.
BEGIN;
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

COMMIT;
