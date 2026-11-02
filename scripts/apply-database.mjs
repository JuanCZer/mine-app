import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseEnv } from 'node:util';

import { spawnWithSignals } from './run-backend.mjs';

const modulePath = fileURLToPath(import.meta.url);
const defaultProjectRoot = path.resolve(path.dirname(modulePath), '..');
const deploymentConnectionName = 'POSTGRES_DEPLOY_URL';
const deploymentConnectionNameLower = deploymentConnectionName.toLowerCase();
const deploymentConnectionError =
  'POSTGRES_DEPLOY_URL must be a valid PostgreSQL URL with sslmode=verify-full.';

function isConnectionVariable(name) {
  const normalized = name.toLowerCase();
  return (
    normalized === deploymentConnectionNameLower ||
    normalized.startsWith('pg') ||
    normalized.startsWith('connectionstrings__')
  );
}

async function readBackendEnvironment(projectRoot) {
  try {
    return parseEnv(await readFile(path.join(projectRoot, 'backend/.env'), 'utf8'));
  } catch (error) {
    if (error?.code === 'ENOENT') {
      return {};
    }
    throw error;
  }
}

export function validateDeploymentConnection(value) {
  if (value !== value.trim() || /[\u0000-\u001f\u007f]/.test(value)) {
    throw new Error(deploymentConnectionError);
  }

  let connection;
  try {
    connection = new URL(value);
  } catch {
    throw new Error(deploymentConnectionError);
  }

  if (connection.protocol !== 'postgres:' && connection.protocol !== 'postgresql:') {
    throw new Error(deploymentConnectionError);
  }

  const sslModes = [...connection.searchParams]
    .filter(([name]) => name.toLowerCase() === 'sslmode')
    .map(([, mode]) => mode.toLowerCase());
  if (sslModes.length !== 1 || sslModes[0] !== 'verify-full') {
    throw new Error(deploymentConnectionError);
  }

  return value;
}

export async function buildDatabaseDeploymentEnvironment({
  projectRoot = defaultProjectRoot,
  env = process.env,
} = {}) {
  const fileEnvironment = await readBackendEnvironment(projectRoot);
  const deploymentConnection = Object.hasOwn(env, deploymentConnectionName)
    ? env[deploymentConnectionName]
    : fileEnvironment[deploymentConnectionName];

  if (typeof deploymentConnection !== 'string' || deploymentConnection.trim() === '') {
    throw new Error('POSTGRES_DEPLOY_URL is required.');
  }
  const verifiedDeploymentConnection = validateDeploymentConnection(deploymentConnection);

  const childEnvironment = Object.fromEntries(
    Object.entries(env).filter(([key, value]) => value !== undefined && !isConnectionVariable(key)),
  );

  return {
    ...childEnvironment,
    PGDATABASE: verifiedDeploymentConnection,
  };
}

export async function runDatabaseDeployment({
  projectRoot = defaultProjectRoot,
  env = process.env,
  processRef = process,
  spawnImpl = spawn,
} = {}) {
  const deploymentEnvironment = await buildDatabaseDeploymentEnvironment({ projectRoot, env });
  return spawnWithSignals({
    command: 'psql',
    args: [
      '--set',
      'ON_ERROR_STOP=on',
      '--file',
      path.join(projectRoot, 'backend/Database/apply.sql'),
    ],
    options: {
      cwd: projectRoot,
      env: deploymentEnvironment,
      shell: false,
      stdio: 'inherit',
    },
    processRef,
    spawnImpl,
  });
}

export async function runDatabaseDeploymentCli({
  processRef = process,
  runDatabaseDeploymentImpl = runDatabaseDeployment,
} = {}) {
  try {
    const { code, signal } = await runDatabaseDeploymentImpl({ processRef });
    if (signal !== null) {
      processRef.kill(processRef.pid, signal);
      return;
    }
    processRef.exitCode = code ?? 1;
  } catch (error) {
    processRef.stderr?.write(
      `${error instanceof Error ? error.message : 'Database deployment failed.'}\n`,
    );
    processRef.exitCode = 1;
  }
}

const isCli =
  process.argv[1] !== undefined &&
  pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;

if (isCli) {
  await runDatabaseDeploymentCli();
}
