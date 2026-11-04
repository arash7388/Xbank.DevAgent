const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; this.attributes = {}; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this.textContent = ''; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  showModal() { this.open = true; }
  scrollIntoView() {}
  querySelectorAll() { return []; }
  close() { this.open = false; this.listeners.close(); }
}
const elements = new Map();
const element = id => {
  if (!elements.has(id)) elements.set(id, new Element('div'));
  return elements.get(id);
};
const find = (root, predicate) => {
  if (predicate(root)) return root;
  for (const child of root.children) { const found = find(child, predicate); if (found) return found; }
};
const text = root => [root.textContent || '', ...root.children.map(text)].join(' ');
const requests = [];
let respond;
const context = {
  document: { querySelector: element, createElement: tag => new Element(tag) },
  fetch: async url => { requests.push(url); return respond(url); },
};
element('#run-history-limit').value = '50';
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/run-history.js'), 'utf8'), context);
const flush = () => new Promise(resolve => setImmediate(resolve));
const ok = data => ({ ok: true, json: async () => data });
const list = element('#run-history-list');
const details = element('#run-history-details');
const refresh = element('#run-history-refresh');

(async () => {
  respond = () => ok([]);
  element('#previous-runs-button').listeners.click();
  await flush();
  assert.equal(element('#run-history-dialog').open, true);
  assert.equal(requests[0], '/api/runs?limit=51&offset=0');
  assert.match(element('#run-history-message').textContent, /No previous runs/);

  const run = { id: 'saved-run', workflowType: 'repository_change', status: 'failed', error: '<img src=x onerror=alert(1)>',
    createdAt: '2026-10-28T08:00:00Z', startedAt: '2026-10-28T08:00:00Z', finishedAt: '2026-10-28T08:01:05Z' };
  respond = () => ok([run]);
  await refresh.listeners.click();
  assert.match(text(list), /Run type/);
  assert.equal(find(list, item => item.tag === 'tbody').children[0].children[1].textContent, 'Develop');
  assert.match(text(list), /0h 1m 5s/);
  assert.match(text(list), /<img src=x onerror=alert\(1\)>/);
  assert.equal(find(list, item => item.tag === 'img'), undefined, 'Saved content must be rendered as text');
  const view = find(list, item => item.tag === 'button');
  respond = () => ok({ run, agentRuns: [{ agent: 'Coder', model: 'model', status: 'failed', attempt: 2,
    inputJson: '{"task":"fix"}', responseJson: 'plain output', errorJson: 'agent failure',
    startedAt: run.startedAt, completedAt: run.finishedAt }],
    toolCalls: [{ agent: 'Coder', tool: 'read_file', status: 'completed', durationMilliseconds: 0 }] });
  view.listeners.click();
  await flush();
  assert.equal(requests[requests.length - 1], '/api/runs/saved-run');
  for (const expected of ['Coder', 'model', 'Attempt', 'plain output', 'agent failure', 'read_file']) {
    assert.ok(text(details).includes(expected), `Details should show ${expected}`);
  }
  assert.equal(details.hidden, false);
  assert.equal(find(details, item => item.tag === 'tbody').children[0].children[0].textContent, 'Develop');

  const reviewRun = { ...run, workflowType: 'gitlab_review' };
  respond = () => ok([reviewRun]);
  await refresh.listeners.click();
  assert.equal(find(list, item => item.tag === 'tbody').children[0].children[1].textContent, 'Review');
  respond = () => ok({ run: reviewRun });
  find(list, item => item.tag === 'button').listeners.click();
  await flush();
  assert.equal(find(details, item => item.tag === 'tbody').children[0].children[0].textContent, 'Review');

  respond = () => ({ ok: false, status: 500 });
  await refresh.listeners.click();
  assert.equal(details.hidden, true);
  assert.equal(list.children.length, 0);
  assert.equal(element('#run-history-error').hidden, false);
  assert.equal(refresh.disabled, false);

  element('#run-history-limit').value = '100';
  respond = () => ok([run]);
  await element('#run-history-limit').listeners.change();
  assert.equal(requests[requests.length - 1], '/api/runs?limit=101&offset=0');
  respond = () => ok({ run });
  find(list, item => item.tag === 'button').listeners.click();
  await flush();
  assert.match(text(details), /No agent runs were recorded/);

  respond = () => ({ ok: false, status: 404 });
  find(list, item => item.tag === 'button').listeners.click();
  await flush();
  assert.match(text(details), /Could not load run details/);
  respond = () => ok({ run });
  find(details, item => item.tag === 'button').listeners.click();
  await flush();
  assert.match(text(details), /No tool calls were recorded/);

  element('#run-history-limit').value = '50';
  respond = () => ok(Array.from({ length: 51 }, (_, index) => ({ ...run, id: `run-${index}` })));
  await element('#run-history-limit').listeners.change();
  assert.equal(element('#run-history-prev').disabled, true);
  assert.equal(element('#run-history-next').disabled, false);
  assert.match(element('#run-history-message').textContent, /1–50/);
  assert.equal(find(list, item => item.tag === 'tbody').children.length, 50, 'Lookahead row stays off the current page');
  assert.ok(find(list, item => item.className === 'history-status history-status-danger'));
  respond = () => ok([run]);
  await element('#run-history-next').listeners.click();
  assert.equal(requests[requests.length - 1], '/api/runs?limit=51&offset=50');
  assert.equal(element('#run-history-next').disabled, true);
  assert.equal(element('#run-history-prev').disabled, false);
  assert.equal(element('#run-history-page').textContent, 'Page 2');
  assert.match(element('#run-history-message').textContent, /51–51/);
  await element('#run-history-prev').listeners.click();
  assert.equal(requests[requests.length - 1], '/api/runs?limit=51&offset=0');
  assert.equal(element('#run-history-page').textContent, 'Page 1');

  let finishOldRequest;
  respond = () => new Promise(resolve => { finishOldRequest = resolve; });
  const oldLoad = refresh.listeners.click();
  respond = () => ok([]);
  await refresh.listeners.click();
  finishOldRequest(ok([run]));
  await oldLoad;
  assert.equal(list.children.length, 0, 'A stale response must not replace refreshed history');
  element('#run-history-close').listeners.click();
  assert.equal(element('#run-history-dialog').open, false);
  console.log('Run history: pagination, status badges, saved details, safe text, retries and stale responses pass.');
})().catch(error => { console.error(error); process.exitCode = 1; });
