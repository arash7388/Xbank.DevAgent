const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const cards = ['planner', 'developer', 'reviewer', 'release_manager', 'security'].map(agent => {
  const fields = {
    enabled: { checked: agent !== 'security' },
    provider: { value: agent === 'security' ? 'deepseek' : 'aifa' },
    model: { value: 'test-model' }, effort: { disabled: true },
    prompt: { value: '' }, 'max-agent-turns': { value: '60' },
  };
  return { dataset: { agent }, fields,
    querySelector: selector => fields[selector.match(/data-field="([^"]+)"/)[1]] };
});
const providerKeys = Object.fromEntries(['deepseek', 'aifa', 'openai', 'claude'].map(id => {
  const classes = new Set();
  return [id, { value: id === 'aifa' ? 'aifa-token' : '',
    classList: { toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); } }, classes }];
}));
const context = { agentModels: { querySelectorAll: () => cards }, providerKeys,
  configuredProviderKeys: new Set(),
  document: { querySelector: () => ({ querySelector: () => ({ value: '' }) }) },
  providerCatalog: {
    aifa: { name: 'AIFA', supportsReasoningEffort: [] },
    deepseek: { name: 'DeepSeek', supportsReasoningEffort: [] },
  } };
vm.createContext(context);
for (const [start, end] of [
  ['function isAgentCardEnabled(', 'function syncAgentCardEnabled('],
  ['function syncRequiredProviderKeys(', 'function applyProviderToAllAgents('],
  ['function readAgentConfigurations(', 'cancelButton.addEventListener('],
]) vm.runInContext(source.slice(source.indexOf(start), source.indexOf(end)), context);

context.syncRequiredProviderKeys();
assert.equal(providerKeys.deepseek.required, false, 'Disabled DeepSeek agents need no key');
assert.equal(providerKeys.deepseek.classes.has('required-key'), false);
assert.equal(providerKeys.aifa.required, true);
assert.deepEqual(Object.keys(context.readAgentConfigurations()), ['planner', 'developer', 'reviewer', 'release_manager']);

cards[4].fields.enabled.checked = true;
context.syncRequiredProviderKeys();
assert.equal(providerKeys.deepseek.required, true, 'An enabled DeepSeek agent needs a key');
assert.throws(() => context.readAgentConfigurations(), /API key for DeepSeek/);

context.configuredProviderKeys.add('deepseek');
context.syncRequiredProviderKeys();
assert.equal(providerKeys.deepseek.required, false, 'Server credentials satisfy the requirement');
context.configuredProviderKeys.clear();
cards[4].fields.provider.value = 'aifa';
context.syncRequiredProviderKeys();
assert.equal(providerKeys.deepseek.required, false, 'Switching the last DeepSeek agent clears the requirement');
assert.equal(providerKeys.deepseek.classes.has('required-key'), false);
assert.equal(Object.keys(context.readAgentConfigurations()).length, 5);
providerKeys.aifa.value = '';
assert.throws(() => context.readAgentConfigurations(), /API key for AIFA/);
console.log('Provider keys are required only for enabled agents, with server credential fallback.');
