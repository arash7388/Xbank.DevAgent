const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

// These are standalone scripts, so this also works on Node versions without node --test.
const results = fs.readdirSync(__dirname).filter(name => name.endsWith('.test.cjs')).sort().map(name => {
  const started = Date.now();
  const result = spawnSync(process.execPath, [path.join(__dirname, name)], {
    encoding: 'utf8', timeout: 60000, windowsHide: true,
  });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error) console.error(`${name}: ${result.error.message}`);
  return { name, passed: result.status === 0 && !result.error, durationMs: Date.now() - started };
});
const failed = results.filter(result => !result.passed);
console.log(`UI scripts: ${results.length - failed.length} passed, ${failed.length} failed, ${results.length} total.`);
const reportArg = process.argv.indexOf('--report');
if (reportArg >= 0) {
  const report = path.resolve(process.argv[reportArg + 1]);
  fs.mkdirSync(path.dirname(report), { recursive: true });
  fs.writeFileSync(report, JSON.stringify({ node: process.version, results }, null, 2));
}
process.exitCode = failed.length ? 1 : 0;
