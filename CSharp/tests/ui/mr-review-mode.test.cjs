const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const nodes = Object.fromEntries(['input[name="runMode"]:checked', '#mr-review-settings', '#merge-request-iid', '#task', '#review-team-map', '#change-team-map']
  .map(id => [id, { value: '', required: true }]));
const sourceRadios = [{ value: 'github' }, { value: 'gitlab' }];
const jiraToggle = { checked: false, disabled: false, dispatchEvent() {} };
const agentCatalog = ['jira', 'planner', 'developer', 'reviewer', 'security', 'release_manager']
  .map(id => ({ id, required: !['jira', 'security'].includes(id) }));
const cards = agentCatalog.map(agent => {
  const toggle = agent.id === 'jira' ? jiraToggle : { checked: true, disabled: agent.required };
  const label = {};
  return { dataset: { agent: agent.id }, toggle,
    querySelector: selector => selector === '[data-field="enabled"]' ? toggle : label };
});
nodes['#agent-selection-guide'] = {};
for (const selector of ['.steps [data-step="jira"] span', '#jira-title', '[data-change-source="manual"] strong',
  'label[for="task"]', '#task + .field-guide', '#manual-change', '#empty-activity p']) nodes[selector] = {};
const context = {
  document: {
    querySelector: id => id === '#source-control-gitlab' ? sourceRadios[1] : nodes[id],
    querySelectorAll: () => sourceRadios,
  },
  baseBranchInput: {}, sourceControlInput: {}, setRepositorySource(value) { this.provider = value; },
  loadOpenMergeRequests() {},
  currentSetupStep: 'connection', setSetupStep() {},
  agentModels: { querySelectorAll: () => cards }, agentCatalog, syncAgentCardEnabled() {}, Event: class {},
};
vm.createContext(context);
const start = source.indexOf('function syncMergeRequestMode()');
const end = source.indexOf("document.querySelector('#use-saved-gitlab-token')", start);
vm.runInContext(source.slice(start, end), context);
nodes['input[name="runMode"]:checked'].value = 'gitlab_review';
context.syncMergeRequestMode();
assert.equal(nodes['.steps [data-step="jira"] span'].textContent, 'Review request');
assert.equal(nodes['#jira-title'].textContent, 'Choose your review request');
assert.equal(nodes['[data-change-source="manual"] strong'].textContent, 'Type review instructions');
assert.equal(nodes['label[for="task"]'].textContent, 'Additional review instructions (optional)');
assert.ok(nodes['#task + .field-guide'].textContent.includes('MR source branch'));
assert.ok(!nodes['#task + .field-guide'].textContent.includes('task number'));
assert.ok(nodes['#task'].placeholder.includes('selected MR'));
assert.equal(nodes['#mr-review-settings'].hidden, false);
assert.equal(nodes['#merge-request-iid'].required, true);
assert.equal(nodes['#task'].required, false);
assert.equal(context.sourceControlInput.value, 'gitlab');
assert.equal(sourceRadios[1].checked, true);
assert.ok(sourceRadios.every(input => input.disabled));
assert.equal(context.baseBranchInput.disabled, true);
assert.equal(jiraToggle.checked, true);
assert.equal(jiraToggle.disabled, true);
for (const card of cards) {
  assert.equal(card.toggle.checked, ['jira', 'reviewer', 'release_manager'].includes(card.dataset.agent));
  assert.equal(card.toggle.disabled, true);
}
// Repeated synchronization must preserve the original selection.
context.syncMergeRequestMode();
nodes['input[name="runMode"]:checked'].value = 'change';
context.syncMergeRequestMode();
assert.equal(nodes['.steps [data-step="jira"] span'].textContent, 'Change request');
assert.equal(nodes['#jira-title'].textContent, 'Choose your change request');
assert.equal(nodes['label[for="task"]'].textContent, 'Requested code change');
assert.ok(nodes['#task + .field-guide'].textContent.includes('task number'));
assert.equal(nodes['#task'].required, true);
assert.equal(context.baseBranchInput.disabled, false);
assert.equal(context.sourceControlInput.disabled, false);
assert.ok(sourceRadios.every(input => !input.disabled));
assert.equal(jiraToggle.disabled, false);
assert.equal(jiraToggle.checked, false);
for (const card of cards.filter(card => card.dataset.agent !== 'jira')) assert.equal(card.toggle.checked, true);
const requestContext = {
  connection: { sourceControl: 'gitlab', repositoryUrl: 'https://gitlab.com/acme/orders.git', accessToken: 'token' },
  values: new Map([['runMode', 'gitlab_review'], ['mergeRequestIid', '17'], ['task', ''], ['includeResolvedDiscussions', 'on']]),
  agents: {}, referenceCollectionId: null, cognia: null, changeRequestSource: 'manual',
};
vm.createContext(requestContext);
vm.runInContext(require('./request-payload.cjs')(source), requestContext);
assert.equal(requestContext.request.runMode, 'gitlab_review');
assert.equal(requestContext.request.mergeRequestIid, 17);
assert.equal(requestContext.request.includeResolvedDiscussions, true);
assert.equal(requestContext.request.mrReviewMode, 'combined');
assert.equal(requestContext.request.commentIssuesOnSourceCode, false);
requestContext.values.set('commentIssuesOnSourceCode', 'on');
for (const mode of ['independent', 'feedback', 'combined']) {
  requestContext.values.set('mrReviewMode', mode);
  vm.runInContext(require('./request-payload.cjs')(source), requestContext);
  assert.equal(requestContext.request.mrReviewMode, mode);
  assert.equal(requestContext.request.includeResolvedDiscussions, mode !== 'independent');
  assert.equal(requestContext.request.commentIssuesOnSourceCode, mode !== 'feedback');
}
requestContext.values.set('runMode', 'change');
vm.runInContext(require('./request-payload.cjs')(source), requestContext);
assert.equal(requestContext.request.commentIssuesOnSourceCode, false);
assert.equal(requestContext.request.baseBranch, '');
assert.equal(requestContext.request.task, '');
requestContext.values.set('runMode', 'gitlab_review');
requestContext.changeRequestSource = 'jira';
requestContext.selectedJiraKey = 'TASK-31';
requestContext.selectedJiraAttachmentIds = ['42'];
requestContext.selectedJiraIncludeComments = false;
vm.runInContext(require('./request-payload.cjs')(source), requestContext);
assert.equal(requestContext.request.jiraIssueKey, 'TASK-31');
assert.equal(requestContext.request.jiraIncludeComments, false);
assert.deepEqual(requestContext.request.jiraAttachmentIds, ['42']);
let modeChanged;
const resolved = { checked: true, disabled: false };
const sourceComments = { checked: true, disabled: false };
const selectedMode = { value: 'combined' };
const modeContext = {
  document: { querySelector: selector => selector === '#mr-review-mode'
    ? { addEventListener: (event, handler) => { modeChanged = handler; } }
    : selector === '[name="includeResolvedDiscussions"]' ? resolved
    : selector === '[name="commentIssuesOnSourceCode"]' ? sourceComments : selectedMode },
};
vm.createContext(modeContext);
const modeStart = source.indexOf("document.querySelector('#mr-review-mode').addEventListener");
vm.runInContext(source.slice(modeStart, source.indexOf('function syncMergeRequestMode()', modeStart)), modeContext);
for (const mode of ['independent', 'feedback', 'combined']) {
  selectedMode.value = mode;
  modeChanged();
  assert.equal(resolved.disabled, mode === 'independent');
  assert.equal(sourceComments.disabled, mode === 'feedback');
}
const html = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent.Api/wwwroot/index.html'), 'utf8');
assert.match(html, /<input name="commentIssuesOnSourceCode" type="checkbox">/);
console.log('MR review mode selects GitLab and permits branch and task discovery.');
