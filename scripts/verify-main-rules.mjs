import { readFileSync } from 'node:fs';

try {
  const value = JSON.parse(readFileSync(0, 'utf8'));
  const rules = Array.isArray(value) ? value.flat() : [];
  const pullRequest = rules.some((r) => r?.type === 'pull_request'
    && r.parameters?.required_approving_review_count >= 1
    && r.parameters?.dismiss_stale_reviews_on_push === true
    && r.parameters?.allowed_merge_methods?.includes('merge'));
  const checks = rules.some((r) => r?.type === 'required_status_checks'
    && Array.isArray(r.parameters?.required_status_checks)
    && r.parameters.required_status_checks.some((c) => c.context === 'verify'));
  if (!pullRequest || !checks) throw new Error();
  process.stdout.write('Reglas activas de revisión y comprobación detectadas.\n');
} catch {
  process.stderr.write('Faltan reglas activas de PR con revisión vigente, merge y comprobación verify obligatoria.\n');
  process.exitCode = 1;
}
