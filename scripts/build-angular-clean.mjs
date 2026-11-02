import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ANGULAR_WARNING_PATTERN = /▲ \[WARNING\]/i;
const BUDGET_WARNING_PATTERN = /exceeded maximum budget/i;

export function buildAngularClean({
  projectRoot = process.cwd(),
  env = process.env,
  spawnImpl = spawn,
} = {}) {
  const angularCliPath = path.resolve(projectRoot, 'node_modules/@angular/cli/bin/ng.js');

  return new Promise((resolve, reject) => {
    const child = spawnImpl(
      process.execPath,
      [angularCliPath, 'build', '--configuration', 'production'],
      {
        cwd: projectRoot,
        env,
        shell: false,
        stdio: ['ignore', 'pipe', 'pipe'],
      },
    );

    let stdout = '';
    let stderr = '';

    child.stdout?.on('data', (chunk) => {
      stdout += String(chunk);
    });
    child.stderr?.on('data', (chunk) => {
      stderr += String(chunk);
    });

    child.on('error', (error) => {
      error.stdout = stdout;
      error.stderr = stderr;
      reject(error);
    });

    child.on('close', (code, signal) => {
      const combinedOutput = `${stdout}\n${stderr}`;
      const warningDetected =
        ANGULAR_WARNING_PATTERN.test(combinedOutput) || BUDGET_WARNING_PATTERN.test(combinedOutput);

      if (signal) {
        const error = new Error(`Angular build terminated by signal ${signal}.`);
        error.exitCode = 1;
        error.signal = signal;
        error.stdout = stdout;
        error.stderr = stderr;
        return reject(error);
      }

      if (code !== 0) {
        const error = new Error(`Angular build exited with code ${code}.`);
        error.exitCode = code;
        error.stdout = stdout;
        error.stderr = stderr;
        return reject(error);
      }

      if (warningDetected) {
        const error = new Error(
          'Angular production build failed because the CLI emitted a warning marker or exceeded the maximum budget.',
        );
        error.exitCode = 1;
        error.stdout = stdout;
        error.stderr = stderr;
        return reject(error);
      }

      resolve({ code, signal, stdout, stderr });
    });
  });
}

export async function runBuildAngularClean(options = {}) {
  try {
    const result = await buildAngularClean(options);
    if (result.stdout) process.stdout.write(result.stdout);
    if (result.stderr) process.stderr.write(result.stderr);
    return result;
  } catch (error) {
    if (error.stdout) process.stdout.write(error.stdout);
    if (error.stderr) process.stderr.write(error.stderr);
    const exitCode = error.exitCode ?? 1;
    process.exit(exitCode);
  }
}

const directExecution =
  process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]);

if (directExecution) {
  runBuildAngularClean({ projectRoot: process.cwd() });
}
