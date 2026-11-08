import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

test('build:clean genera configuración antes de ejecutar Angular', async () => {
  const project = await mkdtemp(path.join(tmpdir(), 'gds-clean-build-'));
  try {
    const original = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
    await mkdir(path.join(project, 'scripts'), { recursive: true });
    await mkdir(path.join(project, 'node_modules/@angular/cli/bin'), { recursive: true });
    for (const name of ['generate-public-env.mjs', 'build-angular-clean.mjs']) {
      await writeFile(path.join(project, 'scripts', name), await readFile(path.join(root, 'scripts', name)));
    }
    await writeFile(path.join(project, 'package.json'), JSON.stringify({ private: true, scripts: original.scripts }));
    await writeFile(path.join(project, 'node_modules/@angular/cli/bin/ng.js'),
      "const fs = require('node:fs'); fs.writeFileSync('build-ran', 'yes');\n");
    const env = { ...process.env, MAPBOX_PUBLIC_TOKEN: 'pk.fake.test' };
    execFileSync('npm', ['run', 'build:clean'], { cwd: project, env, stdio: 'pipe' });
    assert.match(await readFile(path.join(project, 'client/app/core/config/public-env.generated.ts'), 'utf8'), /pk\.fake\.test/);
    assert.equal(await readFile(path.join(project, 'build-ran'), 'utf8'), 'yes');
    await rm(path.join(project, 'build-ran'));
    const failed = spawnSync('npm', ['run', 'build:clean'], { cwd: project, env: { ...env, MAPBOX_PUBLIC_TOKEN: 'invalid' }, encoding: 'utf8' });
    assert.notEqual(failed.status, 0);
    await assert.rejects(readFile(path.join(project, 'build-ran')), { code: 'ENOENT' });
  } finally {
    await rm(project, { recursive: true, force: true });
  }
});
