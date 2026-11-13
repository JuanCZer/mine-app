import { execFileSync } from 'node:child_process';

const branch = process.argv[2] ?? '';
const git = (...args) => execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

try {
  if (!branch.startsWith('release/') || branch === 'release/') throw new Error();
  git('check-ref-format', '--branch', branch);
  const release = git('rev-parse', '--verify', `refs/remotes/origin/${branch}^{commit}`);
  const main = git('rev-parse', '--verify', 'refs/remotes/origin/main^{commit}');
  if (!/^[0-9a-f]{40,64}$/.test(release) || !/^[0-9a-f]{40,64}$/.test(main)) throw new Error();
  if (release !== main) throw new Error();
  process.stdout.write(`${release}\n`);
} catch {
  process.stderr.write('La rama debe existir en este repositorio, comenzar por release/ y apuntar al mismo commit actual de main.\n');
  process.exitCode = 1;
}
