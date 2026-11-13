import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';

const root = new URL('../../', import.meta.url);
const read = (path) => readFile(new URL(path, root), 'utf8');

test('únicamente la pipeline provisional está disponible para GitHub Actions', async () => {
  const files = (await readdir(new URL('.github/workflows/', root)))
    .filter((file) => /\.ya?ml$/i.test(file));
  assert.deepEqual(files, ['deploy-provisional.yml']);
  for (const file of ['ci.yml', 'deploy.yml', 'merge-main.yml']) {
    assert.ok((await read(`deploy/futuro/workflows/${file}`)).length > 0);
  }
});

test('despliegue provisional usa main y runner propio sin ejecutar código de PR', async () => {
  const workflow = await read('.github/workflows/deploy-provisional.yml');
  assert.match(workflow, /push:\s*\n\s*branches:\s*\[main\]/);
  assert.doesNotMatch(workflow, /pull_request(?:_target)?:/);
  assert.match(workflow, /self-hosted/);
  assert.match(workflow, /contents:\s*read/);
  assert.match(workflow, /persist-credentials:\s*false/);
  assert.match(workflow, /deploy\/provisional\/compose\.ya?ml/);
  assert.match(workflow, /docker compose[^\n]*build/);
  assert.match(workflow, /docker compose[^\n]*up/);
});

test('el despliegue provisional usa un proyecto y una imagen de desarrollo propios', async () => {
  const compose = await read('deploy/provisional/compose.yaml');
  const workflow = await read('.github/workflows/deploy-provisional.yml');
  const example = await read('deploy/provisional/deploy.env.example');
  assert.match(compose, /^name: mineapp-dev$/m);
  assert.match(compose, /image: mineapp-dev-web:/);
  assert.match(workflow, /COMPOSE_PROJECT_NAME: mineapp-dev/);
  assert.match(example, /^MINE_APP_PORT=8081$/m);
  assert.doesNotMatch(example, /^ConnectionStrings__/m);
});
