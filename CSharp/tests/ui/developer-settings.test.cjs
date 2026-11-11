const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

function node(tag) {
  return {
    tag, children: [], handlers: {}, dataset: {}, value: '', checked: false, disabled: false,
    append(...items) { this.children.push(...items); items.forEach(item => { if (typeof item === 'object') item.parent = this; }); },
    replaceChildren() { this.children = []; },
    remove() { this.parent.children = this.parent.children.filter(item => item !== this); },
    addEventListener(type, handler) { this.handlers[type] = handler; },
    get lastElementChild() { return this.children[this.children.length - 1]; },
    focus() { this.focused = true; },
    querySelector(selector) {
      if (selector === '[type="submit"]') return submit;
      return this.children.reduce((found, item) => found || (item.tag === selector ? item : item.querySelector?.(selector)), null);
    },
    querySelectorAll(selector) {
      const type = selector.includes('password') ? 'password' : 'checkbox';
      const found = [];
      function visit(item) { if (selector === 'fieldset' ? item.tag === 'fieldset' : item.type === type) found.push(item); (item.children || []).forEach(visit); }
      visit(this);
      return found;
    },
    showModal() { this.open = true; },
    close() { this.open = false; this.handlers.close?.(); },
    reset() {},
  };
}
const submit = node('button');
const nodes = new Map();
const get = selector => {
  if (!nodes.has(selector)) nodes.set(selector, node());
  return nodes.get(selector);
};
let fail = false;
const calls = [];
const settings = { gitLabHost: 'gitlab.test', jiraBaseUrl: 'https://jira.test', jiraUser: '', jiraAuthMode: 'Bearer',
  hasGitLabToken: true, hasJiraToken: true,
  repositories: [{ key: '<script>example</script>', repoPath: 'C:\\repo', gitLabProjectPath: 'https://gitlab.test/team/repo.git', hasToken: true, database: 'RepositoryDb', defaultBaseBranch: 'release/service' }] };
const context = {
  document: { querySelector: get, createElement: node, createTextNode: text => ({ textContent: text }) },
  Event: class { constructor(type) { this.type = type; } },
  window: { location: { reload() {} }, dispatchEvent() {} },
  fetch: async (url, options) => {
    calls.push({ url, ...options });
    return { ok: !fail, status: options ? 204 : 200,
      json: async () => fail ? { error: 'Save failed' } : settings };
  },
};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/developer-settings.js'), 'utf8'), context);
const event = async (selector, type = 'click') => get(selector).handlers[type]({ preventDefault() {} });
const field = name => get(`#settings-${name}`);
const payload = () => JSON.parse(calls[calls.length - 1].body);

(async () => {
  await event('#developer-settings-button');
  assert.equal(get('#developer-settings-dialog').open, true);
  assert.equal(field('gitlab-token').value, '', 'Saved secrets are never prefilled');
  assert.match(field('gitlab-token').placeholder, /saved/);
  const row = field('repositories').children[0];
  assert.equal(row.children[1].children[0].value, '<script>example</script>', 'Names use input values, not HTML');
  await event('#developer-settings-form', 'submit');
  assert.equal(payload().gitLabToken, null);
  assert.equal(payload().jiraToken, null);
  assert.equal(payload().repositories[0].token, null);
  assert.equal(payload().repositories[0].database, 'RepositoryDb');
  assert.equal(row.children[6].children[0].value, 'release/service');
  assert.equal(payload().repositories[0].defaultBaseBranch, 'release/service');
  row.children[6].children[0].value = ' develop ';
  row.children[5].children[0].value = ' ChangedDb ';
  assert.equal(payload().sqlServer.connectionString, null);
  assert.equal(payload().sqlServer.password, null);
  assert.equal(payload().sqlServer.enabled, false);
  assert.equal(payload().sqlServer.useConnectionString, false);
  assert.equal(payload().sqlServer.useWindowsAuthentication, true);
  assert.equal(payload().sqlServer.trustServerCertificate, false);
  assert.equal(field('sql-credentials').hidden, true);

  field('sql-advanced').checked = true;
  await event('#settings-sql-advanced', 'change');
  assert.equal(field('sql-simple-fields').hidden, true);
  assert.equal(field('sql-credentials').hidden, false);
  field('sql-connection').value = 'Server=test;Database=test;Integrated Security=True;';
  field('sql-password').value = ' whitespace password ';
  field('sql-enabled').checked = true;
  await event('#developer-settings-form', 'submit');
  assert.equal(payload().sqlServer.password, ' whitespace password ');
  assert.equal(payload().sqlServer.enabled, true);
  assert.equal(payload().repositories[0].database, 'ChangedDb');
  assert.equal(payload().repositories[0].defaultBaseBranch, 'develop');
  assert.equal(field('sql-password').value, '');
  assert.equal(field('sql-connection').value, '');

  field('sql-advanced').checked = false;
  field('sql-server').value = 'SERVER\\INSTANCE';
  field('sql-database').value = 'MyDatabase';
  field('sql-trust-certificate').checked = true;
  await event('#settings-sql-advanced', 'change');
  assert.equal(field('sql-credentials').hidden, true);
  assert.equal(field('sql-user').disabled, true);
  await event('#developer-settings-form', 'submit');
  assert.equal(payload().sqlServer.server, 'SERVER\\INSTANCE');
  assert.equal(payload().sqlServer.database, 'MyDatabase');
  assert.equal(payload().sqlServer.trustServerCertificate, true);
  assert.equal(payload().sqlServer.useWindowsAuthentication, true);
  assert.equal(payload().sqlServer.useConnectionString, false);
  assert.equal(field('sql-user').disabled, true, 'Windows credentials remain disabled after save');
  field('sql-windows').checked = false;
  await event('#settings-sql-windows', 'change');
  assert.equal(field('sql-credentials').hidden, false);
  assert.equal(field('sql-user').required, true);
  field('sql-user').value = 'reader';
  await event('#developer-settings-form', 'submit');
  assert.equal(payload().sqlServer.useWindowsAuthentication, false);
  assert.equal(payload().sqlServer.username, 'reader');

  field('gitlab-clear').checked = true;
  await event('#settings-gitlab-clear', 'change');
  assert.equal(field('gitlab-token').disabled, true, 'Clearing a credential disables its input');
  field('jira-token').value = 'new-jira-token';
  row.children[7].children[0].checked = true;
  await event('#developer-settings-form', 'submit');
  assert.equal(payload().gitLabToken, '');
  assert.equal(payload().jiraToken, 'new-jira-token');
  assert.equal(payload().repositories[0].token, '');
  assert.equal(field('jira-token').value, '', 'Successful save clears token inputs');
  assert.equal(field('gitlab-token').placeholder, 'No shared token saved');
  assert.equal(field('gitlab-token').disabled, false);
  assert.equal(row.children[4].children[0].placeholder, 'Use shared token');
  assert.match(field('jira-token').placeholder, /Credential saved/);
  assert.equal(get('#developer-settings-status').dataset.state, 'success');
  assert.equal(field('reload').hidden, false);

  field('jira-auth').value = 'Basic';
  await event('#settings-jira-auth', 'change');
  assert.equal(field('jira-user').required, true);
  field('jira-auth').value = 'Bearer';
  await event('#settings-jira-auth', 'change');
  assert.equal(field('jira-user').required, false);

  fail = true;
  field('jira-token').value = 'retry-secret';
  await event('#developer-settings-form', 'submit');
  assert.equal(field('jira-token').value, 'retry-secret', 'Failed save preserves draft');
  assert.equal(get('#developer-settings-status').textContent, 'Save failed');
  assert.equal(get('#developer-settings-status').dataset.state, 'error');
  assert.equal(submit.disabled, false);
  row.children[8].handlers.click();
  assert.equal(field('repositories').children.length, 0);
  await event('#settings-add-repository');
  const addedRow = field('repositories').lastElementChild;
  assert.equal(addedRow.querySelector('input').focused, true, 'Adding a repository focuses its name');
  await event('#developer-settings-close');
  assert.equal(get('#developer-settings-dialog').open, false);

  let finishLoad;
  context.fetch = () => new Promise(resolve => { finishLoad = resolve; });
  const loading = event('#developer-settings-button');
  await event('#developer-settings-close');
  finishLoad({ ok: true, status: 200, json: async () => settings });
  await loading;
  assert.equal(field('repositories').children.length, 0, 'A late response cannot repopulate a closed dialog');
  console.log('Developer settings UI tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
