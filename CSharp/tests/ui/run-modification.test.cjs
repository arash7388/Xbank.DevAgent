const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');

function setup(api) {
  const nodes = new Map();
  function node(selector) {
    if (!nodes.has(selector)) nodes.set(selector, {
      value: '', hidden: true, disabled: false, handlers: {}, open: false,
      addEventListener(name, handler) { this.handlers[name] = handler; },
      showModal() { this.open = true; }, close() { this.open = false; },
      focus() { this.focused = true; },
    });
    return nodes.get(selector);
  }
  const context = {
    document: { querySelector: node }, sessionId: 'run-42', api,
    stopPolling() { context.stopped = true; },
    clearError() { context.cleared = true; },
    render(snapshot) { context.rendered = snapshot; },
    setInterval(callback, delay) { context.interval = { callback, delay }; return 17; },
    refresh() {}, pollTimer: null,
  };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf('const modificationDialog ='), source.indexOf("approveButton.addEventListener('click'")), context);
  return { context, node, submit: () => node('#modification-form').handlers.submit({ preventDefault() {} }) };
}

(async () => {
  const calls = [];
  const snapshot = { id: 'run-42', status: 'running', executionPending: true };
  const success = setup(async (url, options) => { calls.push({ url, options }); return snapshot; });
  const { context, node, submit } = success;
  node('#request-changes-button').handlers.click();
  assert.equal(node('#modification-dialog').open, true);
  assert.equal(node('#modification-prompt').focused, true);
  await submit();
  assert.equal(calls.length, 0, 'Blank prompts must not start revisions');
  node('#modification-prompt').value = '  Keep the changes and add validation  ';
  node('#modification-submit').disabled = true;
  await submit();
  assert.equal(calls.length, 0, 'Duplicate submits must be ignored');
  node('#modification-submit').disabled = false;
  await submit();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, '/api/sessions/run-42/modify');
  assert.equal(calls[0].options.method, 'POST');
  assert.deepEqual(JSON.parse(calls[0].options.body), { prompt: 'Keep the changes and add validation' });
  assert.equal(context.rendered, snapshot);
  assert.equal(context.stopped, true, 'Invalidate any stale polling responses before rendering the revision');
  assert.equal(context.pollTimer, 17);
  assert.equal(context.interval.callback, context.refresh);
  assert.equal(context.interval.delay, 1000);
  assert.equal(node('#modification-dialog').open, false);
  assert.equal(node('#modification-prompt').value, '');
  assert.equal(node('#modification-submit').disabled, false);

  const failed = setup(async () => { throw new Error('References expired'); });
  failed.node('#request-changes-button').handlers.click();
  failed.node('#modification-prompt').value = 'Keep my feedback';
  await failed.submit();
  assert.equal(failed.node('#modification-dialog').open, true);
  assert.equal(failed.node('#modification-prompt').value, 'Keep my feedback');
  assert.equal(failed.node('#modification-error').textContent, 'References expired');
  assert.equal(failed.node('#modification-error').hidden, false);
  assert.equal(failed.node('#modification-submit').disabled, false);
  assert.equal(failed.context.pollTimer, null);
  failed.node('#modification-cancel').handlers.click();
  assert.equal(failed.node('#modification-dialog').open, false);

  let finish;
  const stale = setup(() => new Promise(resolve => { finish = resolve; }));
  stale.node('#modification-prompt').value = 'Revise';
  const pending = stale.submit();
  stale.context.sessionId = 'different-run';
  finish(snapshot);
  await pending;
  assert.equal(stale.context.rendered, undefined, 'A late response cannot replace another session');
  assert.equal(stale.context.pollTimer, null);

  const polling = {
    sessionId: 'run-42', pollGeneration: 0, stopped: 0,
    render() {}, isTerminal: status => status === 'completed',
    stopPolling() { polling.stopped++; }, showError(message) { assert.fail(message); },
  };
  vm.createContext(polling);
  vm.runInContext(source.slice(source.indexOf('async function refresh()'), source.indexOf('async function api(')), polling);
  for (const status of ['completed', 'awaiting_approval']) {
    polling.api = async () => ({ status, executionPending: true });
    await polling.refresh();
    assert.equal(polling.stopped, 0, 'Wait for final cleanup so the Request changes action becomes available');
  }
  polling.api = async () => ({ status: 'completed', executionPending: false, canRequestChanges: true });
  await polling.refresh();
  assert.equal(polling.stopped, 1);
  console.log('Modification prompts preserve feedback on error and resume polling for the same revised session.');
})().catch(error => { console.error(error); process.exitCode = 1; });
