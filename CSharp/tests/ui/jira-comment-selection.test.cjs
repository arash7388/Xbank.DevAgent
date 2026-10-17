const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const html = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/index.html'), 'utf8');
assert.match(html, /id="jira-include-comments"[^>]*checked/);

function node(extra = {}) {
  return { value: '', textContent: '', hidden: false, handlers: {},
    addEventListener(event, handler) { this.handlers[event] = handler; },
    dispatchEvent() {}, click() { this.handlers.click(); }, ...extra };
}
const nodes = {
  '#jira-include-comments': node({ checked: true }),
  '#jira-select-task': node(), '#task': node({ maxLength: 50000 }),
  '#change-source-error': node(), '#jira-selected-task': node(), '#jira-comment-key': node(),
};
const context = {
  document: { querySelector: selector => nodes[selector], querySelectorAll: () => [] },
  loadedJiraIssue: { key: 'TASK-42', fields: { summary: 'Example', description: 'Original description' } },
  selectedJiraKey: '', selectedJiraContext: '', selectedJiraIncludeComments: true,
  selectedJiraAttachmentIds: [], selectedJiraAttachments: [],
  changeRequestDrafts: {}, agentModels: { querySelector: () => null },
  jiraText: value => value, updateJiraSelection() {}, Event: class {},
  setJiraError() { throw new Error('Unexpected selection error'); },
};
vm.createContext(context);
const start = source.indexOf("document.querySelector('#jira-include-comments').addEventListener");
const end = source.indexOf('\nlet jiraSearchMode', start);
vm.runInContext(source.slice(start, end), context);
nodes['#jira-select-task'].click();
assert.equal(context.selectedJiraIncludeComments, true);
assert.match(nodes['#task'].value, /Jira comments: included/);
nodes['#task'].value += '\nAdditional instructions';
nodes['#jira-include-comments'].checked = false;
nodes['#jira-include-comments'].handlers.change();
assert.equal(context.selectedJiraIncludeComments, false);
assert.match(nodes['#task'].value, /Jira comments: excluded/);
assert.match(nodes['#task'].value, /Additional instructions/);
assert.match(nodes['#jira-selected-task'].textContent, /comments excluded/);
assert.equal((nodes['#task'].value.match(/Jira task:/g) || []).length, 1);

// Previewing another issue must not alter the previously selected run's setting.
context.loadedJiraIssue = { key: 'TASK-43', fields: {} };
nodes['#jira-include-comments'].checked = true;
nodes['#jira-include-comments'].handlers.change();
assert.equal(context.selectedJiraIncludeComments, false);

context.connection = { sourceControl: 'gitlab', repositoryUrl: 'https://example.test/repo.git' };
context.values = new Map([['branch', 'main'], ['task', 'TASK-42'], ['maxAiCalls', '30'], ['maxBudgetUsd', '0.1']]);
context.agents = {};
context.referenceCollectionId = null;
context.cognia = null;
context.changeRequestSource = 'jira';
const requestStart = source.indexOf('  const request = {\n    sourceControl: connection.sourceControl');
const requestEnd = source.indexOf('\n\n  try {', requestStart);
const requestCode = source.slice(requestStart, requestEnd).replace('const request =', 'globalThis.request =');
vm.runInContext(requestCode, context);
assert.equal(context.request.jiraIncludeComments, false);
context.changeRequestSource = 'manual';
vm.runInContext(requestCode, context);
assert.equal(context.request.jiraIncludeComments, true);
console.log('Jira comment selection UI checks passed.');
