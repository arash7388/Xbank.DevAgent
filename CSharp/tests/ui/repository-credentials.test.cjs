const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const controls = {
  '#use-saved-gitlab-token': { checked: true },
  '#gitlab-token': { value: 'stale-browser-value', disabled: false },
  '#github-token': { value: ' github-token ' },
  '#gitlab-repository': { value: ' https://gitlab.example/group/repo.git ' },
  '#github-repository': { value: 'https://github.com/owner/repo.git' },
};
const calls = [];
let submit;
const context = {
  sourceControlInput: { value: 'gitlab' },
  document: { querySelector: selector => controls[selector] },
  form: { addEventListener: (event, callback) => { submit = callback; } },
  runButton: { disabled: false },
  currentSetupStep: 'confirm',
  FormData: class { get() { return ''; } },
  clearError() {}, stopPolling() {}, resetOutput() {}, startElapsedTimer() {}, resetElapsedTimer() {},
  setBusy(value) { context.runButton.disabled = value; },
  readAgentConfigurations() { return {}; },
  selectedReferenceFiles() { return []; },
  showError(message) { context.error = message; },
  setSetupStep(step) { context.step = step; },
  uploadReferenceDocuments() { throw new Error('Must check repository access before uploading documents'); },
  async api(url, options) {
    calls.push({ url, body: JSON.parse(options.body) });
    if (url === '/api/runs/preflight') return { status: 'ok' };
    throw new Error('GitLab rejected the token');
  },
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('function syncSavedGitLabToken()'),
  source.indexOf('async function loadRepositoryBranches()')), context);

context.syncSavedGitLabToken();
assert.equal(controls['#gitlab-token'].disabled, true);
assert.equal(context.readRepositoryConnection().accessToken, null);
assert.equal(context.readRepositoryConnection().repositoryUrl, 'https://gitlab.example/group/repo.git');

controls['#use-saved-gitlab-token'].checked = false;
context.syncSavedGitLabToken();
assert.equal(controls['#gitlab-token'].disabled, false);
assert.equal(context.readRepositoryConnection().accessToken, 'stale-browser-value');
controls['#gitlab-token'].value = '  ';
assert.equal(context.readRepositoryConnection().accessToken, null);

context.sourceControlInput.value = 'github';
controls['#use-saved-gitlab-token'].checked = true;
assert.equal(context.readRepositoryConnection().accessToken, 'github-token');

context.sourceControlInput.value = 'gitlab';
controls['#gitlab-token'].value = 'stale-browser-value';
const submitStart = source.indexOf("form.addEventListener('submit'");
vm.runInContext(source.slice(submitStart, source.indexOf('\nfunction readAgentConfigurations(', submitStart)), context);

(async () => {
  await submit({ preventDefault() {}, submitter: context.runButton });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].url, '/api/runs/preflight');
  assert.equal(calls[1].url, '/api/repositories/branches');
  assert.equal(calls[1].body.accessToken, null);
  assert.equal(context.step, 'connection');
  assert.match(context.error, /GitLab rejected the token/);
  assert.equal(context.runButton.disabled, false);

  // Branch loading and workflow startup must resolve the same saved credential choice.
  context.FormData = class {
    get(name) {
      return { branch: 'develop', task: 'WEPOD-1 Test', maxAiCalls: '1', maxBudgetUsd: '0.01' }[name] || '';
    }
  };
  context.readCogniaConfiguration = () => null;
  context.uploadReferenceDocuments = async () => null;
  context.changeRequestSource = 'manual';
  context.setActiveStep = () => {};
  context.render = () => {};
  context.setInterval = () => 1;
  context.refresh = () => {};
  context.api = async (url, options) => {
    calls.push({ url, body: JSON.parse(options.body) });
    return url === '/api/repositories/branches' ? ['develop'] : { id: 'test-session' };
  };
  await submit({ preventDefault() {}, submitter: context.runButton });
  assert.equal(calls[2].url, '/api/runs/preflight');
  assert.equal(calls[3].url, '/api/repositories/branches');
  assert.equal(calls[4].url, '/api/sessions');
  assert.equal(calls[4].body.gitLabToken, calls[3].body.accessToken);
  assert.equal(calls[4].body.repositoryUrl, calls[3].body.repositoryUrl);
  assert.equal(calls[4].body.sourceControl, calls[3].body.sourceControl);
  console.log('Repository credential UI checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
