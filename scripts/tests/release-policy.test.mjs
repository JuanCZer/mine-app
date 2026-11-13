import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import test from 'node:test';

const script = fileURLToPath(new URL('../verify-release-main.mjs', import.meta.url));
const git = (directory, ...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8', stdio: ['ignore','pipe','pipe'] }).trim();

test('solo acepta una rama release en el commit actual de main', () => {
  const dir = mkdtempSync(path.join(tmpdir(), 'mineapp-release-test-'));
  try {
    git(dir, 'init', '-q', '-b', 'main');
    git(dir, 'config', 'user.name', 'Prueba');
    git(dir, 'config', 'user.email', 'test@example.invalid');
    writeFileSync(path.join(dir, 'a'), 'initial');
    git(dir, 'add', 'a'); git(dir, 'commit', '-qm', 'initial');
    const initial = git(dir, 'rev-parse', 'HEAD');
    git(dir, 'update-ref', 'refs/remotes/origin/main', initial);
    git(dir, 'update-ref', 'refs/remotes/origin/release/ready', initial);
    git(dir, 'update-ref', 'refs/remotes/origin/feature/ready', initial);
    git(dir, 'checkout', '-qb', 'future');
    writeFileSync(path.join(dir, 'a'), 'future');
    git(dir, 'commit', '-qam', 'future');
    git(dir, 'update-ref', 'refs/remotes/origin/release/future', git(dir, 'rev-parse', 'HEAD'));
    const run = (branch) => spawnSync(process.execPath, [script, branch], { cwd: dir, encoding: 'utf8' });
    const accepted = run('release/ready');
    assert.equal(accepted.status, 0, accepted.stderr);
    assert.equal(accepted.stdout.trim(), initial);
    for (const rejected of ['feature/ready', 'release/future', 'release/missing', 'release/../main', 'release/']) {
      assert.notEqual(run(rejected).status, 0, rejected);
    }
    git(dir, 'checkout', '-q', 'main');
    writeFileSync(path.join(dir, 'a'), 'new main');
    git(dir, 'commit', '-qam', 'new main');
    const latest = git(dir, 'rev-parse', 'HEAD');
    git(dir, 'update-ref', 'refs/remotes/origin/main', latest);
    assert.notEqual(run('release/ready').status, 0, 'release antiguo no incluye main actualizado');
    git(dir, 'update-ref', 'refs/remotes/origin/release/ready', latest);
    assert.equal(run('release/ready').stdout.trim(), latest);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
