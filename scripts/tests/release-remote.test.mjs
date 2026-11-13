import assert from 'node:assert/strict';
import test from 'node:test';
import { refsMatch } from '../verify-release-remote.mjs';

const expected = 'a'.repeat(40);
const result = (main, release) => ({ data: { repository: { main: { target: { oid: main } }, release: { target: { oid: release } } } } });

test('el despliegue se detiene si main o release cambian después de CI', () => {
  assert.equal(refsMatch(result(expected, expected), expected), true);
  assert.equal(refsMatch(result('b'.repeat(40), expected), expected), false);
  assert.equal(refsMatch(result(expected, 'b'.repeat(40)), expected), false);
  assert.equal(refsMatch({ errors: [{ message: 'unavailable' }] }, expected), false);
  assert.equal(refsMatch({ data: { repository: null } }, expected), false);
});

test('consulta ambas referencias usando variables GraphQL y falla si GitHub no responde correctamente', async () => {
  const { verifyRemoteRelease } = await import('../verify-release-remote.mjs');
  const env = { GITHUB_REPOSITORY: 'owner/repo', RELEASE_BRANCH: 'release/v1', EXPECTED_RELEASE_SHA: expected,
    GH_TOKEN: 'synthetic-token', GITHUB_GRAPHQL_URL: 'https://example.invalid/graphql' };
  let captured;
  const fetchStub = async (url, options) => {
    captured = { url, options };
    return { ok: true, json: async () => result(expected, expected) };
  };
  await verifyRemoteRelease(env, fetchStub);
  assert.equal(JSON.parse(captured.options.body).variables.release, 'refs/heads/release/v1');
  await assert.rejects(verifyRemoteRelease(env, async () => ({ ok: false })));
  await assert.rejects(verifyRemoteRelease(env, async () => ({ ok: true, json: async () => result('b'.repeat(40), expected) })));
});
