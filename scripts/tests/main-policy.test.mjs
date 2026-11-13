import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import test from 'node:test';

const script = fileURLToPath(new URL('../verify-main-rules.mjs', import.meta.url));
const review = { type: 'pull_request', parameters: { required_approving_review_count: 1, dismiss_stale_reviews_on_push: true, allowed_merge_methods: ['merge'] } };
const checks = { type: 'required_status_checks', parameters: { required_status_checks: [{ context: 'verify' }] } };
const run = (rules) => spawnSync(process.execPath, [script], { input: JSON.stringify(rules), encoding: 'utf8' });

test('la integración requiere revisión vigente, método merge y CI obligatorio en reglas activas', () => {
  assert.equal(run([review, checks]).status, 0);
  for (const invalid of [[], [review], [checks], [{ ...review, parameters: { ...review.parameters, dismiss_stale_reviews_on_push: false } }, checks],
    [{ ...review, parameters: { ...review.parameters, required_approving_review_count: 0 } }, checks],
    [{ ...review, parameters: { ...review.parameters, allowed_merge_methods: ['squash'] } }, checks],
    [review, { ...checks, parameters: { required_status_checks: [{ context: 'otra' }] } }]]) {
    assert.notEqual(run(invalid).status, 0);
  }
});

test('plantilla de protección está desactivada y el job de merge no ejecuta código del PR', async () => {
  const { readFile } = await import('node:fs/promises');
  const policy = JSON.parse(await readFile(new URL('../../.github/rulesets/main.disabled.json', import.meta.url), 'utf8'));
  const workflow = await readFile(new URL('../../deploy/futuro/workflows/merge-main.yml', import.meta.url), 'utf8');
  assert.equal(policy.enforcement, 'disabled');
  assert.deepEqual(policy.bypass_actors, []);
  assert.match(workflow, /vars\.ENABLE_MAIN_AUTO_MERGE == 'true'/);
  assert.match(workflow, /vars\.MAIN_POLICY_NO_BYPASS_CONFIRMED == 'true'/);
  assert.match(workflow, /ref: \$\{\{ github\.event\.pull_request\.base\.sha \}\}/);
  assert.doesNotMatch(workflow, /ref: \$\{\{ github\.event\.pull_request\.head\.sha \}\}/);
  assert.match(workflow, /verify-main-rules\.mjs/);
  assert.match(workflow, /--match-head-commit/);
});
