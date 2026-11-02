import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseEnv } from 'node:util';

import { spawnWithSignals } from './run-backend.mjs';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const deploymentName = 'POSTGRES_DEPLOY_URL';

async function deploymentEnvironment(env = process.env) {
  let fileEnvironment = {};
  try {
    fileEnvironment = parseEnv(await readFile(path.join(projectRoot, 'backend/.env'), 'utf8'));
  } catch (error) {
    if (error?.code !== 'ENOENT') throw error;
  }
  const connection = Object.hasOwn(env, deploymentName)
    ? env[deploymentName]
    : fileEnvironment[deploymentName];
  if (typeof connection !== 'string' || connection.trim() === '') {
    throw new Error('POSTGRES_DEPLOY_URL is required.');
  }
  const clean = Object.fromEntries(Object.entries(env).filter(([key, value]) =>
    value !== undefined
    && !key.toLowerCase().startsWith('connectionstrings__')
    && key.toLowerCase() !== deploymentName.toLowerCase()));
  return { ...clean, [deploymentName]: connection };
}

try {
  const result = await spawnWithSignals({
    command: 'dotnet',
    args: ['run', '--project', 'backend-import/LegacyImport.Tool.csproj', '--', ...process.argv.slice(2)],
    options: { cwd: projectRoot, env: await deploymentEnvironment(), shell: false, stdio: 'inherit' },
    processRef: process,
    spawnImpl: spawn,
  });
  process.exitCode = result.code ?? 1;
} catch (error) {
  process.stderr.write(`${error instanceof Error ? error.message : 'Legacy import failed.'}\n`);
  process.exitCode = 1;
}
