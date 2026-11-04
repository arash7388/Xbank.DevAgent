const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8').replace(/\r\n/g, '\n');
function input(id, value, min = 1, max = 2147483647) {
  return { id, value: String(value), min: String(min), max: String(max), validity: { valid: true }, handlers: {},
    addEventListener(event, callback) { this.handlers[event] = callback; },
    setAttribute(name, value) { this[name] = value; } };
}
const slider = input('task-complexity', 1, 0, 2);
const label = {};
const limits = [input('max-ai-calls', 200), input('max-budget-usd', 0.1, 0.01, 1000000),
  input('planner', 60), input('developer', 120), input('disabled-agent', 60)];
const context = { document: {
  querySelector: selector => selector === '#task-complexity' ? slider : label,
  querySelectorAll: () => limits,
} };
vm.createContext(context);
const start = source.indexOf('function initializeTaskComplexity()');
const end = source.indexOf('\n}\n', start) + 3;
vm.runInContext(source.slice(start, end) + '\ninitializeTaskComplexity();', context);
function select(level, expected) {
  slider.value = String(level);
  slider.handlers.input();
  assert.deepEqual(limits.map(control => Number(control.value)), expected);
  assert.equal(slider['aria-valuetext'], label.textContent);
}
select(0, [100, 0.05, 30, 60, 30]);
assert.equal(label.textContent, 'Simple — 0.5×');
select(2, [600, 0.3, 180, 360, 180]);
select(1, [200, 0.1, 60, 120, 60]);
// Custom limits follow the selected complexity and round trips do not accumulate rounding.
limits[2].value = '61';
limits[2].handlers.input();
select(0, [100, 0.05, 31, 60, 30]);
select(2, [600, 0.3, 183, 360, 180]);
select(1, [200, 0.1, 61, 120, 60]);
limits[0].value = '1';
limits[0].handlers.input();
limits[1].value = '0.01';
limits[1].handlers.input();
select(0, [1, 0.01, 31, 60, 30]);
select(2, [3, 0.03, 183, 360, 180]);
const payloadContext = { connection: {}, agents: {}, referenceCollectionId: null, cognia: null, changeRequestSource: 'manual',
  values: new Map([['branch', 'main'], ['task', 'Change'], ['maxAiCalls', '600'], ['maxBudgetUsd', '0.3']]) };
vm.createContext(payloadContext);
const payload = require('./request-payload.cjs')(source);
for (const [value, name] of [[null, 'normal'], ['0', 'simple'], ['1', 'normal'], ['2', 'hard']]) {
  payloadContext.values.set('taskComplexity', value);
  vm.runInContext(payload, payloadContext);
  assert.equal(payloadContext.request.taskComplexity, name);
  assert.equal(payloadContext.request.maxAiCalls, 600);
  assert.equal(payloadContext.request.maxBudgetUsd, 0.3);
}
console.log('Task complexity scaling, custom limits, rounding, accessibility, and payload passed.');
