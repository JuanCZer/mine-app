import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = (name) => readFile(path.join(root, name), 'utf8');

test('El contenedor web sirve ambos componentes y protege configuración y proxy', async () => {
  const [compose, dockerfile, caddy] = await Promise.all([read('compose.yaml'), read('Dockerfile'), read('deploy/Caddyfile.example')]);
  assert.match(compose, /^  web:\s*$/m);
  assert.doesNotMatch(compose, /^  (?:api|frontend):\s*$/m);
  assert.match(compose, /127\.0\.0\.1:\$\{MINE_APP_PORT:-8080\}:8080/);
  assert.match(compose, /format:\s*raw/);
  assert.match(compose, /HttpSecurity__KnownProxies__0:\s*\$\{MINE_APP_CADDY_PROXY_IP:/);
  assert.match(compose, /identity_keys:\/var\/lib\/mine-app\/keys/);
  assert.match(dockerfile, /COPY --from=frontend-build[^\n]*\/app\/wwwroot/);
  assert.match(dockerfile, /Spa__Enabled=true/);
  assert.match(caddy, /header_up X-Forwarded-For \{remote_host\}/);
  assert.match(caddy, /header_up X-Forwarded-Proto https/);
});

test('El runner privado se usa solo en despliegue manual desde main', async () => {
  const [ci, deploy] = await Promise.all([read('deploy/futuro/workflows/ci.yml'), read('deploy/futuro/workflows/deploy.yml')]);
  assert.match(ci, /pull_request:/);
  assert.doesNotMatch(ci, /self-hosted/);
  assert.match(deploy, /workflow_dispatch:/);
  assert.doesNotMatch(deploy, /pull_request(?:_target)?:/);
  assert.match(deploy, /vars\.ENABLE_PRODUCTION_DEPLOY == 'true'.*github\.ref == 'refs\/heads\/main'/);
  assert.match(deploy, /verify-release-main\.mjs/);
  assert.match(deploy, /test \"\$release_sha\" = \"\$GITHUB_SHA\"/);
  assert.match(deploy, /source_ref: \$\{\{ needs\.validate_release\.outputs\.release_sha \}\}/);
  assert.match(deploy, /ref: \$\{\{ needs\.validate_release\.outputs\.release_sha \}\}/);
  assert.match(deploy, /runs-on: \[self-hosted, linux, mineapp-deploy\]/);
  assert.match(deploy, /POSTGRES_DEPLOY_URL/);
});

test('El contexto Docker excluye credenciales, llaves y datos heredados', async () => {
  const patterns = new Set((await read('.dockerignore')).split(/\r?\n/).map(x => x.trim()));
  for (const pattern of ['.env', '**/.env', '**/.env.*', '**/*.key', '**/*.pfx', 'server/Data/notes.json']) {
    assert.ok(patterns.has(pattern), `Falta exclusión ${pattern}`);
  }
});
