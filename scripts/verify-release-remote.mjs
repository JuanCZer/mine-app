import { pathToFileURL } from 'node:url';
import path from 'node:path';

export function refsMatch(result, expected) {
  const repository = result?.data?.repository;
  return !result?.errors?.length && /^[a-f0-9]{40,64}$/.test(expected ?? '')
    && repository?.main?.target?.oid === expected && repository?.release?.target?.oid === expected;
}

export async function verifyRemoteRelease(env = process.env, fetchImpl = fetch) {
  const [owner, name, extra] = (env.GITHUB_REPOSITORY ?? '').split('/');
  const branch = env.RELEASE_BRANCH ?? '';
  const expected = env.EXPECTED_RELEASE_SHA ?? '';
  const token = env.GH_TOKEN ?? '';
  const endpoint = env.GITHUB_GRAPHQL_URL ?? '';
  if (!owner || !name || extra || !branch.startsWith('release/') || !token || !endpoint.startsWith('https://')) throw new Error();
  const query = `query($owner:String!,$name:String!,$release:String!){repository(owner:$owner,name:$name){main:ref(qualifiedName:"refs/heads/main"){target{oid}} release:ref(qualifiedName:$release){target{oid}}}}`;
  const response = await fetchImpl(endpoint, {
    method: 'POST',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify({ query, variables: { owner, name, release: `refs/heads/${branch}` } }),
    signal: AbortSignal.timeout(15000),
  });
  if (!response.ok || !refsMatch(await response.json(), expected)) throw new Error();
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    await verifyRemoteRelease();
    process.stdout.write('main y release conservan el commit validado.\n');
  } catch {
    process.stderr.write('No se pudo confirmar que main y release siguen en el commit validado.\n');
    process.exitCode = 1;
  }
}
