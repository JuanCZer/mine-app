"""Stage SQL static contract; PostgreSQL execution belongs to subsequent tasks.

These checks freeze the independently deployable artifacts and the Tasks 1–2
structure/guard contract, without claiming runtime/ACL or complete upgrade parity.
"""
from pathlib import Path
import hashlib
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
DATABASE = ROOT / 'backend/Database'
FIXTURE = ROOT / 'backend-tests/fixtures/pre-user-administration-apply.sql.fixture'
KEY_ID_PATTERN = '^[a-z0-9][a-z0-9._-]{0,63}$'
TABLES = (
    'app.identity_idempotency_receipts',
    'app.identity_rate_limit_key_state',
    'app.identity_rate_limits',
)


def statements(sql: str) -> list[str]:
    """Split top-level SQL, excluding comments, preserving quoted bodies.

    Semicolons and BEGIN inside PL/pgSQL dollar quotes are not transactions.
    """
    result, current = [], []
    pos = 0
    while pos < len(sql):
        if sql.startswith('--', pos):
            end = sql.find('\n', pos)
            pos = len(sql) if end < 0 else end
            current.append(' ')
        elif sql.startswith('/*', pos):
            depth = 1
            pos += 2
            while depth and pos < len(sql):
                if sql.startswith('/*', pos):
                    depth += 1
                    pos += 2
                elif sql.startswith('*/', pos):
                    depth -= 1
                    pos += 2
                else:
                    pos += 1
            if depth:
                raise ValueError('Unclosed SQL comment')
            current.append(' ')
        elif sql[pos] in "'\"":
            quote, start = sql[pos], pos
            pos += 1
            while pos < len(sql):
                if sql[pos] == quote:
                    pos += 1
                    if pos < len(sql) and sql[pos] == quote:
                        pos += 1
                        continue
                    break
                pos += 1
            else:
                raise ValueError('Unclosed SQL quote')
            current.append(sql[start:pos])
        elif sql[pos] == '$' and (tag := re.match(r'\$(?:[A-Za-z_][A-Za-z_0-9]*)?\$', sql[pos:])):
            delimiter = tag.group()
            end = sql.find(delimiter, pos + len(delimiter))
            if end < 0:
                raise ValueError('Unclosed SQL body')
            end += len(delimiter)
            current.append(sql[pos:end])
            pos = end
        elif sql[pos] == ';':
            statement = ''.join(current).strip()
            if statement:
                result.append(statement)
            current = []
            pos += 1
        else:
            current.append(sql[pos])
            pos += 1
    if ''.join(current).strip():
        raise ValueError('SQL must end with a statement terminator')
    return result


def normalized(statement: str) -> str:
    return re.sub(r'\s+', ' ', statement).strip()


class UserAdministrationSqlContractTests(unittest.TestCase):
    def delta(self) -> str:
        path = DATABASE / 'user-administration.sql'
        self.assertTrue(path.is_file(), 'The self-contained upgrade delta is missing')
        return path.read_text()

    def artifacts(self) -> tuple[str, str]:
        return (DATABASE / 'apply.sql').read_text(), self.delta()

    def test_rate_limit_lock_uses_privileged_read_only_function(self) -> None:
        delta = (ROOT / 'docs/runbooks/identity-rate-limit-lock-fix.sql').read_text()
        for sql in (*self.artifacts(), delta):
            with self.subTest(artifact=sql[:30]):
                self.assertIn('LANGUAGE sql VOLATILE SECURITY DEFINER', sql)
                self.assertIn('SET search_path = pg_catalog', sql)
                self.assertIn('FROM app.identity_rate_limit_key_state AS s', sql)
                self.assertIn('FOR SHARE', sql)
                self.assertIn('REVOKE ALL ON FUNCTION app.lock_identity_rate_limit_key_state() FROM PUBLIC', sql)
                self.assertIn('GRANT EXECUTE ON FUNCTION app.lock_identity_rate_limit_key_state() TO gds_identity_admin', sql)
                self.assertIn('identity_lock_existing_owner_or_acl_untrusted', sql)
                self.assertIn('identity_lock_owner_or_acl_invalid', sql)
                self.assertIn("'app.identity_rate_limit_key_state', 'SELECT'", sql)
                self.assertIn("'app.identity_rate_limit_key_state', 'UPDATE'", sql)
                self.assertNotRegex(sql, r'GRANT UPDATE\s*\([^)]*\) ON app\.identity_rate_limit_key_state')
        limiter = (ROOT / 'backend/IdentityAdministration/IdentityRateLimiter.cs').read_text()
        self.assertIn('FROM app.lock_identity_rate_limit_key_state()', limiter)

    def test_database_has_exactly_two_sql_artifacts(self) -> None:
        self.assertEqual(
            {'apply.sql', 'user-administration.sql'},
            {str(path.relative_to(DATABASE)) for path in DATABASE.rglob('*')
             if path.is_file() and path.suffix == '.sql'},
        )

    def test_delta_is_the_only_upgrade_sql_and_is_transactional(self) -> None:
        sql = self.delta()
        parts = statements(sql)
        self.assertEqual(['ROLLBACK', 'BEGIN', 'COMMIT'], [part.upper() for part in parts
                         if re.match(r'(?i)^(BEGIN|START\s+TRANSACTION|COMMIT|END|ROLLBACK|ABORT)\b', part)])
        self.assertEqual(['ROLLBACK', 'BEGIN'], [part.upper() for part in parts[:2]])
        commit = next(i for i, part in enumerate(parts) if part.upper() == 'COMMIT')
        self.assertGreater(commit, 2)
        self.assertGreater(len(parts[commit + 1:]), 0)
        for part in parts[commit + 1:]:
            self.assertRegex(part, r'(?i)^(?:SELECT|WITH)\b')
            self.assertNotRegex(part, r'(?is)^SELECT\b.*?\bINTO\b.*?\bFROM\b')
            self.assertNotRegex(part, r'(?i)\b(setval|nextval)\s*\(')
        self.assertNotRegex(sql, r'(?m)^\s*\\')
        self.assertNotRegex(sql, r'(?i)\b(?:include|apply\.sql)\b')
        # Exercise the scanner with PL/pgSQL, quoted semicolons and comments.
        self.assertEqual(['BEGIN', "DO $x$ BEGIN PERFORM ';'; END $x$", 'COMMIT'],
                         statements("-- BEGIN;\nBEGIN; /* COMMIT; */ DO $x$ BEGIN PERFORM ';'; END $x$; COMMIT;"))

    def test_delta_resets_a_stale_dedicated_session_before_preflight_or_mutation(self) -> None:
        sql = self.delta()
        parts = statements(sql)
        self.assertEqual('ROLLBACK', parts[0].upper())
        self.assertEqual('BEGIN', parts[1].upper())
        self.assertRegex(
            sql[:sql.index('ROLLBACK;')],
            r'(?is)exclusive\s+deployment\s+session.*discards?\s+any\s+unfinished\s+transaction',
        )

        preflight = next(i for i, part in enumerate(parts) if '$preflight$' in part)
        mutation_indices = [
            i for i, part in enumerate(parts)
            if re.match(r'(?i)^(?:LOCK|CREATE|ALTER|DROP|INSERT|UPDATE|DELETE|MERGE|GRANT|REVOKE)\b', part)
        ]
        commit = next(i for i, part in enumerate(parts) if part.upper() == 'COMMIT')
        self.assertGreater(preflight, 1)
        self.assertLess(0, preflight)
        self.assertTrue(mutation_indices)
        for mutation in mutation_indices:
            self.assertGreater(mutation, 1)
            self.assertLess(mutation, commit)

    def test_delta_checks_existing_roles_and_active_admin_before_ddl(self) -> None:
        parts = statements(self.delta())
        ddl = next(i for i, part in enumerate(parts) if re.match(r'(?i)^(CREATE|ALTER|DROP)\b', part))
        preflight = '\n'.join(parts[1:ddl])
        self.assertRegex(preflight, r'(?i)DO\s+\$preflight\$')
        for table in ('public."AspNetUsers"', 'public."AspNetRoles"', 'public."AspNetUserRoles"'):
            self.assertIn(table, preflight)
        for role in ('operador', 'validador', 'administrador', 'OPERADOR', 'VALIDADOR', 'ADMINISTRADOR'):
            self.assertIn("'" + role + "'", preflight)
        self.assertRegex(preflight, r'(?i)COUNT\([^)]*\)\s*<>\s*1')
        self.assertIn('"NormalizedName"', preflight)
        self.assertIn('"IsActive"', preflight)
        self.assertRegex(
            preflight,
            r'(?is)IF\s+EXISTS\s*\(\s*SELECT\s+1\s+FROM\s+public\."AspNetUsers"\s*\)\s*AND\s+NOT\s+EXISTS',
        )
        self.assertGreaterEqual(len(re.findall(r'(?i)RAISE EXCEPTION', preflight)), 3)
        self.assertNotRegex(preflight, r'(?im)^\s*(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP)\b')

    def test_delta_contains_no_connection_or_secret_material(self) -> None:
        sql = self.delta()
        for forbidden in (
            r'(?i)\b(?:postgres(?:ql)?|https?|file)://',
            r'\b(?:\d{1,3}\.){3}\d{1,3}\b',
            r'(?i)\b(?:host|hostname|port|database|username|user\s+id|password|pwd|token|secret|connectionstring)\s*=',
            r'(?i)\b(?:CREATE|ALTER)\s+(?:ROLE|USER)\s+[^;]*\bLOGIN\b',
            r'(?i)\bPASSWORD\s+\x27',
            r'(?i)-----BEGIN .*PRIVATE KEY-----',
            r'(?i)\b(?:localhost|127\.0\.0\.1|ConnectionStrings__)\b',
        ):
            self.assertNotRegex(sql, forbidden)
        self.assertIn('deployment', sql.lower())
        self.assertIn('outside', sql.lower())

    def test_rate_key_id_check_matches_varchar_64_contract(self) -> None:
        for sql in self.artifacts():
            parts = statements(sql)
            for table in TABLES[1:]:
                create = next(part for part in parts if part.startswith('CREATE TABLE IF NOT EXISTS ' + table + ' ('))
                self.assertRegex(create, r'key_id\s+varchar\(64\)\s+NOT NULL')
                self.assertIn("CHECK (key_id ~ '" + KEY_ID_PATTERN + "')", create)
                self.assertNotRegex(create, r'(?i)\b(?:IDENTITY|SERIAL|nextval)\b')
        for value, accepted in [('a', True), ('0' * 64, True), ('v1._-', True),
                                ('', False), ('a' * 65, False), ('V1', False),
                                ('_v1', False), ('a b', False), ('a\n', False)]:
            self.assertEqual(accepted, re.fullmatch(KEY_ID_PATTERN, value) is not None, value)

    def test_apply_and_delta_share_identity_schema_contract(self) -> None:
        apply, delta = (statements(sql) for sql in self.artifacts())
        for column, definition in (
            ('MustChangePassword', 'boolean NOT NULL DEFAULT false'),
            ('PasswordChangedAt', 'timestamptz NULL'),
            ('ManagementVersion', 'bigint NOT NULL DEFAULT 1 CHECK ("ManagementVersion" > 0)'),
        ):
            expected = f'ALTER TABLE public."AspNetUsers" ADD COLUMN IF NOT EXISTS "{column}" {definition}'
            self.assertIn(expected, [normalized(part) for part in delta])
            self.assertIn(expected, [normalized(part) for part in apply])
        for table in TABLES:
            prefix = 'CREATE TABLE IF NOT EXISTS ' + table + ' ('
            definitions = [next((normalized(part) for part in source if part.startswith(prefix)), None)
                           for source in (apply, delta)]
            self.assertIsNotNone(definitions[1], table)
            self.assertEqual(*definitions, table)
        # Parity includes all Task 1 tables, seeds, function, index and trigger.
        shared = [part for part in delta if part.startswith((
            'ALTER TABLE', 'CREATE TABLE', 'CREATE UNIQUE INDEX', 'CREATE INDEX',
            'CREATE OR REPLACE FUNCTION', 'DROP TRIGGER', 'CREATE CONSTRAINT TRIGGER',
            'CREATE TRIGGER', 'REVOKE', 'INSERT INTO', 'DO $identity_membership_fk$', 'DO $identity_guard_acl$', 'DO $identity_role_structure$'))]
        self.assertGreaterEqual(len(shared), 14)
        for part in shared:
            self.assertIn(normalized(part), [normalized(item) for item in apply])

    def test_structures_preserve_receipts_and_distributed_bucket_identity(self) -> None:
        for sql in self.artifacts():
            parts = statements(sql)
            receipt = next(part for part in parts if part.startswith('CREATE TABLE IF NOT EXISTS ' + TABLES[0]))
            for field in ('key_sha256 bytea PRIMARY KEY', 'actor_user_id uuid NOT NULL',
                          'payload_sha256 bytea NOT NULL', 'user_id uuid NOT NULL',
                          'protected_response bytea NULL', 'created_at timestamptz NOT NULL',
                          'expires_at timestamptz NOT NULL'):
                self.assertIn(field, receipt)
            self.assertNotRegex(receipt, r'(?i)\b(?:IDENTITY|SERIAL|nextval|password|username|payload_json)\b')
            buckets = next(part for part in parts if part.startswith('CREATE TABLE IF NOT EXISTS ' + TABLES[2] + ' ('))
            for field in ('REFERENCES app.identity_rate_limit_key_state(key_id)',
                          'bucket_hmac bytea NOT NULL', 'window_started_at timestamptz NOT NULL',
                          'window_ends_at timestamptz NOT NULL', 'attempt_count integer NOT NULL',
                          'expires_at timestamptz NOT NULL', 'PRIMARY KEY (key_id, bucket_hmac, window_started_at)'):
                self.assertIn(field, buckets)

    def test_rate_key_seed_and_deferred_single_active_are_rotation_safe(self) -> None:
        for sql in self.artifacts():
            parts = statements(sql)
            seed = next(part for part in parts if part.startswith('INSERT INTO app.identity_rate_limit_key_state'))
            self.assertIn("'rate-limit-v1', 'active'", seed)
            self.assertRegex(seed, r'(?is)WHERE NOT EXISTS\s*\(SELECT 1 FROM app\.identity_rate_limit_key_state\)')
            self.assertIn('ON CONFLICT (key_id) DO NOTHING', seed)
            self.assertNotRegex(seed, r'(?i)\bUPDATE\b')
            self.assertRegex(sql, r"(?is)CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON app\.identity_rate_limit_key_state\s*\(state\)\s+WHERE state = 'active'")
            self.assertRegex(sql, r'(?is)CREATE CONSTRAINT TRIGGER \w+\s+AFTER INSERT OR UPDATE OR DELETE ON app\.identity_rate_limit_key_state\s+DEFERRABLE INITIALLY DEFERRED')
            function = next(part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.assert_identity_rate_limit_active_key()'))
            self.assertIn('SECURITY INVOKER', function)
            self.assertIn('SET search_path = pg_catalog', function)
            self.assertRegex(function, r"(?is)count\(\*\).*?FROM app\.identity_rate_limit_key_state WHERE state = 'active'\) <> 1")
            self.assertIn('RAISE EXCEPTION', function)
            roles = next(part for part in parts if part.startswith('INSERT INTO public."AspNetRoles"'))
            for role in ('operador', 'validador', 'administrador'):
                self.assertIn(f"'{role}', '{role.upper()}'", roles)
            self.assertIn('ON CONFLICT ("NormalizedName")', roles)
            self.assertNotRegex(roles, r'(?i)\bUPDATE\b')

    def test_deferred_role_triggers_cover_every_required_event(self) -> None:
        for sql in self.artifacts():
            parts = statements(sql)
            for table, events, function in (
                ('AspNetUsers', 'INSERT OR UPDATE', 'enforce_identity_user_role_cardinality'),
                ('AspNetUserRoles', 'INSERT OR UPDATE OR DELETE', 'enforce_identity_user_role_cardinality'),
                ('AspNetRoles', 'INSERT OR UPDATE OR DELETE', 'enforce_identity_role_catalog'),
            ):
                with self.subTest(table=table):
                    self.assertRegex(sql, rf'(?is)CREATE CONSTRAINT TRIGGER \w+\s+AFTER {events} ON public\."{table}"\s+DEFERRABLE INITIALLY DEFERRED\s+FOR EACH ROW EXECUTE FUNCTION app\.{function}\(\)')
            fk = next((part for part in parts if part.startswith('DO $identity_membership_fk$')), '')
            self.assertIn('pg_catalog.pg_constraint', fk)
            self.assertIn('public."AspNetUserRoles"', fk)
            self.assertIn('public."AspNetUsers"', fk)
            self.assertIn('ALTER CONSTRAINT %I DEFERRABLE INITIALLY DEFERRED', fk)
            cardinality = next(part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.enforce_identity_user_role_cardinality()'))
            for reference in ('OLD."Id"', 'NEW."Id"', 'OLD."UserId"', 'NEW."UserId"'):
                self.assertIn(reference, cardinality)
            self.assertIn('CONTINUE', cardinality)
            self.assertRegex(cardinality, r'(?is)count\([^)]*\).*?<> 1')
            catalog = next(part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.enforce_identity_role_catalog()'))
            for role in ('operador', 'validador', 'administrador', 'OPERADOR', 'VALIDADOR', 'ADMINISTRADOR'):
                self.assertIn("'" + role + "'", catalog)
            for reference in ('OLD."Id"', 'NEW."Id"'):
                self.assertIn(reference, catalog)

    def test_role_catalog_prevents_semantic_swaps_and_preserves_installed_ids(self) -> None:
        # A final three-name check alone permits admin/operator IDs to swap meaning.
        # Reject every historical semantic mutation, even if later writes restore
        # canonical names; retain arbitrary existing Identity UUIDs and stamp updates.
        for sql in self.artifacts():
            parts = statements(sql)
            catalog = next(part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.enforce_identity_role_catalog()'))
            self.assertIn("IF TG_OP IN ('INSERT', 'DELETE') THEN", catalog)
            self.assertIn("RAISE EXCEPTION 'identity_role_catalog_immutable'", catalog)
            for column in ('Id', 'Name', 'NormalizedName'):
                self.assertIn(f'NEW."{column}" IS DISTINCT FROM OLD."{column}"', catalog)
            self.assertLess(catalog.index('identity_role_catalog_immutable'),
                            catalog.index('SELECT count(*) FROM public."AspNetRoles"'))
            self.assertNotIn('ConcurrencyStamp', catalog)
            self.assertNotRegex(catalog, r'(?i)md5|identity-role:|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
            seed = next(i for i, part in enumerate(parts) if part.startswith('INSERT INTO public."AspNetRoles"'))
            trigger = next(i for i, part in enumerate(parts) if part.startswith('CREATE CONSTRAINT TRIGGER trg_identity_role_catalog'))
            self.assertLess(seed, trigger)

    def test_admin_guard_requires_actor_lock_and_qualified_references(self) -> None:
        for sql in self.artifacts():
            parts = statements(sql)
            guard = next((part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.guard_identity_admin_mutation()')), '')
            self.assertTrue(guard, 'Administrative mutation guard is missing')
            self.assertIn('SET LOCAL app.actor_user_id', sql)
            self.assertIn('pg_catalog.pg_advisory_xact_lock(714205736012345678::bigint)', guard)
            self.assertIn("pg_catalog.current_setting('app.actor_user_id', true)", guard)
            self.assertIn("pg_catalog.pg_has_role(session_user, 'gds_db_deployer', 'member')", guard)
            self.assertIn("pg_catalog.current_setting('app.bootstrap_mode', true)", guard)
            self.assertIn('pg_catalog.pg_current_xact_id()', guard)
            for setting in ('app.bootstrap_xact_id', 'app.bootstrap_user_id'):
                self.assertIn(f"pg_catalog.set_config('{setting}',", guard)
                self.assertIn(f"pg_catalog.current_setting('{setting}', true)", guard)
            self.assertIn('identity_bootstrap_requires_single_admin', guard)
            self.assertIn('(SELECT count(*) FROM public."AspNetUsers") <> 1', guard)
            self.assertIn("TG_OP <> 'INSERT'", guard)
            self.assertLess(guard.index('pg_catalog.pg_advisory_xact_lock'), guard.index("pg_catalog.current_setting('app.bootstrap_mode'"))
            self.assertIn('NOT EXISTS (SELECT 1 FROM public."AspNetUsers")', guard)
            self.assertIn("pg_catalog.current_setting('transaction_isolation') <> 'read committed'", guard)
            self.assertIn('identity_admin_isolation_unsupported', guard)
            self.assertIn('TG_WHEN', guard)
            self.assertIn('identity_self_management_forbidden', guard)
            self.assertIn('identity_last_active_admin', guard)
            for function in ('enforce_identity_user_role_cardinality', 'enforce_identity_role_catalog',
                             'guard_identity_admin_mutation', 'reject_immutable_change'):
                definition = next(part for part in parts if part.startswith('CREATE OR REPLACE FUNCTION app.' + function + '()'))
                self.assertIn('SECURITY INVOKER', definition)
                self.assertIn('SET search_path = pg_catalog', definition)
                self.assertNotIn('SECURITY DEFINER', definition)
                self.assertNotIn('pg_temp', definition)
                self.assertNotRegex(definition, r'(?i)(?:FROM|JOIN|UPDATE|INTO)\s+(?:"AspNet|identity_)')
                self.assertIn(f'REVOKE EXECUTE ON FUNCTION app.{function}() FROM PUBLIC', sql)
                self.assertIn(f'REVOKE EXECUTE ON FUNCTION app.{function}() FROM gds_identity_admin', sql)
            for table in ('AspNetUsers', 'AspNetUserRoles'):
                self.assertRegex(sql, rf'(?is)CREATE TRIGGER \w+\s+BEFORE INSERT OR UPDATE OR DELETE ON public\."{table}"\s+FOR EACH ROW EXECUTE FUNCTION app\.guard_identity_admin_mutation\(\)')
                self.assertRegex(sql, rf'(?is)CREATE CONSTRAINT TRIGGER \w+\s+AFTER INSERT OR UPDATE OR DELETE ON public\."{table}"\s+DEFERRABLE INITIALLY DEFERRED\s+FOR EACH ROW EXECUTE FUNCTION app\.guard_identity_admin_mutation\(\)')

    def test_identity_admin_matches_exact_column_allowlist(self) -> None:
        grants = (
            'GRANT USAGE ON SCHEMA public, app TO gds_identity_admin',
            'GRANT SELECT ("Id", "UserName", "NormalizedUserName", "DisplayName", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "IsActive", "CreatedAt", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin',
            'GRANT INSERT ("Id", "UserName", "NormalizedUserName", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "DisplayName", "IsActive", "CreatedAt", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin',
            'GRANT UPDATE ("PasswordHash", "SecurityStamp", "ConcurrencyStamp", "IsActive", "UpdatedAt", "MustChangePassword", "PasswordChangedAt", "ManagementVersion") ON public."AspNetUsers" TO gds_identity_admin',
            'GRANT SELECT ("Id", "Name", "NormalizedName") ON public."AspNetRoles" TO gds_identity_admin',
            'GRANT SELECT ("UserId", "RoleId"), INSERT ("UserId", "RoleId"), DELETE ON public."AspNetUserRoles" TO gds_identity_admin',
            'GRANT INSERT (actor_user_id, action, outcome, entity_type, entity_id, correlation_id, source_ip_hash, changes, security_context) ON app.audit_events TO gds_identity_admin',
            'GRANT SELECT (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at), INSERT (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at), UPDATE (protected_response) ON app.identity_idempotency_receipts TO gds_identity_admin',
            'GRANT SELECT (key_id, state, changed_at) ON app.identity_rate_limit_key_state TO gds_identity_admin',
            'GRANT SELECT (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at), INSERT (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at), UPDATE (attempt_count, expires_at), DELETE ON app.identity_rate_limits TO gds_identity_admin',
        )
        for sql in self.artifacts():
            compact = [normalized(part) for part in statements(sql)]
            for grant in grants:
                self.assertIn(grant, compact)
            self.assertRegex(sql, r'(?i)ALTER ROLE gds_identity_admin\s+NOLOGIN\s+NOINHERIT\s+NOSUPERUSER\s+NOCREATEDB\s+NOCREATEROLE\s+NOREPLICATION\s+NOBYPASSRLS')
            self.assertIn('identity_admin_unexpected_effective_privilege', sql)
            self.assertIn('identity_admin_unexpected_membership', sql)
            self.assertIn('identity_admin_unexpected_owned_object', sql)

    def test_identity_admin_has_no_sequence_or_direct_application_execute(self) -> None:
        for sql in self.artifacts():
            self.assertIn('REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public, app FROM gds_identity_admin', sql)
            self.assertIn('REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA app FROM gds_identity_admin', sql)
            self.assertIn("p.pronamespace = 'app'::pg_catalog.regnamespace", sql)
            self.assertIn("c.relkind = 'S'", sql)
            self.assertIn("pg_catalog.has_function_privilege('gds_identity_admin', p.oid, 'EXECUTE')", sql)
            self.assertIn("pg_catalog.has_sequence_privilege('gds_identity_admin', c.oid, 'USAGE,SELECT,UPDATE')", sql)

    def test_sequence_privilege_checks_are_type_guarded(self) -> None:
        """Never call has_sequence_privilege for non-sequence pg_class rows.

        PostgreSQL may reorder ordinary WHERE predicates, so a sibling
        ``c.relkind = 'S'`` predicate is not an evaluation barrier.
        """
        sources = (*self.artifacts(), (
            ROOT / 'backend-tests/user_administration_postgres_runner.py'
        ).read_text())
        guarded = re.compile(
            r"(?is)CASE\s+WHEN\s+c\.relkind\s*=\s*'S'\s+THEN\s+"
            r"pg_catalog\.has_sequence_privilege\(\s*'gds_identity_admin'\s*,\s*"
            r"c\.oid\s*,\s*'USAGE,SELECT,UPDATE'\s*\)\s+ELSE\s+FALSE\s+END"
        )
        unguarded = re.compile(
            r"(?is)AND\s+pg_catalog\.has_sequence_privilege\(\s*"
            r"'gds_identity_admin'\s*,\s*c\.oid"
        )
        for source in sources:
            self.assertIsNotNone(guarded.search(source))
            self.assertIsNone(unguarded.search(source))

    def test_public_extension_execute_is_exactly_gen_random_uuid(self) -> None:
        for sql in self.artifacts():
            self.assertIn('pg_catalog.pg_extension', sql)
            self.assertIn("p.pronamespace <> 'pg_catalog'::pg_catalog.regnamespace", sql)
            self.assertIn("p.oid <> 'public.gen_random_uuid()'::pg_catalog.regprocedure", sql)
            self.assertIn("REVOKE EXECUTE ON FUNCTION %s FROM PUBLIC", sql)
            self.assertIn('GRANT EXECUTE ON FUNCTION public.gen_random_uuid() TO PUBLIC', sql)
            self.assertIn('unexpected_public_extension_execute', sql)

    def test_platform_public_baselines_are_reported_separately(self) -> None:
        for sql in self.artifacts():
            commit = next(i for i, part in enumerate(statements(sql)) if part.upper() == 'COMMIT')
            reports = '\n'.join(statements(sql)[commit + 1:])
            self.assertIn('identity_baseline:platform_public_database', reports)
            self.assertIn('identity_baseline:platform_pg_catalog_execute', reports)
            self.assertIn("acl.grantee = 0", reports)
            self.assertIn("acl.privilege_type = 'CONNECT'", reports)
            self.assertIn("acl.privilege_type = 'TEMPORARY'", reports)

    def test_runtime_privileges_are_unchanged(self) -> None:
        for sql in self.artifacts():
            self.assertIn('identity_baseline:runtime_privilege', sql)
            self.assertIn("r.rolname = 'gds_app_runtime'", sql)
            reconciliation = sql[sql.index('-- Identity administration privilege reconciliation'):sql.index('COMMIT;')]
            self.assertNotRegex(reconciliation, r'(?i)\b(?:GRANT|REVOKE|ALTER\s+ROLE)\b[^;]*\bgds_app_runtime\b')

    def test_final_reconciliation_enumerates_exact_acl_and_full_metadata(self) -> None:
        expected_reports = (
            'managed_acl', 'function_metadata', 'constraint_metadata',
            'trigger_metadata', 'role_attributes', 'memberships',
            'owned_objects', 'public_extension_execute',
        )
        for sql in self.artifacts():
            commit = next(i for i, part in enumerate(statements(sql)) if part.upper() == 'COMMIT')
            reports = '\n'.join(statements(sql)[commit + 1:])
            self.assertIn('identity_reconciliation_begin', reports)
            self.assertIn('identity_reconciliation_end', reports)
            self.assertIn('identity_reconciliation_manifest', reports)
            for report in expected_reports:
                self.assertIn(f'identity_problem:{report}', reports)
            for catalog in (
                'information_schema.column_privileges',
                'information_schema.table_privileges',
                'pg_catalog.pg_constraint', 'pg_catalog.pg_trigger', 'pg_catalog.pg_proc',
            ):
                self.assertIn(catalog, reports)
            for metadata in (
                'pg_catalog.pg_get_function_identity_arguments', 'prosecdef', 'proconfig',
                'tgdeferrable', 'tginitdeferred', 'tgenabled', 'condeferrable', 'condeferred',
                'pg_catalog.has_function_privilege',
            ):
                self.assertIn(metadata, reports)

            report_statements = statements(sql)[commit + 1:]
            constraint_report = next(
                part for part in report_statements
                if "identity_problem:constraint_metadata" in part
            )
            trigger_report = next(
                part for part in report_statements
                if "identity_problem:trigger_metadata" in part
            )
            self.assertNotIn('count(*) >=', constraint_report)
            self.assertIn('normalized_definition', constraint_report)
            self.assertIn('actual.normalized_definition IS DISTINCT FROM expected.normalized_definition',
                          constraint_report)
            for constraint_name in (
                'AspNetUsers_ManagementVersion_check',
                'AspNetUserRoles_UserId_fkey',
                'identity_idempotency_receipts_pkey',
                'identity_idempotency_receipts_key_sha256_check',
                'identity_idempotency_receipts_actor_user_id_fkey',
                'identity_idempotency_receipts_payload_sha256_check',
                'identity_idempotency_receipts_user_id_fkey',
                'ck_identity_receipt_expiry',
                'identity_rate_limit_key_state_pkey',
                'identity_rate_limit_key_state_key_id_check',
                'identity_rate_limit_key_state_state_check',
                'identity_rate_limits_pkey',
                'identity_rate_limits_key_id_fkey',
                'identity_rate_limits_key_id_check',
                'identity_rate_limits_bucket_hmac_check',
                'identity_rate_limits_attempt_count_check',
                'ck_identity_rate_limit_window',
                'ck_identity_rate_limit_expiry',
            ):
                self.assertIn("'" + constraint_name + "'", constraint_report)

            self.assertNotIn('::pg_catalog.regclass::text', trigger_report)
            self.assertNotIn('::pg_catalog.regprocedure::text', trigger_report)
            self.assertIn("pg_catalog.format('%I.%I', relation_namespace.nspname, relation.relname)",
                          trigger_report)
            self.assertIn("pg_catalog.format('%I.%I(%s)', function_namespace.nspname, routine.proname",
                          trigger_report)
            for field in (
                'is_before', 'on_insert', 'on_update', 'on_delete', 'on_truncate',
                'for_each_row', 'is_constraint', 'tgdeferrable', 'tginitdeferred',
            ):
                self.assertIn(field, trigger_report)
            self.assertIn('actual.on_insert IS DISTINCT FROM expected.on_insert', trigger_report)
            self.assertIn('actual.for_each_row IS DISTINCT FROM expected.for_each_row', trigger_report)

    def test_final_reconciliation_reports_are_paired_and_read_only(self) -> None:
        post_commit = []
        for sql in self.artifacts():
            parts = statements(sql)
            commit = next(i for i, part in enumerate(parts) if part.upper() == 'COMMIT')
            tail = [normalized(part) for part in parts[commit + 1:]]
            self.assertTrue(tail)
            self.assertTrue(all(part.startswith('SELECT') or part.startswith('WITH') for part in tail))
            post_commit.append(tail)
        self.assertEqual(post_commit[0], post_commit[1])

    def test_membership_lock_uses_narrow_definer_and_no_direct_update(self) -> None:
        delta = (ROOT / 'docs/runbooks/identity-membership-lock-fix.sql').read_text()
        store = (ROOT / 'backend/IdentityAdministration/IdentityAdministrationStore.cs').read_text()
        for sql in [*self.artifacts(), delta]:
            self.assertIn('CREATE OR REPLACE FUNCTION app.lock_identity_user_roles(p_user uuid)', sql)
            self.assertIn('SECURITY DEFINER\nSET search_path = pg_catalog', sql)
            self.assertIn('WHERE s."UserId" = p_user\n    ORDER BY s."RoleId"\n    FOR UPDATE', sql)
            self.assertIn('REVOKE ALL ON FUNCTION app.lock_identity_user_roles(uuid) FROM PUBLIC', sql)
            self.assertIn('GRANT EXECUTE ON FUNCTION app.lock_identity_user_roles(uuid) TO gds_identity_admin', sql)
            self.assertNotRegex(sql, r'GRANT UPDATE[^;]*ON public\."AspNetUserRoles"')
        self.assertIn('FROM app.lock_identity_user_roles(@target_user_id)', store)

    def test_old_schema_fixture_is_frozen_before_administration(self) -> None:
        self.assertEqual('8a0bb85c88f01bb402b273c2dbc0176abce748c7faa33522a26db83342830059',
                         hashlib.sha256(FIXTURE.read_bytes()).hexdigest())
        self.assertNotIn('"ManagementVersion"', FIXTURE.read_text())
        self.assertNotIn('identity_rate_limit_key_state', FIXTURE.read_text())


if __name__ == '__main__':
    unittest.main()
