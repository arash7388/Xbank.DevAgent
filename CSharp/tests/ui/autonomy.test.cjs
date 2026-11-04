const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const root = path.join(__dirname, '../../src/Xbank.DevAgent.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const nodes = {};
for (const id of ['autonomy-level', 'autonomy-label', 'autonomy-description', 'auto-commit', 'auto-push', 'auto-create-pull-request']) {
  nodes[`#${id}`] = { value: '100', checked: false, handlers: {}, attributes: {},
    addEventListener(name, handler) { this.handlers[name] = handler; },
    setAttribute(name, value) { this.attributes[name] = value; },
  };
}
vm.runInNewContext(fs.readFileSync(path.join(root, 'autonomy.js'), 'utf8'), {
  document: { querySelector: selector => nodes[selector] },
});
const slider = nodes['#autonomy-level'];
const names = ['Guided', 'Assisted', 'Collaborative', 'Highly autonomous', 'Fully autonomous'];
for (const [index, level] of [0, 25, 50, 75, 100].entries()) {
  slider.value = String(level);
  slider.handlers.input();
  assert.equal(nodes['#autonomy-label'].textContent, `${level}% — ${names[index]}`);
  assert.equal(slider.attributes['aria-valuetext'], nodes['#autonomy-label'].textContent);
}
const commit = nodes['#auto-commit'], push = nodes['#auto-push'], request = nodes['#auto-create-pull-request'];
request.checked = true;
request.handlers.change();
assert.ok(commit.checked && push.checked);
commit.checked = false;
commit.handlers.change();
assert.ok(!push.checked && !request.checked);
push.checked = true;
push.handlers.change();
assert.ok(commit.checked);
request.checked = true;
push.checked = false;
push.handlers.change();
assert.ok(!request.checked && commit.checked);
assert.ok(/id="autonomy-level"[^>]*step="25"[^>]*value="100"/.test(html));
assert.ok(html.includes('/autonomy.js?'));

const context = { connection: {}, agents: {}, referenceCollectionId: null, cognia: null, changeRequestSource: 'manual',
  values: new Map([['branch', 'main'], ['task', 'TASK-42 Update behavior'], ['maxAiCalls', '30'], ['maxBudgetUsd', '0.1']]) };
vm.createContext(context);
const code = require('./request-payload.cjs')(source);
vm.runInContext(code, context);
assert.equal(context.request.autonomyLevel, 100);
assert.equal(context.request.autoCommit, false);
assert.equal(context.request.autoPush, false);
assert.equal(context.request.autoCreatePullRequest, false);
context.values.set('autonomyLevel', '100');
for (const field of ['autoCommit', 'autoPush', 'autoCreatePullRequest']) context.values.set(field, 'on');
vm.runInContext(code, context);
assert.equal(context.request.autonomyLevel, 100);
assert.ok(context.request.autoCommit && context.request.autoPush && context.request.autoCreatePullRequest);
console.log('Autonomy labels, accessibility, completion dependencies, and request payload passed.');

(async () => {
  const poll = { sessionId: 'run', pollGeneration: 0, stopped: 0,
    render() {}, showError(message) { assert.fail(message); },
    isTerminal: status => ['completed', 'committed', 'pushed', 'pull_request_created'].includes(status),
    stopPolling() { poll.stopped++; },
  };
  vm.createContext(poll);
  vm.runInContext(source.slice(source.indexOf('async function refresh()'), source.indexOf('async function api(')), poll);
  for (const status of ['awaiting_approval', 'committed', 'pushed', 'creating_pull_request']) {
    poll.api = async () => ({ status, automaticCompletionPending: true });
    await poll.refresh();
    assert.equal(poll.stopped, 0, 'Keep polling until all automatic actions finish');
  }
  poll.api = async () => ({ status: 'pull_request_created', automaticCompletionPending: false });
  await poll.refresh();
  assert.equal(poll.stopped, 1);
  poll.api = async () => ({ status: 'completed' });
  await poll.refresh();
  assert.equal(poll.stopped, 2, 'A finished patch needs no human approval or further polling');
  console.log('Automatic completion polling passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
