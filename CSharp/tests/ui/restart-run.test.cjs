const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');

const elements = new Map();
function element(selector) {
  if (!elements.has(selector)) elements.set(selector, {
    append(child) { child.parentElement = this; },
    removeAttribute() {}, prepend() {},
  });
  return elements.get(selector);
}
const context = {
  document: { querySelector: element },
  workflowSteps: [], workflowPanels: [], setupStepOrder: ['connection', 'confirm'],
  currentSetupStep: 'connection', currentWorkflowStep: 'running',
  restartRunButton: {}, backButton: {}, approveButton: {}, rejectButton: {},
  generatePrButton: {}, pullRequestLink: element('pr'), cancelButton: {}, runButton: {},
  sessionLabel: {}, diffSummary: {}, pushBranchLabel: {}, pushBranchGuide: {},
  reportButtons: [], teamMapSection: {}, activityHeading: null,
  setElapsedTimer() {}, shortRepo: () => 'repo',
  setStatus(status) { context.setActiveStep(['failed', 'cancelled', 'no_changes', 'rejected', 'approval_failed'].includes(status) ? 'running'
    : ['awaiting_approval', 'completed'].includes(status) ? 'review' : 'finish'); },
  renderEvents() {}, renderUsage() {}, renderAgentSummaries() {}, renderJiraProblemNotice() {},
  renderBuildWarning() {}, renderJiraClarification() {}, renderDiff() {}, showRunError() {}, clearRunError() {},
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('function isTerminal(status)'), source.indexOf('function stopPolling()')), context);
vm.runInContext(source.slice(source.indexOf('function setActiveStep(active)'), source.indexOf('function setBusy(value)')), context);
vm.runInContext(source.slice(source.indexOf('function render(snapshot)'), source.indexOf('async function loadRunReport(')), context);

for (const status of ['failed', 'cancelled', 'no_changes', 'rejected', 'approval_failed', 'awaiting_approval',
  'completed', 'committed', 'pushed', 'pull_request_created']) {
  context.render({ status });
  assert.equal(context.restartRunButton.hidden, false, `Restart must be available for ${status}`);
  assert.equal(context.restartRunButton.parentElement,
    element(['failed', 'cancelled', 'no_changes', 'rejected', 'approval_failed'].includes(status) ? '.run-meta' : '.review-actions'));
  context.render({ status, automaticCompletionPending: true });
  assert.equal(context.restartRunButton.hidden, true, 'Wait for automatic completion before restarting');
}
for (const status of ['cloning', 'running', 'awaiting_input', 'approving', 'creating_pull_request']) {
  context.render({ status });
  assert.equal(context.restartRunButton.hidden, true, `Restart must not abandon an active ${status} operation`);
}
for (const status of ['completed', 'awaiting_approval', 'no_changes']) {
  context.render({ status, canRequestChanges: true });
  assert.equal(element('#request-changes-button').hidden, false);
  assert.equal(element('#request-changes-button').parentElement,
    element(status === 'no_changes' ? '.run-meta' : '.review-actions'));
  context.render({ status, canRequestChanges: false });
  assert.equal(element('#request-changes-button').hidden, true, 'Archived or busy results cannot be revised');
}
console.log('Restart is available after stopped/finished runs and follows the visible panel.');
