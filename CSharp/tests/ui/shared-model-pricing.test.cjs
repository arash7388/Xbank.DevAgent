const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const rate = value => ({ value, dataset: {} });
const prices = {
  'deepseek/deepseek-flash': { 'input-cost': rate('0.25'), 'cached-input-cost': rate('0.01'), 'output-cost': rate('0.8') },
  'openai/gpt-5.4-mini': { 'input-cost': rate('1'), 'cached-input-cost': rate(''), 'output-cost': rate('4') },
};
const cards = ['planner', 'developer'].map(agent => {
  const fields = {
    provider: { value: 'deepseek' }, model: { value: 'deepseek-flash' },
    effort: { value: 'low', disabled: false }, prompt: { value: '' },
    enabled: { checked: true }, 'max-agent-turns': { value: '60' },
  };
  return { fields, dataset: { agent }, querySelector: selector => fields[selector.match(/data-field="([^"]+)"/)[1]] };
});
const context = {
  agentModels: { querySelectorAll: () => cards },
  providerKeys: { deepseek: { value: 'key' }, openai: { value: 'key' } },
  configuredProviderKeys: new Set(),
  document: { querySelector(selector) {
    const [, provider, model] = selector.match(/data-pricing-provider="([^"]+)"\]\[data-pricing-model="([^"]+)"/);
    return { querySelector: field => prices[`${provider}/${model}`][field.match(/data-field="([^"]+)"/)[1]] };
  } },
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('const providerCatalog ='), source.indexOf('const agentCatalog =')), context);
vm.runInContext(source.slice(source.indexOf('function isAgentCardEnabled('), source.indexOf('function syncAgentCardEnabled(')), context);
vm.runInContext(source.slice(source.indexOf('function readAgentConfigurations('), source.indexOf('cancelButton.addEventListener(')), context);
vm.runInContext(source.slice(source.indexOf('function syncDefaultTokenPrice('), source.indexOf('function syncAgentCard(')), context);
let agents = context.readAgentConfigurations();
assert.equal(agents.planner.provider.inputCostPerMillionTokens, 0.25);
assert.equal(agents.developer.provider.inputCostPerMillionTokens, 0.25);
prices['deepseek/deepseek-flash']['input-cost'].value = '0.33';
cards[1].fields.effort.value = 'high';
agents = context.readAgentConfigurations();
assert.equal(agents.planner.provider.inputCostPerMillionTokens, 0.33);
assert.equal(agents.developer.provider.inputCostPerMillionTokens, 0.33);
cards[1].fields.provider.value = 'openai';
cards[1].fields.model.value = 'gpt-5.4-mini';
agents = context.readAgentConfigurations();
assert.equal(agents.developer.provider.inputCostPerMillionTokens, 1);
assert.equal(agents.developer.provider.cachedInputCostPerMillionTokens, null);
assert.equal(agents.planner.provider.inputCostPerMillionTokens, 0.33);
const input = rate('');
context.syncDefaultTokenPrice(input, 0.15);
assert.equal(input.value, '0.15');
context.syncDefaultTokenPrice(input, 0.12);
assert.equal(input.value, '0.12');
input.value = '0.5';
context.syncDefaultTokenPrice(input, 0.1);
assert.equal(input.value, '0.5');
prices['deepseek/deepseek-flash']['input-cost'].value = '-1';
assert.throws(() => context.readAgentConfigurations(), /non-negative/);
console.log('Shared model prices apply across agents, model changes use matching rates, and refresh preserves overrides.');
