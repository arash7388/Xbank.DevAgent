const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const nodes = new Map();
const get = selector => {
  if (!nodes.has(selector)) nodes.set(selector, { value: '', textContent: '', checked: false, handlers: {},
    disabled: false, hidden: false, addEventListener(type, handler) { this.handlers[type] = handler; },
    querySelectorAll() { return []; } });
  return nodes.get(selector);
};
const field = name => get(`#sqlserver-${name}`);
field('operation').value = 'schema';
field('parameters').value = '{}';
const calls = [];
let enabled = false;
let fail = false;
const context = {
  URLSearchParams,
  document: { querySelector: get },
  window: { location: { search: '' }, addEventListener() {} },
  fetch: async (url, options) => {
    const body = options?.body ? JSON.parse(options.body) : null;
    calls.push({ url, body, method: options?.method });
    if (url.endsWith('/enabled') && !fail) enabled = body.enabled;
    return { ok: !fail, status: url.endsWith('/enabled') && !fail ? 204 : 200,
      json: async () => fail ? { error: 'Access unavailable' }
        : url.endsWith('/execute') ? { data: '<script>untrusted row</script>', row_count: 1 } : { enabled } };
  },
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/sql-server.js'), 'utf8'), context);
async function event(node, type = 'click') {
  node.handlers[type]({ preventDefault() {} });
  await new Promise(setImmediate);
}
(async () => {
  await event(get('[data-app-mode="sqlserver"]'));
  assert.equal(field('enabled').checked, false);
  field('enabled').checked = true;
  await event(field('enabled'), 'change');
  assert.equal(calls[calls.length - 1].method, 'PUT');
  assert.equal(enabled, true);
  await event(field('connect'));
  assert.match(calls[calls.length - 1].body.sql, /DB_NAME/);
  assert.match(field('status').textContent, /Connected/);
  assert.match(field('result').textContent, /<script>/, 'Database content renders as plain text');

  field('operation').value = 'query';
  await event(field('operation'), 'change');
  assert.equal(field('query-fields').hidden, false);
  field('parameters').value = '[]';
  const count = calls.length;
  await event(field('form'), 'submit');
  assert.equal(calls.length, count, 'Invalid parameter shape never reaches the API');
  field('parameters').value = '{"id":7}';
  field('query').value = 'SELECT @id AS id';
  await event(field('form'), 'submit');
  assert.deepEqual(calls[calls.length - 1].body.parameters, { id: 7 });
  assert.equal(calls[calls.length - 1].body.estimated, true);

  fail = true;
  field('enabled').checked = false;
  await event(field('enabled'), 'change');
  assert.equal(field('enabled').checked, true, 'Failed disable restores current state');
  assert.equal(field('status').textContent, 'Access unavailable');
  assert.equal(field('connect').disabled, false);
  assert.equal(field('result').textContent, '', 'Failures clear stale results');
  console.log('SQL Server workbench: enablement, connection, parameters, safe results, and failure recovery pass.');
})().catch(error => { console.error(error); process.exitCode = 1; });
