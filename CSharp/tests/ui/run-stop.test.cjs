const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');

(async () => {
  const handlers = {};
  const renders = [];
  const errors = [];
  let completePoll;
  const context = {
    sessionId: 'old-run', pollGeneration: 0,
    cancelButton: { addEventListener(_, handler) { handlers.stop = handler; } },
    backButton: { addEventListener(_, handler) { handlers.edit = handler; } },
    restartRunButton: { hidden: false, disabled: false, addEventListener(_, handler) { handlers.restart = handler; } },
    runButton: {},
    stopPolling() { context.pollGeneration++; }, setBusy() {}, clearError() {},
    setActiveStep(step) { context.step = step; }, setSetupStep(step) { context.setupStep = step; },
    document: { querySelector() { return null; } },
    api: () => new Promise(resolve => { completePoll = resolve; }),
    render(snapshot) { renders.push(snapshot); }, showError(message) { errors.push(message); },
    isTerminal: status => status === 'cancelled', setInterval() { return 1; },
  };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf("cancelButton.addEventListener('click',"), source.indexOf("rejectButton.addEventListener('click',")), context);
  vm.runInContext(source.slice(source.indexOf('async function refresh()'), source.indexOf('async function api(')), context);

  const oldPoll = context.refresh();
  const resolveOldPoll = completePoll;
  context.api = async url => {
    assert.equal(url, '/api/sessions/old-run/cancel');
    return { id: 'old-run', status: 'cancelled' };
  };
  await handlers.stop();
  assert.equal(context.cancelButton.disabled, false);
  assert.equal(renders[0].status, 'cancelled');
  resolveOldPoll({ id: 'old-run', status: 'running' });
  await oldPoll;
  assert.equal(renders.length, 1, 'A stale poll must not overwrite cancellation');

  handlers.edit();
  assert.equal(context.step, 'setup');
  assert.equal(context.sessionId, null);
  context.sessionId = 'finished-run';
  context.form = { reset() { assert.fail('Restart must retain setup values and attachments'); } };
  const previousApi = context.api;
  context.api = () => { assert.fail('Restart must wait for setup confirmation before creating a run'); };
  handlers.restart();
  assert.equal(context.sessionId, null);
  assert.equal(context.step, 'setup');
  assert.equal(context.setupStep, 'connection');
  assert.equal(context.restartRunButton.hidden, true);
  context.api = previousApi;
  context.sessionId = 'new-run';
  context.api = () => new Promise(resolve => { completePoll = resolve; });
  const nextPoll = context.refresh();
  context.sessionId = 'another-run';
  completePoll({ id: 'new-run', status: 'running' });
  await nextPoll;
  assert.equal(renders.length, 1, 'A previous run must not overwrite the new run');

  context.api = async () => { throw new Error('Stop failed'); };
  await handlers.stop();
  assert.equal(context.cancelButton.disabled, false);
  assert.equal(context.pollTimer, 1, 'Failed stop must resume polling');
  assert.deepEqual(errors, ['Stop failed']);
  console.log('Stopping supports configuration edits and ignores stale run responses.');
})().catch(error => { console.error(error); process.exitCode = 1; });
