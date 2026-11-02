const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const root = path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'run-settings.js'), 'utf8');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');

function node(value = '') {
  return {
    value, checked: false, attributes: {}, handlers: {},
    setAttribute(key, value) { this.attributes[key] = value; },
    addEventListener(event, handler) { this.handlers[event] = handler; },
    dispatchEvent(event) { if (this.handlers[event.type]) this.handlers[event.type](event); },
    closest() { return this; },
  };
}
const nodes = new Map();
function get(selector) {
  if (!nodes.has(selector)) nodes.set(selector, node());
  return nodes.get(selector);
}
const cards = ['developer', 'reviewer'].map(id => {
  const fields = Object.fromEntries(['provider', 'max-agent-turns', 'prompt', 'tools'].map(field => [field, node()]));
  fields.provider.value = 'claude';
  fields.provider.options = ['aifa', 'deepseek', 'openai', 'claude'].map(value => node(value));
  fields['max-agent-turns'].previousElementSibling = node();
  fields['max-agent-turns'].nextElementSibling = node();
  fields.prompt.previousElementSibling = node();
  return {
    dataset: { agent: id }, fields,
    querySelector(selector) { return fields[selector.match(/data-field="([^"]+)"/)[1]]; },
    querySelectorAll() { return [fields.tools, fields['max-agent-turns'], fields.prompt]; },
  };
});
const keys = Object.fromEntries(['openai', 'claude'].map(id => [id, { parentElement: node() }]));
const marked = [node(), node()];
get('[name="runExperience"]:checked').value = 'simple';
let summaries = 0;
let requiredSyncs = 0;
const context = {
  document: { querySelector: get, querySelectorAll: () => marked, documentElement: { dataset: {} } },
  providerKeys: keys, agentModels: { querySelectorAll: () => cards },
  Event: class { constructor(type) { this.type = type; } },
  syncAgentCard() {}, syncRequiredProviderKeys() { requiredSyncs++; },
  currentSetupStep: 'confirm', renderRunReview() { summaries++; },
};
vm.createContext(context);
vm.runInContext(source, context);

function verifySimple() {
  assert.equal(context.document.documentElement.dataset.runExperience, 'simple');
  for (const [name, expected] of Object.entries({
    includeResolvedDiscussions: false, commentIssuesOnSourceCode: false,
    releaseManagerValidationEnabled: false, jiraValidationEnabled: true,
    autoCommit: false, autoPush: false, autoCreatePullRequest: false,
    buildProject: true, retryOnWorkflowAgent: false, includeCodingRules: false, codeGraphEnabled: true,
  })) assert.equal(get(`[name="${name}"]`).checked, expected, name);
  assert.equal(get('[name="mrReviewMode"][value="independent"]').checked, true);
  assert.equal(get('#autonomy-level').value, '100');
  assert.equal(get('#task-complexity').value, '1');
  cards.forEach(card => {
    assert.equal(card.fields.provider.value, 'deepseek');
    assert.equal(card.fields['max-agent-turns'].value, card.dataset.agent === 'developer' ? '120' : '60');
    assert.equal(card.fields.prompt.value, '');
    for (const option of card.fields.provider.options) {
      assert.equal(option.disabled, ['claude', 'openai'].includes(option.value));
    }
    for (const field of ['tools', 'max-agent-turns', 'prompt']) {
      assert.ok('data-advanced-only' in card.fields[field].attributes);
    }
  });
}
verifySimple();
get('[name="runExperience"]:checked').value = 'advanced';
get('#run-experience').handlers.change();
assert.equal(context.document.documentElement.dataset.runExperience, 'advanced');
assert.equal(get('[name="includeCodingRules"]').checked, true);
assert.equal(get('[name="codeGraphEnabled"]').checked, true);
cards.forEach(card => assert.ok(card.fields.provider.options.every(option => !option.disabled && !option.hidden)));
// Advanced settings must be reset before they become hidden again.
get('[name="autoPush"]').checked = true;
get('[name="codeGraphEnabled"]').checked = false;
get('[name="includeCodingRules"]').checked = true;
get('[name="buildProject"]').checked = false;
get('[name="jiraValidationEnabled"]').checked = false;
get('#task-complexity').value = '2';
cards.forEach(card => { card.fields.prompt.value = 'Custom guidance'; card.fields.provider.value = 'openai'; });
get('[name="runExperience"]:checked').value = 'simple';
get('#run-experience').handlers.change();
verifySimple();
assert.equal(summaries, 3, 'Refresh step 5 after each mode change');
assert.equal(requiredSyncs, 3, 'Validate keys for the providers offered by the mode');
assert.match(html, /name="runExperience" type="radio" value="simple" checked/);
assert.match(app, /control\.closest\?\.\('\[data-advanced-only\]'\)/);
assert.match(app, /\['autonomy', 'verification', 'retry', 'limits', 'coding-rules', 'analysis'\]\.includes\(category\)/);
assert.match(app, /analysisSettings\.setAttribute\('data-advanced-only', ''\)/);
assert.match(html, /id="codegraph-enabled" name="codeGraphEnabled" type="checkbox" checked>/);
get('[name="codeGraphEnabled"]').disabled = true;
get('#run-experience').handlers.change();
assert.equal(get('[name="codeGraphEnabled"]').checked, false, 'Respect server-disabled CodeGraph in Simple Run');
assert.match(fs.readFileSync(path.join(root, 'styles.css'), 'utf8'), /html\[data-run-experience="simple"\] \[data-advanced-only\] \{ display: none !important; \}/);
console.log('Simple/Advanced Run defaults, mode transitions, providers, and summary visibility passed.');
