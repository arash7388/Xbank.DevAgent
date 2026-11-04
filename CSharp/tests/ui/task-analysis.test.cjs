const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const nodes = new Map();
function get(id) {
  if (!nodes.has(id)) nodes.set(id, {
    value: '', checked: false, children: [], handlers: {}, textContent: '',
    add(item) { this.children.push(item); },
    replaceChildren(...items) { this.children = items; },
    append(item) { this.children.push(item); },
    addEventListener(name, fn) { this.handlers[name] = fn; },
    showModal() { this.open = true; }, close() { this.open = false; },
  });
  return nodes.get(id);
}
for (const field of ['analyse', 'summarize', 'sample']) get(`analysis-${field}`).type = 'checkbox';
get('analysis-analyse').checked = true;
get('analysis-language').value = 'Persian';
get('analysis-issue').value = 'TASK-1';
const saved = new Map([['maf-task-analysis-settings-v1', JSON.stringify({
  provider: 'deepseek', model: 'test-model', effort: 'medium', prompt: 'Keep my existing guidance',
  'input-limit': 128000, 'output-limit': 4096,
})]]);
let payload, fail = false;
const catalog = { deepseek: { name: 'DeepSeek', url: 'https://api.deepseek.com',
  models: [['test-model', 'Test']], thinking: true, reasoningEffort: 'medium',
  supportsReasoningEffort: ['low', 'medium'], maxInputTokens: 128000, maxOutputTokens: 4096 } };
const original = JSON.stringify(catalog);
const html = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/index.html'), 'utf8');
for (const id of ['analysis-input-limit', 'analysis-output-limit']) {
  const tag = html.match(new RegExp(`<input[^>]+id="${id}"[^>]*>`))[0];
  assert(!/\smax=/.test(tag), `${id} must not impose an upper bound`);
}
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/task-analysis.js'), 'utf8'), {
  document: { getElementById: get, createElement: () => ({}) },
  providerCatalog: catalog, AbortController: class {
    constructor() { this.signal = { aborted: false }; }
    abort() { this.signal.aborted = true; }
  },
  Option: function(text, value) { this.text = text; this.value = value; },
  localStorage: { getItem: key => saved.get(key), setItem: (key, value) => saved.set(key, value) },
  fetch: async (url, options) => {
    if (url === '/api/projects') return { ok: true, json: async () => [{ key: 'sample-repo', gitLabProjectPath: 'https://gitlab.example/repo.git' }] };
    assert.equal(url, '/api/task-analysis');
    payload = JSON.parse(options.body);
    return { ok: !fail, json: async () => fail ? { error: 'Jira unavailable' } : {
      issueKey: 'TASK-1', outputLanguage: 'Persian', text: '<script>untrusted</script>', notices: ['Unreadable screenshot'],
    } };
  },
});
(async () => {
  get('task-analysis-button').handlers.click();
  await new Promise(setImmediate);
  assert.equal(get('task-analysis-dialog').open, true);
  assert.equal(get('analysis-repository').children[1].value, 'sample-repo');
  assert.equal(get('analysis-input-limit').value, 1000000);
  assert.equal(get('analysis-output-limit').value, 285000);
  get('analysis-repository').value = 'sample-repo';
  get('analysis-model').value = 'test-model';
  get('analysis-key').value = 'private-key';
  await get('task-analysis-form').handlers.submit({ preventDefault() {} });
  assert.equal(payload.model.apiKey, 'private-key');
  assert.equal(payload.model.prompt, 'Keep my existing guidance');
  assert.equal(payload.projectKey, 'sample-repo');
  assert.equal(payload.model.provider.maxInputTokens, 1000000);
  assert.equal(payload.model.provider.maxOutputTokens, 285000);
  get('analysis-input-limit').value = 5000000;
  get('analysis-output-limit').value = 3000000;
  await get('task-analysis-form').handlers.submit({ preventDefault() {} });
  assert.equal(payload.model.provider.maxInputTokens, 5000000);
  assert.equal(payload.model.provider.maxOutputTokens, 3000000);
  assert.equal(payload.model.provider.reasoningEffort, 'medium');
  assert.equal(payload.outputLanguage, 'Persian');
  assert.equal(get('analysis-result').dir, 'rtl');
  assert.equal(get('analysis-result').textContent, '<script>untrusted</script>');
  assert.equal(get('analysis-notices').children[0].textContent, 'Unreadable screenshot');
  assert([...saved.values()].every(value => !value.includes('private-key')));
  assert.equal(JSON.stringify(catalog), original);
  fail = true;
  await get('task-analysis-form').handlers.submit({ preventDefault() {} });
  assert.equal(get('analysis-error').textContent, 'Jira unavailable');
  assert.equal(get('analysis-submit').disabled, false);
  console.log('Task Analysis UI tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
