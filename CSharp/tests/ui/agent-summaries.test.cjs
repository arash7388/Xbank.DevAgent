const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function renderAgentSummaries(');
const end = source.indexOf('\nfunction renderUsage(', start);
function node() {
  return {
    children: [], textContent: '', hidden: true, dataset: {},
    set innerHTML(_) { throw new Error('Agent summaries must remain plain text.'); },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
  };
}
const panel = node();
let location;
const context = {
  document: { createElement: node }, agentSummaries: panel,
  finalUsageSummary: { before(element) { assert.equal(element, panel); location = 'diff'; } },
  usageSummary: { after(element) { assert.equal(element, panel); location = 'activity'; } },
  isTerminal: status => ['failed', 'cancelled', 'no_changes', 'rejected', 'pushed', 'committed', 'approval_failed', 'pull_request_created'].includes(status),
};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);
function text(element) { return [element.textContent, ...element.children.map(text)].join(' '); }
const events = [
  { type: 'agent_summary', agent: 'planner', text: 'Planned a focused edit.', status: 'provided' },
  { type: 'agent_summary', agent: 'developer', text: 'Initial patch.', status: 'provided' },
  { type: 'agent_summary', agent: 'reviewer', text: 'This agent was not reached during the run.', status: 'not_run' },
  { type: 'message', agent: 'developer', text: 'Detailed work log should stay in activity.' },
  { type: 'agent_summary', agent: 'developer', text: 'ایراد برطرف شد. <img src=x onerror=alert(1)>', status: 'provided' },
];
context.renderAgentSummaries(events, 'running');
assert.equal(panel.hidden, true);
context.renderAgentSummaries(events, 'awaiting_approval');
assert.equal(panel.hidden, false);
assert.equal(location, 'diff');
assert.equal(panel.children[1].children.length, 3);
assert.equal(panel.children[1].children[0].children[0].textContent, 'Planner');
assert.equal(panel.children[1].children[1].children[0].textContent, 'Developer');
assert.match(text(panel), /ایراد برطرف شد\. <img/);
assert.equal(panel.children[1].children[1].children[1].dir, 'rtl');
assert.equal(panel.children[1].children[1].children[1].lang, 'fa');
assert.doesNotMatch(text(panel), /Initial patch|Detailed work log|Security|Jira/);
for (const status of ['failed', 'cancelled', 'no_changes', 'rejected', 'approval_failed']) {
  context.renderAgentSummaries(JSON.parse(JSON.stringify(events)), status);
  assert.equal(panel.hidden, false, status);
  assert.equal(location, 'activity', status);
}
for (const status of ['committed', 'pushed', 'creating_pull_request', 'pull_request_created']) {
  context.renderAgentSummaries(events, status);
  assert.equal(panel.hidden, false, status);
  assert.equal(location, 'diff', status);
}
context.renderAgentSummaries([{ type: 'agent_summary', agent: 'planner', text: '', status: 'unavailable' }], 'failed');
assert.match(text(panel), /خلاصه‌ای ارائه نشد\./);
context.renderAgentSummaries([], 'failed');
assert.equal(panel.hidden, true);
assert.equal(panel.children.length, 0);
console.log('Agent summary checks passed.');
