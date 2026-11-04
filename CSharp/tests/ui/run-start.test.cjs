const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');

function setup() {
  const state = { busy: false, errors: [], calls: [], stages: [], timer: false };
  const button = { disabled: false, set textContent(value) { state.stages.push(value); } };
  const context = {
    form: { addEventListener(_, handler) { state.submit = handler; } },
    runButton: button, currentSetupStep: 'confirm', sessionId: 'previous-run',
    clearError() {}, stopPolling() {}, resetOutput() {},
    setBusy(value) { state.busy = button.disabled = value; },
    startElapsedTimer() { state.timer = true; }, resetElapsedTimer() { state.timer = false; },
    showError(message) { state.errors.push(message); },
    setSetupStep(step) { state.setupStep = step; }, setActiveStep(step) { state.activeStep = step; },
    FormData: class { get(key) { return ({ branch: 'main', task: 'Fix the app', maxAiCalls: '30', maxBudgetUsd: '1' })[key] ?? null; } },
    readRepositoryConnection: () => ({ sourceControl: 'github', repositoryUrl: 'https://example.test/repo.git' }),
    readAgentConfigurations: () => ({}), readCogniaConfiguration: () => null,
    selectedReferenceFiles: () => [], uploadReferenceDocuments: async () => null,
    changeRequestSource: 'manual',
    api: async (url, options) => { state.calls.push({ url, options }); return { id: 'new-run' }; },
    render(snapshot) { state.snapshot = snapshot; }, refresh() {},
    setInterval() { state.polling = true; return 1; },
  };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf("form.addEventListener('submit',"), source.indexOf('function readAgentConfigurations()')), context);
  state.run = () => state.submit({ preventDefault() {}, submitter: button });
  return { state, context };
}

(async () => {
  for (const failure of ['resetOutput', 'readRepositoryConnection', 'readAgentConfigurations', 'readCogniaConfiguration', 'uploadReferenceDocuments']) {
    const { state, context } = setup();
    context[failure] = () => { throw new Error(`Failure in ${failure}`); };
    await state.run();
    assert.equal(state.busy, false, `${failure} must release the start button`);
    assert.equal(state.timer, false, `${failure} must stop the elapsed timer`);
    assert.match(state.errors[0], new RegExp(failure));
    assert.equal(context.sessionId, null, 'A failed start must not retain the previous session');
    assert.ok(!state.polling);
  }
  for (const failedUrl of ['/api/runs/preflight', '/api/repositories/branches', '/api/sessions']) {
    const { state, context } = setup();
    context.api = async url => {
      state.calls.push(url);
      if (url === failedUrl) throw new Error('Request timed out');
      return {};
    };
    await state.run();
    assert.equal(state.busy, false);
    assert.match(state.errors[0], /timed out/);
    assert.ok(!state.polling);
    if (failedUrl === '/api/runs/preflight') {
      assert.deepEqual(state.calls, ['/api/runs/preflight']);
      assert.equal(state.setupStep, 'connection');
    }
  }
  const { state, context } = setup();
  await state.run();
  assert.deepEqual(state.errors, []);
  assert.equal(context.sessionId, 'new-run');
  assert.equal(state.activeStep, 'running');
  assert.equal(state.polling, true);
  assert.ok(state.stages.includes('Checking repository access…'));
  assert.ok(state.stages.includes('Creating agent session…'));
  assert.ok(state.calls.every(call => call.options.timeoutMs === 30_000));
  assert.equal(state.calls[0].url, '/api/runs/preflight');

  // Exercise the actual request helper with both connection and response-body timeouts.
  for (const bodyTimeout of [false, true]) {
    const timeout = Object.assign(new Error('deadline'), { name: 'TimeoutError' });
    const signal = { aborted: true, reason: timeout };
    const apiContext = {
      AbortSignal: { timeout(ms) { assert.equal(ms, 30_000); return signal; } },
      fetch: async (_, options) => {
        assert.equal(options.signal, signal);
        assert.equal(options.timeoutMs, undefined);
        if (!bodyTimeout) throw timeout;
        return { ok: true, json: async () => { throw Object.assign(new Error('aborted'), { name: 'AbortError' }); } };
      },
    };
    vm.createContext(apiContext);
    vm.runInContext(source.slice(source.indexOf('async function api('), source.indexOf('\nfunction render(snapshot)')), apiContext);
    await assert.rejects(apiContext.api('/api/repositories/branches', { timeoutMs: 30_000 }), /timed out after 30 seconds/);
  }
  console.log('Run start recovers from initialization errors and timeouts, and starts polling on success.');
})().catch(error => { console.error(error); process.exitCode = 1; });
