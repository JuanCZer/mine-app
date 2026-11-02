import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseEnv } from 'node:util';

const modulePath = fileURLToPath(import.meta.url);
const defaultProjectRoot = path.resolve(path.dirname(modulePath), '..');
const runtimeConnectionName = 'ConnectionStrings__Postgres';
const identityAdminConnectionName = 'ConnectionStrings__IdentityAdmin';
const rateLimitKeyEnvironmentPattern = /^IdentitySecurity__RateLimitHmacKeys__\d+__(?:KeyId|SecretBase64)$/;
const forbiddenIdentitySelectors = new Set([
  'IdentitySecurity__CurrentKeyId',
  'IdentitySecurity__ActiveKeyId',
  'IdentitySecurity__RateLimitHmacKeyId',
  'IdentitySecurity__Current',
  'IdentitySecurity__Active',
]);
const deploymentConnectionName = 'POSTGRES_DEPLOY_URL';
const deploymentConnectionNameLower = deploymentConnectionName.toLowerCase();

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

export async function buildBackendEnvironment({
  projectRoot = defaultProjectRoot,
  env = process.env,
  requireIdentitySecurity = false,
} = {}) {
  const fileEnvironment = await readBackendEnvironment(projectRoot);
  const inheritedEnvironment = Object.fromEntries(
    Object.entries(env).filter(
      ([key, value]) => value !== undefined && key.toLowerCase() !== deploymentConnectionNameLower,
    ),
  );

  const connection = Object.hasOwn(inheritedEnvironment, runtimeConnectionName)
    ? inheritedEnvironment[runtimeConnectionName]
    : fileEnvironment[runtimeConnectionName];

  if (typeof connection !== 'string' || connection.trim() === '') {
    throw new Error('ConnectionStrings__Postgres is required.');
  }

  const identityAdminConnection = Object.hasOwn(inheritedEnvironment, identityAdminConnectionName)
    ? inheritedEnvironment[identityAdminConnectionName]
    : fileEnvironment[identityAdminConnectionName];
  const backendEnvironment = {
    ...inheritedEnvironment,
    [runtimeConnectionName]: connection,
  };
  if (typeof identityAdminConnection === 'string' && identityAdminConnection.trim() !== '') {
    backendEnvironment[identityAdminConnectionName] = identityAdminConnection;
  }
  if ([...forbiddenIdentitySelectors].some((key) => Object.hasOwn(fileEnvironment, key))) {
    throw new Error('Identity security configuration is invalid.');
  }
  for (const [key, value] of Object.entries(fileEnvironment)) {
    if (rateLimitKeyEnvironmentPattern.test(key) && !Object.hasOwn(backendEnvironment, key)) {
      backendEnvironment[key] = value;
    }
  }
  if (requireIdentitySecurity) {
    if (!(identityAdminConnectionName in backendEnvironment)) {
      throw new Error('ConnectionStrings__IdentityAdmin is required.');
    }
    const configuredKeyFields = Object.keys(backendEnvironment).filter((key) =>
      rateLimitKeyEnvironmentPattern.test(key),
    );
    if (!configuredKeyFields.some((key) => key.endsWith('__KeyId'))
        || !configuredKeyFields.some((key) => key.endsWith('__SecretBase64'))) {
      throw new Error('IdentitySecurity__RateLimitHmacKeys is required.');
    }
  }
  return backendEnvironment;
}

export function spawnWithSignals({
  command,
  args,
  options,
  processRef = process,
  spawnImpl = spawn,
}) {
  return new Promise((resolve, reject) => {
    let child;
    let pendingSignal = null;
    const signalHandlers = new Map(
      ['SIGINT', 'SIGTERM'].map((signal) => [
        signal,
        () => {
          if (child === undefined) {
            pendingSignal ??= signal;
            return;
          }
          child.kill(signal);
        },
      ]),
    );
    const cleanup = () => {
      for (const [signal, handler] of signalHandlers) {
        processRef.off(signal, handler);
      }
    };
    let settled = false;
    const settle = (callback) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      callback();
    };

    for (const [signal, handler] of signalHandlers) {
      processRef.on(signal, handler);
    }

    try {
      child = spawnImpl(command, args, options);
    } catch (error) {
      settle(() => reject(error));
      return;
    }

    child.once('error', (error) => settle(() => reject(error)));
    child.once('close', (code, signal) => settle(() => resolve({ code, signal })));

    if (pendingSignal !== null) {
      child.kill(pendingSignal);
    }
  });
}

export async function runBackend({
  projectRoot = defaultProjectRoot,
  env = process.env,
  processRef = process,
  spawnImpl = spawn,
  requireIdentitySecurity = false,
} = {}) {
  const backendEnvironment = await buildBackendEnvironment({ projectRoot, env, requireIdentitySecurity });
  return spawnWithSignals({
    command: 'dotnet',
    args: ['run', '--project', 'backend'],
    options: {
      cwd: projectRoot,
      env: backendEnvironment,
      shell: false,
      stdio: 'inherit',
    },
    processRef,
    spawnImpl,
  });
}

export async function runBackendCli({ processRef = process, runBackendImpl = runBackend } = {}) {
  try {
    const { code, signal } = await runBackendImpl({ processRef, requireIdentitySecurity: true });
    if (signal !== null) {
      processRef.kill(processRef.pid, signal);
      return;
    }
    processRef.exitCode = code ?? 1;
  } catch (error) {
    processRef.stderr?.write(
      `${error instanceof Error ? error.message : 'Backend startup failed.'}\n`,
    );
    processRef.exitCode = 1;
  }
}

const isCli =
  process.argv[1] !== undefined &&
  pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url;

if (isCli) {
  await runBackendCli();
}
