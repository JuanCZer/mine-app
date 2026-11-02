import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { fileURLToPath } from 'node:url';
import {
  link,
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test, { afterEach } from 'node:test';

const generatorModuleUrl = new URL('../generate-public-env.mjs', import.meta.url);
const backendModuleUrl = new URL('../run-backend.mjs', import.meta.url);
const databaseDeploymentModuleUrl = new URL('../apply-database.mjs', import.meta.url);
const temporaryDirectories = [];

afterEach(async () => {
  await Promise.all(
    temporaryDirectories
      .splice(0)
      .map((directory) => rm(directory, { recursive: true, force: true })),
  );
});

async function createProjectFixture() {
  const projectRoot = await mkdtemp(path.join(tmpdir(), 'mine-app-environment-'));
  temporaryDirectories.push(projectRoot);
  await mkdir(path.join(projectRoot, 'src/app/core/config'), { recursive: true });
  await mkdir(path.join(projectRoot, 'backend'), { recursive: true });
  return projectRoot;
}

async function generatedToken(projectRoot) {
  const source = await readFile(
    path.join(projectRoot, 'src/app/core/config/public-env.generated.ts'),
    'utf8',
  );
  const match = source.match(/mapboxPublicToken: (.+),$/m);
  assert.ok(match, 'generated config contains a JSON token literal');
  return { source, token: JSON.parse(match[1]) };
}

async function loadGenerator() {
  return import(generatorModuleUrl.href);
}

async function loadBackendRunner() {
  return import(backendModuleUrl.href);
}

async function loadDatabaseDeployment() {
  return import(databaseDeploymentModuleUrl.href);
}

function secureDeploymentUrl(label = 'selected') {
  return `postgresql://deployer:synthetic-${label}-password@db.internal.example:5432/gds?sslmode=verify-full`;
}

test('missingPublicTokenFailsWithoutValue', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();

  await assert.rejects(
    generatePublicConfig({ projectRoot, env: {} }),
    /MAPBOX_PUBLIC_TOKEN is required/,
  );
});

test('secretMapboxTokenIsRejected', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();
  const rejectedValue = 'sk.synthetic-rejected-value';

  await assert.rejects(
    generatePublicConfig({
      projectRoot,
      env: { MAPBOX_PUBLIC_TOKEN: rejectedValue },
    }),
    (error) => {
      assert.match(error.message, /valid public Mapbox token/);
      assert.equal(error.message.includes(rejectedValue), false);
      return true;
    },
  );
});

test('nonPublicMapboxTokenIsRejected', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();

  await assert.rejects(
    generatePublicConfig({
      projectRoot,
      env: { MAPBOX_PUBLIC_TOKEN: 'synthetic-invalid-value' },
    }),
    /valid public Mapbox token/,
  );
});

test('malformedPublicMapboxTokensAreRejected', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();
  const malformedTokens = [
    'pk.',
    'pk.only-one-segment',
    'pk..missing-payload',
    'pk.missing-signature.',
    'pk.invalid+character.signature',
    'pk.too.many.segments',
  ];

  for (const token of malformedTokens) {
    await assert.rejects(
      generatePublicConfig({
        projectRoot,
        env: { MAPBOX_PUBLIC_TOKEN: token },
      }),
      (error) => {
        assert.match(error.message, /valid public Mapbox token/);
        assert.equal(error.message.includes(token), false);
        return true;
      },
    );
  }
});

test('blankPublicTokenIsRejected', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();

  await assert.rejects(
    generatePublicConfig({
      projectRoot,
      env: { MAPBOX_PUBLIC_TOKEN: '   ' },
    }),
    /MAPBOX_PUBLIC_TOKEN is required/,
  );
});

test('privateSentinelNeverAppearsInGeneratedOutput', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();
  const privateSentinel = 'PRIVATE_SENTINEL_FOR_ALLOWLIST_TEST';
  await writeFile(
    path.join(projectRoot, '.env'),
    `MAPBOX_PUBLIC_TOKEN=pk.synthetic_payload.file_signature\nConnectionStrings__Postgres=${privateSentinel}\n`,
  );

  await generatePublicConfig({
    projectRoot,
    env: {
      MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.injected_signature',
      PRIVATE_BUILD_SETTING: privateSentinel,
    },
  });

  const { source } = await generatedToken(projectRoot);
  assert.equal(source.includes(privateSentinel), false);
  assert.equal(source.includes('ConnectionStrings__Postgres'), false);
  assert.equal(source.includes('PRIVATE_BUILD_SETTING'), false);
});

test('tokensWithSyntaxCharactersAreRejectedWithoutDisclosure', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();
  const tokenWithSyntaxCharacters = 'pk.synthetic";\nthrow new Error("not data");\\tail';

  await assert.rejects(
    generatePublicConfig({
      projectRoot,
      env: { MAPBOX_PUBLIC_TOKEN: tokenWithSyntaxCharacters },
    }),
    (error) => {
      assert.match(error.message, /valid public Mapbox token/);
      assert.equal(error.message.includes(tokenWithSyntaxCharacters), false);
      return true;
    },
  );
});

test('injectedPublicVariableWins', async () => {
  const projectRoot = await createProjectFixture();
  const { generatePublicConfig } = await loadGenerator();
  await writeFile(
    path.join(projectRoot, '.env'),
    'MAPBOX_PUBLIC_TOKEN=pk.synthetic_payload.file_signature\n',
  );

  await generatePublicConfig({
    projectRoot,
    env: { MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.injected_signature' },
  });

  const { token } = await generatedToken(projectRoot);
  assert.equal(token, 'pk.synthetic_payload.injected_signature');
});

test('worksOutsideProjectCwd', async () => {
  const projectRoot = await createProjectFixture();
  const unrelatedDirectory = await mkdtemp(path.join(tmpdir(), 'mine-app-cwd-'));
  temporaryDirectories.push(unrelatedDirectory);
  const { generatePublicConfig } = await loadGenerator();
  await writeFile(
    path.join(projectRoot, '.env'),
    'MAPBOX_PUBLIC_TOKEN=pk.synthetic_payload.project_signature\n',
  );
  const originalCwd = process.cwd();

  try {
    process.chdir(unrelatedDirectory);
    await generatePublicConfig({ projectRoot, env: {} });
  } finally {
    process.chdir(originalCwd);
  }

  const { token } = await generatedToken(projectRoot);
  assert.equal(token, 'pk.synthetic_payload.project_signature');
});

test('generatedConfigReplacesSymlinkWithoutModifyingExternalTarget', async () => {
  const projectRoot = await createProjectFixture();
  const externalDirectory = await mkdtemp(path.join(tmpdir(), 'mine-app-external-'));
  temporaryDirectories.push(externalDirectory);
  const externalTarget = path.join(externalDirectory, 'external-target.ts');
  const destination = path.join(projectRoot, 'src/app/core/config/public-env.generated.ts');
  await writeFile(externalTarget, 'external target must remain unchanged\n');
  await symlink(externalTarget, destination);
  const { generatePublicConfig } = await loadGenerator();

  await generatePublicConfig({
    projectRoot,
    env: { MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.symlink_signature' },
  });

  assert.equal(await readFile(externalTarget, 'utf8'), 'external target must remain unchanged\n');
  const destinationState = await lstat(destination);
  assert.equal(destinationState.isSymbolicLink(), false);
  assert.equal(destinationState.isFile(), true);
  assert.equal((await generatedToken(projectRoot)).token, 'pk.synthetic_payload.symlink_signature');
});

test('generatedConfigReplacesHardLinkWithoutModifyingExternalTarget', async () => {
  const projectRoot = await createProjectFixture();
  const externalDirectory = await mkdtemp(path.join(tmpdir(), 'mine-app-external-'));
  temporaryDirectories.push(externalDirectory);
  const externalTarget = path.join(externalDirectory, 'external-target.ts');
  const destination = path.join(projectRoot, 'src/app/core/config/public-env.generated.ts');
  await writeFile(externalTarget, 'external hard-link target must remain unchanged\n');
  await link(externalTarget, destination);
  const { generatePublicConfig } = await loadGenerator();

  await generatePublicConfig({
    projectRoot,
    env: { MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.hardlink_signature' },
  });

  assert.equal(
    await readFile(externalTarget, 'utf8'),
    'external hard-link target must remain unchanged\n',
  );
  assert.equal(
    (await generatedToken(projectRoot)).token,
    'pk.synthetic_payload.hardlink_signature',
  );
});

test('failedGeneratedConfigReplacementCleansTemporaryFile', async () => {
  const projectRoot = await createProjectFixture();
  const configDirectory = path.join(projectRoot, 'src/app/core/config');
  const destination = path.join(configDirectory, 'public-env.generated.ts');
  await mkdir(destination);
  const { generatePublicConfig } = await loadGenerator();

  await assert.rejects(
    generatePublicConfig({
      projectRoot,
      env: { MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.cleanup_signature' },
    }),
  );

  assert.deepEqual(await readdir(configDirectory), ['public-env.generated.ts']);
});

test('repeatedGenerationProducesIdenticalContents', async () => {
  const projectRoot = await createProjectFixture();
  const destination = path.join(projectRoot, 'src/app/core/config/public-env.generated.ts');
  const { generatePublicConfig } = await loadGenerator();
  const options = {
    projectRoot,
    env: { MAPBOX_PUBLIC_TOKEN: 'pk.synthetic_payload.repeat_signature' },
  };

  await generatePublicConfig(options);
  const first = await readFile(destination, 'utf8');
  await generatePublicConfig(options);

  assert.equal(await readFile(destination, 'utf8'), first);
});

test('missingBackendConnectionFailsWithoutValue', async () => {
  const projectRoot = await createProjectFixture();
  const { buildBackendEnvironment } = await loadBackendRunner();

  await assert.rejects(
    buildBackendEnvironment({ projectRoot, env: {} }),
    /ConnectionStrings__Postgres is required/,
  );
});

test('backendConnectionPreservesSpacesAndSpecialCharacters', async () => {
  const projectRoot = await createProjectFixture();
  const { buildBackendEnvironment } = await loadBackendRunner();
  await writeFile(
    path.join(projectRoot, 'backend/.env'),
    'ConnectionStrings__Postgres="Host=db;Password=value with spaces # and &;SSL Mode=Require"\n',
  );

  const result = await buildBackendEnvironment({ projectRoot, env: {} });

  assert.equal(
    result.ConnectionStrings__Postgres,
    'Host=db;Password=value with spaces # and &;SSL Mode=Require',
  );
});

test('injectedBackendConnectionWins', async () => {
  const projectRoot = await createProjectFixture();
  const { buildBackendEnvironment } = await loadBackendRunner();
  await writeFile(
    path.join(projectRoot, 'backend/.env'),
    'ConnectionStrings__Postgres=synthetic-file-connection\n',
  );

  const result = await buildBackendEnvironment({
    projectRoot,
    env: { ConnectionStrings__Postgres: 'synthetic-injected-connection' },
  });

  assert.equal(result.ConnectionStrings__Postgres, 'synthetic-injected-connection');
});

test('rootEnvironmentIsNeverReadByBackendLoader', async () => {
  const projectRoot = await createProjectFixture();
  const { buildBackendEnvironment } = await loadBackendRunner();
  await writeFile(
    path.join(projectRoot, '.env'),
    'ConnectionStrings__Postgres=synthetic-root-connection\n',
  );

  await assert.rejects(
    buildBackendEnvironment({ projectRoot, env: {} }),
    /ConnectionStrings__Postgres is required/,
  );
});

test('deploymentCredentialNeverReachesBackendChildEnvironment', async () => {
  const projectRoot = await createProjectFixture();
  const { buildBackendEnvironment } = await loadBackendRunner();
  await writeFile(
    path.join(projectRoot, 'backend/.env'),
    [
      'ConnectionStrings__Postgres=synthetic-runtime-connection',
      'POSTGRES_DEPLOY_URL=synthetic-file-deploy-credential',
      'UNEXPECTED_FILE_SETTING=synthetic-file-setting',
      '',
    ].join('\n'),
  );

  const result = await buildBackendEnvironment({
    projectRoot,
    env: {
      PATH: '/synthetic/bin',
      POSTGRES_DEPLOY_URL: 'synthetic-injected-deploy-credential',
      Postgres_Deploy_Url: 'synthetic-case-variant-deploy-credential',
    },
  });

  assert.equal(result.ConnectionStrings__Postgres, 'synthetic-runtime-connection');
  assert.equal(result.PATH, '/synthetic/bin');
  assert.equal('POSTGRES_DEPLOY_URL' in result, false);
  assert.equal(
    Object.keys(result).some((key) => key.toLowerCase() === 'postgres_deploy_url'),
    false,
  );
  assert.equal('UNEXPECTED_FILE_SETTING' in result, false);
});

test('backendRunnerUsesArgumentArrayWithoutLoggingSecrets', async () => {
  const projectRoot = await createProjectFixture();
  const { runBackend } = await loadBackendRunner();
  const child = new EventEmitter();
  child.kill = () => true;
  const calls = [];
  const processRef = new EventEmitter();
  const standardOutput = [];
  const standardError = [];
  processRef.stdout = { write: (value) => standardOutput.push(value) };
  processRef.stderr = { write: (value) => standardError.push(value) };
  const runtimeValue = 'synthetic-runtime-value-never-logged';

  const completion = runBackend({
    projectRoot,
    env: { ConnectionStrings__Postgres: runtimeValue },
    processRef,
    spawnImpl(command, args, options) {
      calls.push({ command, args, options });
      queueMicrotask(() => child.emit('close', 0, null));
      return child;
    },
  });

  assert.deepEqual(await completion, { code: 0, signal: null });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].command, 'dotnet');
  assert.deepEqual(calls[0].args, ['run', '--project', 'backend']);
  assert.equal(calls[0].options.cwd, projectRoot);
  assert.equal(calls[0].options.shell, false);
  assert.equal(calls[0].options.stdio, 'inherit');
  assert.equal(calls[0].args.join(' ').includes(runtimeValue), false);
  assert.equal(standardOutput.join('').includes(runtimeValue), false);
  assert.equal(standardError.join('').includes(runtimeValue), false);
});

test('backendRunnerPreservesExitCode', async () => {
  const projectRoot = await createProjectFixture();
  const { runBackend } = await loadBackendRunner();
  const child = new EventEmitter();
  child.kill = () => true;

  const completion = runBackend({
    projectRoot,
    env: { ConnectionStrings__Postgres: 'synthetic-runtime-connection' },
    processRef: new EventEmitter(),
    spawnImpl() {
      queueMicrotask(() => child.emit('close', 23, null));
      return child;
    },
  });

  assert.deepEqual(await completion, { code: 23, signal: null });
});

test('backendRunnerForwardsSignalsAndPreservesChildSignal', async () => {
  const projectRoot = await createProjectFixture();
  const { runBackend } = await loadBackendRunner();
  const child = new EventEmitter();
  const forwardedSignals = [];
  child.kill = (signal) => {
    forwardedSignals.push(signal);
    queueMicrotask(() => child.emit('close', null, signal));
    return true;
  };
  const processRef = new EventEmitter();

  const completion = runBackend({
    projectRoot,
    env: { ConnectionStrings__Postgres: 'synthetic-runtime-connection' },
    processRef,
    spawnImpl() {
      queueMicrotask(() => processRef.emit('SIGTERM'));
      return child;
    },
  });

  assert.deepEqual(await completion, { code: null, signal: 'SIGTERM' });
  assert.deepEqual(forwardedSignals, ['SIGTERM']);
  assert.equal(processRef.listenerCount('SIGINT'), 0);
  assert.equal(processRef.listenerCount('SIGTERM'), 0);
});

test('backendRunnerHandlesSignalEmittedSynchronouslyDuringSpawn', async () => {
  const projectRoot = await createProjectFixture();
  const { runBackend } = await loadBackendRunner();
  const child = new EventEmitter();
  let signalForwarded = false;
  child.kill = (signal) => {
    signalForwarded = true;
    queueMicrotask(() => child.emit('close', null, signal));
    return true;
  };
  const processRef = new EventEmitter();

  const completion = runBackend({
    projectRoot,
    env: { ConnectionStrings__Postgres: 'synthetic-runtime-connection' },
    processRef,
    spawnImpl() {
      processRef.emit('SIGINT');
      queueMicrotask(() => {
        if (!signalForwarded) child.emit('close', 0, null);
      });
      return child;
    },
  });

  assert.deepEqual(await completion, { code: null, signal: 'SIGINT' });
  assert.equal(processRef.listenerCount('SIGINT'), 0);
  assert.equal(processRef.listenerCount('SIGTERM'), 0);
});

test('backendRunnerCleansSignalHandlersWhenSpawnThrows', async () => {
  const projectRoot = await createProjectFixture();
  const { runBackend } = await loadBackendRunner();
  const processRef = new EventEmitter();

  await assert.rejects(
    runBackend({
      projectRoot,
      env: { ConnectionStrings__Postgres: 'synthetic-runtime-connection' },
      processRef,
      spawnImpl() {
        throw new Error('synthetic spawn failure');
      },
    }),
    /synthetic spawn failure/,
  );
  assert.equal(processRef.listenerCount('SIGINT'), 0);
  assert.equal(processRef.listenerCount('SIGTERM'), 0);
});

test('backendCliAppliesNumericAndSignalTermination', async () => {
  const { runBackendCli } = await loadBackendRunner();
  const numericProcess = {
    exitCode: undefined,
    pid: 101,
    kill() {
      assert.fail('numeric exit must not signal the process');
    },
  };
  await runBackendCli({
    processRef: numericProcess,
    runBackendImpl: async () => ({ code: 7, signal: null }),
  });
  assert.equal(numericProcess.exitCode, 7);

  const signals = [];
  const signaledProcess = {
    exitCode: undefined,
    pid: 202,
    kill(pid, signal) {
      signals.push({ pid, signal });
    },
  };
  await runBackendCli({
    processRef: signaledProcess,
    runBackendImpl: async () => ({ code: null, signal: 'SIGINT' }),
  });
  assert.deepEqual(signals, [{ pid: 202, signal: 'SIGINT' }]);
  assert.equal(signaledProcess.exitCode, undefined);
});

test('missingDeploymentConnectionFailsWithoutValue', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();

  await assert.rejects(
    buildDatabaseDeploymentEnvironment({ projectRoot, env: {} }),
    /POSTGRES_DEPLOY_URL is required/,
  );
});

test('injectedDeploymentConnectionWinsWithoutReadingRootEnvironment', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();
  await writeFile(
    path.join(projectRoot, '.env'),
    'POSTGRES_DEPLOY_URL=synthetic-root-deploy-connection\n',
  );
  await writeFile(
    path.join(projectRoot, 'backend/.env'),
    'POSTGRES_DEPLOY_URL=synthetic-file-deploy-connection\n',
  );

  const deploymentValue = secureDeploymentUrl('injected');
  const result = await buildDatabaseDeploymentEnvironment({
    projectRoot,
    env: {
      POSTGRES_DEPLOY_URL: deploymentValue,
      ConnectionStrings__Postgres: 'synthetic-runtime-connection',
    },
  });

  assert.equal(result.PGDATABASE, deploymentValue);
  assert.equal(
    Object.keys(result).some((key) => key.toLowerCase() === 'postgres_deploy_url'),
    false,
  );
  assert.equal(
    Object.keys(result).some((key) => key.toLowerCase() === 'connectionstrings__postgres'),
    false,
  );
});

test('rootEnvironmentIsNeverReadByDatabaseDeployment', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();
  await writeFile(
    path.join(projectRoot, '.env'),
    'POSTGRES_DEPLOY_URL=synthetic-root-deploy-connection\n',
  );

  await assert.rejects(
    buildDatabaseDeploymentEnvironment({ projectRoot, env: {} }),
    /POSTGRES_DEPLOY_URL is required/,
  );
});

test('databaseDeploymentRemovesConflictingConnectionEnvironmentCaseInsensitively', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();
  const deploymentValue = secureDeploymentUrl();
  const result = await buildDatabaseDeploymentEnvironment({
    projectRoot,
    env: {
      PATH: '/synthetic/bin',
      HOME: '/synthetic/home',
      LANG: 'synthetic_LOCALE.UTF-8',
      POSTGRES_DEPLOY_URL: deploymentValue,
      Postgres_Deploy_Url: 'synthetic-conflicting-deploy-connection',
      PGHOST: 'synthetic-host',
      PgPassword: 'synthetic-password',
      pgservice: 'synthetic-service',
      PgOptions: 'synthetic-options',
      ConnectionStrings__Postgres: 'synthetic-runtime-connection',
      connectionstrings__Catalogs: 'synthetic-catalog-connection',
    },
  });

  assert.equal(result.PGDATABASE, deploymentValue);
  assert.equal(result.PATH, '/synthetic/bin');
  assert.equal(result.HOME, '/synthetic/home');
  assert.equal(result.LANG, 'synthetic_LOCALE.UTF-8');
  assert.deepEqual(
    Object.keys(result).filter((key) => {
      const normalized = key.toLowerCase();
      return (
        (normalized.startsWith('pg') && normalized !== 'pgdatabase') ||
        normalized.startsWith('connectionstrings__') ||
        normalized === 'postgres_deploy_url'
      );
    }),
    [],
  );
});

test('databaseDeploymentUsesEnvironmentInsteadOfArgumentsOrLogs', async () => {
  const projectRoot = await createProjectFixture();
  const { runDatabaseDeployment } = await loadDatabaseDeployment();
  await mkdir(path.join(projectRoot, 'backend/Database'), { recursive: true });
  await writeFile(path.join(projectRoot, 'backend/Database/apply.sql'), 'select 1;\n');
  const child = new EventEmitter();
  child.kill = () => true;
  const calls = [];
  const processRef = new EventEmitter();
  const standardOutput = [];
  const standardError = [];
  processRef.stdout = { write: (value) => standardOutput.push(value) };
  processRef.stderr = { write: (value) => standardError.push(value) };
  const deploymentValue = secureDeploymentUrl('never-logged');

  const completion = runDatabaseDeployment({
    projectRoot,
    env: { POSTGRES_DEPLOY_URL: deploymentValue },
    processRef,
    spawnImpl(command, args, options) {
      calls.push({ command, args, options });
      queueMicrotask(() => child.emit('close', 0, null));
      return child;
    },
  });

  assert.deepEqual(await completion, { code: 0, signal: null });
  assert.equal(calls.length, 1);
  assert.equal(calls[0].command, 'psql');
  assert.deepEqual(calls[0].args, [
    '--set',
    'ON_ERROR_STOP=on',
    '--file',
    path.join(projectRoot, 'backend/Database/apply.sql'),
  ]);
  assert.equal(calls[0].options.shell, false);
  assert.equal(calls[0].options.stdio, 'inherit');
  assert.equal(calls[0].options.env.PGDATABASE, deploymentValue);
  assert.equal(calls[0].args.join(' ').includes(deploymentValue), false);
  assert.equal(standardOutput.join('').includes(deploymentValue), false);
  assert.equal(standardError.join('').includes(deploymentValue), false);
});

test('databaseDeploymentRequiresVerifyFullWithoutDisclosingRejectedValues', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();
  const rejectedValues = [
    'postgresql://deployer:secret-one@db.internal.example/gds',
    'postgresql://deployer:secret-two@db.internal.example/gds?sslmode=require',
    'postgresql://deployer:secret-three@db.internal.example/gds?sslmode=prefer',
    'postgresql://deployer:secret-four@db.internal.example/gds?sslmode=disable',
    'postgresql://deployer:secret-five@db.internal.example/gds?sslmode=verify-ca',
    'postgresql://deployer:secret-six@db.internal.example/gds?sslmode=verify-full&sslmode=require',
    'postgresql://deployer:secret-seven@db.internal.example/gds?sslmode=verify-full&SSLMode=require',
    'postgresql://deployer:secret-eight@db.internal.example/gds?sslmode=verify-\nfull',
    ' postgresql://deployer:secret-nine@db.internal.example/gds?sslmode=verify-full',
    'Host=db.internal.example;Database=gds;Ssl Mode=VerifyFull',
  ];

  for (const rejectedValue of rejectedValues) {
    await assert.rejects(
      buildDatabaseDeploymentEnvironment({
        projectRoot,
        env: { POSTGRES_DEPLOY_URL: rejectedValue },
      }),
      (error) => {
        assert.match(error.message, /valid PostgreSQL URL with sslmode=verify-full/);
        assert.equal(error.message.includes(rejectedValue), false);
        assert.equal(error.message.includes('secret-'), false);
        return true;
      },
    );
  }
});

test('databaseDeploymentRejectsInsecureUrlBeforeSpawn', async () => {
  const projectRoot = await createProjectFixture();
  const { runDatabaseDeployment } = await loadDatabaseDeployment();
  let spawned = false;

  await assert.rejects(
    runDatabaseDeployment({
      projectRoot,
      env: {
        POSTGRES_DEPLOY_URL:
          'postgresql://deployer:never-disclose@db.internal.example/gds?sslmode=require',
      },
      spawnImpl() {
        spawned = true;
        assert.fail('psql must not start for an unverified deployment URL');
      },
    }),
    /valid PostgreSQL URL with sslmode=verify-full/,
  );
  assert.equal(spawned, false);
});

test('databaseDeploymentPreservesVerifiedUrlAndRootCertificate', async () => {
  const projectRoot = await createProjectFixture();
  const { buildDatabaseDeploymentEnvironment } = await loadDatabaseDeployment();
  const deploymentValue =
    'postgresql://deployer:synthetic-password@db.internal.example:5432/gds?sslmode=verify-full&sslrootcert=%2Fetc%2Fgds%2Fpostgres-ca.pem';

  const result = await buildDatabaseDeploymentEnvironment({
    projectRoot,
    env: { POSTGRES_DEPLOY_URL: deploymentValue },
  });

  assert.equal(result.PGDATABASE, deploymentValue);
});

test('documentation_requires_both_sql_paths_bootstrap_and_rollout_order', async () => {
  const workspaceRoot = fileURLToPath(new URL('../..', import.meta.url));
  const documentation = await readFile(path.join(workspaceRoot, 'README.md'), 'utf8');
  const runbook = await readFile(
    path.join(workspaceRoot, 'docs/runbooks/postgresql-cutover.md'),
    'utf8',
  );

  assert.match(documentation, /build:clean|npm run build:clean/i);
  assert.match(runbook, /bootstrap|rollout/i);
});

test('progress_documents_require_user_administration_objective_and_qa_matrix', async () => {
  const workspaceRoot = fileURLToPath(new URL('../..', import.meta.url));
  const generalPlan = await readFile(
    path.join(workspaceRoot, 'docs/superpowers/plans/2026-09-27-plan-general.md'),
    'utf8',
  );
  const statusAudit = await readFile(
    path.join(workspaceRoot, 'docs/superpowers/plans/2026-10-15-plan-status-audit.md'),
    'utf8',
  );
  const matrix = await readFile(
    path.join(workspaceRoot, 'docs/qa/user-administration/test-matrix.md'),
    'utf8',
  ).catch(() => '');

  assert.match(generalPlan, /User Administration|user administration|Task 19/i);
  assert.match(statusAudit, /User Administration|user administration|Task 19/i);
  assert.match(matrix, /QA|accessibility|responsive/i);
});

test('angular_clean_build_uses_production_configuration_shell_false_and_propagates_child_exit', async () => {
  const { buildAngularClean } = await import('../build-angular-clean.mjs');
  const captured = { calls: [] };
  const exitCode = 17;
  const failures = [];

  await assert.rejects(
    buildAngularClean({
      projectRoot: '/workspace/project',
      env: { HOME: '/tmp' },
      spawnImpl(command, args, options) {
        captured.calls.push({ command, args, options });
        const child = new EventEmitter();
        child.stdout = new EventEmitter();
        child.stderr = new EventEmitter();
        child.stdout.write = (chunk) => {
          failures.push(String(chunk));
          return true;
        };
        child.stderr.write = (chunk) => {
          failures.push(String(chunk));
          return true;
        };
        queueMicrotask(() => child.emit('close', exitCode, null));
        return child;
      },
    }),
    (error) => {
      assert.equal(error.exitCode, exitCode);
      return true;
    },
  );

  assert.equal(captured.calls.length, 1);
  assert.equal(captured.calls[0].command, process.execPath);
  assert.match(captured.calls[0].args[0], /@angular[\\/]cli[\\/]bin[\\/]ng\.js$/);
  assert.deepEqual(captured.calls[0].args.slice(1, 4), ['build', '--configuration', 'production']);
  assert.equal(captured.calls[0].options.shell, false);
  assert.equal(failures.length, 0);
});

test('angular_clean_build_fails_on_angular_warning_marker', async () => {
  const { buildAngularClean } = await import('../build-angular-clean.mjs');

  await assert.rejects(
    buildAngularClean({
      projectRoot: '/workspace/project',
      env: { HOME: '/tmp' },
      spawnImpl(command, args, options) {
        const child = new EventEmitter();
        child.stdout = new EventEmitter();
        child.stderr = new EventEmitter();
        child.stdout.write = () => true;
        child.stderr.write = () => true;
        queueMicrotask(() => {
          child.stdout.emit('data', '▲ [WARNING] budget exceeded');
          child.emit('close', 0, null);
        });
        return child;
      },
    }),
    (error) => {
      assert.match(error.message, /warning|budget/i);
      return true;
    },
  );
});

test('angular_clean_build_fails_on_budget_exceeded_case_insensitive', async () => {
  const { buildAngularClean } = await import('../build-angular-clean.mjs');

  await assert.rejects(
    buildAngularClean({
      projectRoot: '/workspace/project',
      env: { HOME: '/tmp' },
      spawnImpl(command, args, options) {
        const child = new EventEmitter();
        child.stdout = new EventEmitter();
        child.stderr = new EventEmitter();
        child.stdout.write = () => true;
        child.stderr.write = () => true;
        queueMicrotask(() => {
          child.stderr.emit('data', 'ERROR: exceeded maximum budget');
          child.emit('close', 0, null);
        });
        return child;
      },
    }),
    (error) => {
      assert.match(error.message, /maximum budget/i);
      return true;
    },
  );
});

test('angular_clean_build_ignores_unrelated_warning_word', async () => {
  const { buildAngularClean } = await import('../build-angular-clean.mjs');

  const result = await buildAngularClean({
    projectRoot: '/workspace/project',
    env: { HOME: '/tmp' },
    spawnImpl(command, args, options) {
      const child = new EventEmitter();
      child.stdout = new EventEmitter();
      child.stderr = new EventEmitter();
      child.stdout.write = () => true;
      child.stderr.write = () => true;
      queueMicrotask(() => {
        child.stdout.emit('data', 'Everything fine, no expensive warning in this line');
        child.emit('close', 0, null);
      });
      return child;
    },
  });

  assert.equal(result.code, 0);
});
