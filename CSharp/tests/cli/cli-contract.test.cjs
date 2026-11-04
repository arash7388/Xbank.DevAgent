const assert = require('assert').strict;
const path = require('path');
const { spawnSync } = require('child_process');

const configuration = process.argv[2] || 'Release';
assert.ok(['Debug', 'Release'].includes(configuration));
const assembly = path.resolve(__dirname, `../../src/Xbank.DevAgent/Xbank.DevAgent.Cli/bin/${configuration}/net10.0/Xbank.DevAgent.Cli.dll`);
// Never inherit provider credentials or workflow settings into a CLI smoke test.
const cleanEnv = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(OPENAI_|AIFA_|MAF_)/i.test(key)));
const cases = [
  { name: 'long help', args: ['--help'], code: 0, output: /Usage:/ },
  { name: 'short help', args: ['-h'], code: 0, output: /Usage:/ },
  { name: 'missing task', args: [], code: 2, output: /Usage:/ },
  { name: 'missing model', args: ['WEPOD-42'], code: 2, output: /OPENAI_MODEL is required/ },
  { name: 'insecure endpoint', args: ['WEPOD-42'], env: { OPENAI_ENDPOINT: 'http://example.test' }, code: 2, output: /HTTPS/ },
  { name: 'missing AIFA key', args: ['WEPOD-42'], env: { MAF_MODEL_PROVIDER: 'aifa' }, code: 2, output: /AIFA_API_KEY is required/ },
];
let failures = 0;
for (const test of cases) {
  try {
    const result = spawnSync('dotnet', [assembly, ...test.args], {
      env: { ...cleanEnv, ...test.env }, encoding: 'utf8', timeout: 15000, windowsHide: true,
    });
    assert.ifError(result.error);
    assert.equal(result.status, test.code, `${test.name}: ${result.stderr}`);
    assert.match(result.stdout + result.stderr, test.output);
    console.log(`CLI PASS: ${test.name}`);
  } catch (error) {
    failures++;
    console.error(`CLI FAIL: ${test.name}: ${error.message}`);
  }
}
console.log(`CLI scenarios: ${cases.length - failures} passed, ${failures} failed, ${cases.length} total.`);
process.exitCode = failures ? 1 : 0;
