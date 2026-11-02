const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const root = path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot');
const source = fs.readFileSync(path.join(root, 'coding-rules.js'), 'utf8');

function node(tag) {
  return {
    tag, children: [], handlers: {}, value: '', hidden: false, textContent: '', disabled: false,
    append(...items) { this.children.push(...items); },
    replaceChildren(...items) { this.children = items; },
    setAttribute() {}, focus() {},
    addEventListener(event, handler) { this.handlers[event] = handler; },
    querySelectorAll() { return []; },
    showModal() { this.open = true; }, close() { this.open = false; },
  };
}
const nodes = new Map();
const get = selector => {
  if (!nodes.has(selector)) nodes.set(selector, node());
  return nodes.get(selector);
};
get('#coding-rule-form').reset = () => {
  get('#coding-rule-title').value = '';
  get('#coding-rule-content').value = '';
};
let rules = [], calls = [], fail = false;
const context = {
  document: { querySelector: get, createElement: node },
  window: { confirm: () => true },
  fetch: async (url, options) => {
    calls.push({ url, ...options });
    if (fail) return { ok: false, status: 500, json: async () => ({ error: 'Storage unavailable' }) };
    const body = options.body && JSON.parse(options.body);
    if (options.method === 'POST') rules.push({ id: 'rule-1', enabled: true, ...body });
    if (options.method === 'PUT' && url.endsWith('/enabled')) rules[0].enabled = body.enabled;
    else if (options.method === 'PUT') rules = [{ ...rules[0], ...body }];
    if (options.method === 'DELETE') rules = [];
    return { ok: true, status: options.method === 'DELETE' ? 204 : 200,
      json: async () => options.method === 'GET' ? rules : rules[0] };
  },
};
vm.createContext(context);
vm.runInContext(source, context);
async function event(selector, type = 'click') {
  get(selector).handlers[type]({ preventDefault() {} });
  await new Promise(setImmediate);
}
async function click(button) {
  button.handlers.click();
  await new Promise(setImmediate);
}
function table() { return get('#coding-rules-list').children[0].children[0]; }
function firstRow() { return table().children[1].children[0]; }
(async () => {
  await event('#coding-rules-button');
  assert.equal(get('#coding-rules-dialog').open, true);
  assert.match(get('#coding-rules-status').textContent, /No coding rules/);
  get('#coding-rule-title').value = '<img src=x onerror=alert(1)>';
  get('#coding-rule-content').value = 'Use PascalCase.';
  await event('#coding-rule-form', 'submit');
  assert.equal(table().tag, 'table');
  assert.deepEqual(table().children[0].children[0].children.map(cell => cell.textContent), ['Title', 'Rule', 'Enabled', 'Actions']);
  let item = firstRow();
  assert.equal(item.children[0].textContent, '<img src=x onerror=alert(1)>');
  assert.equal(item.children[0].children.length, 0, 'Rule titles render as text');
  const toggle = item.children[2].children[0];
  assert.equal(toggle.checked, true);
  toggle.checked = false;
  toggle.handlers.change();
  await new Promise(setImmediate);
  assert.equal(rules[0].enabled, false);
  assert.equal(calls.find(call => call.url.endsWith('/enabled')).method, 'PUT');
  fail = true;
  toggle.checked = true;
  toggle.handlers.change();
  await new Promise(setImmediate);
  assert.equal(toggle.checked, false, 'Failed toggle restores saved state');
  fail = false;
  await click(item.children[3].children[0].children[0]);
  assert.equal(get('#coding-rule-content').value, 'Use PascalCase.');
  get('#coding-rule-content').value = 'Use camelCase.';
  await event('#coding-rule-form', 'submit');
  assert.ok(calls.some(call => call.method === 'PUT' && call.url === '/api/coding-rules/rule-1'));
  assert.equal(firstRow().children[1].textContent, 'Use camelCase.');
  assert.equal(firstRow().children[2].children[0].checked, false, 'Editing preserves disabled state');
  fail = true;
  get('#coding-rule-title').value = 'Preserve draft';
  get('#coding-rule-content').value = 'Draft rule';
  await event('#coding-rule-form', 'submit');
  assert.equal(get('#coding-rules-error').textContent, 'Storage unavailable');
  assert.equal(get('#coding-rules-error').hidden, false);
  assert.equal(get('#coding-rule-content').value, 'Draft rule');
  fail = false;
  await click(firstRow().children[3].children[0].children[1]);
  assert.equal(get('#coding-rules-list').children.length, 0);
  await event('#coding-rules-close');
  assert.equal(get('#coding-rules-dialog').open, false);

  const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const payload = require('./request-payload.cjs')(app);
  const input = { connection: {}, agents: {}, referenceCollectionId: null, cognia: null,
    changeRequestSource: 'manual', values: new Map([['branch', 'main'], ['task', 'Update'], ['maxAiCalls', '200'], ['maxBudgetUsd', '0.1']]) };
  vm.createContext(input);
  vm.runInContext(payload, input);
  assert.equal(input.request.includeCodingRules, false);
  input.values.set('includeCodingRules', 'on');
  vm.runInContext(payload, input);
  assert.equal(input.request.includeCodingRules, true);
  console.log('Coding Rules: CRUD, safe rendering, draft preservation, errors, and run selection pass.');
})().catch(error => { console.error(error); process.exitCode = 1; });
