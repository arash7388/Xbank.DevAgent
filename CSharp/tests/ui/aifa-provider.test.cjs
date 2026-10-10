const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const fields = {
  provider: { value: 'aifa' }, model: { value: 'developer-model' },
  effort: { value: 'low', disabled: false }, prompt: { value: '' }, enabled: { checked: true },
};
const card = { dataset: { agent: 'planner' }, querySelector(selector) {
  return fields[selector.match(/data-field="([^"]+)"/)[1]];
} };
const prices = { querySelector() { return { value: '' }; } };
const context = {
  agentModels: { querySelectorAll() { return [card]; }, querySelector() { return prices; } },
  providerKeys: { aifa: { value: '' } }, configuredProviderKeys: new Set(),
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('const providerCatalog ='), source.indexOf('const agentCatalog =')), context);
vm.runInContext(source.slice(source.indexOf('function isAgentCardEnabled('), source.indexOf('function syncAgentCardEnabled(')), context);
vm.runInContext(source.slice(source.indexOf('function readAgentConfigurations('), source.indexOf("cancelButton.addEventListener(")), context);

assert.throws(() => context.readAgentConfigurations(), /An API key for AIFA is required/);
context.configuredProviderKeys.add('aifa');
const configured = context.readAgentConfigurations().planner;
assert.equal(configured.apiKey, '');
assert.equal(configured.provider.name, 'AIFA');
assert.equal(configured.provider.url, 'https://aifa-chatbot.dev.dotin.ir/v1');
assert.equal(configured.provider.id, 'developer-model');
assert.equal(configured.provider.thinking, true);
assert.equal(configured.provider.reasoningEffort, 'low');
fields.effort.value = 'xhigh';
assert.equal(context.readAgentConfigurations().planner.provider.reasoningEffort, 'xhigh');
assert.equal(configured.provider.inputCostPerMillionTokens, null);
context.providerKeys.aifa.value = ' entered-token ';
assert.equal(context.readAgentConfigurations().planner.apiKey, 'entered-token');
fields.enabled.checked = false;
assert.equal(Object.keys(context.readAgentConfigurations()).length, 0);
console.log('AIFA provider UI checks passed.');
