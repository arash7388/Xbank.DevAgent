const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const html = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/index.html'), 'utf8');
assert.match(html, /<input\b[^>]*id="branch"[^>]*list="repository-branches"[^>]*required/);
assert.match(html, /<datalist id="repository-branches"><\/datalist>/);
assert.doesNotMatch(html, /<select\b[^>]*id="repository-branches"/);
const controls = {
  '#gitlab-repository': { value: 'https://gitlab.example.com/team/coffer.git', checkValidity: () => true },
  '#gitlab-token': { value: '' },
  '#github-repository': { value: 'https://github.com/team/mcp.git', checkValidity: () => true },
  '#github-token': { value: 'test-token' },
  '#use-saved-gitlab-token': { checked: false },
};
const picker = {
  options: [],
  replaceChildren() { this.options = []; },
  append(option) { this.options.push(option); },
};
const context = {
  sourceControlInput: { value: 'gitlab' },
  baseBranchInput: { value: 'develop' },
  repositoryBranches: picker,
  branchStatus: { textContent: '' },
  branchRequestVersion: 0,
  loadOpenMergeRequests() {},
  document: { querySelector: selector => controls[selector] },
  Option: class { constructor(text, value) { this.text = text; this.value = value; } },
  api: async (url, options) => {
    assert.equal(url, '/api/repositories/branches');
    assert.deepEqual(JSON.parse(options.body), {
      repositoryUrl: controls['#gitlab-repository'].value, sourceControl: 'gitlab', accessToken: null,
    });
    return ['Develop', 'main'];
  },
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('function readRepositoryConnection()'),
  source.indexOf('async function loadRepositoryBranches()')), context);
const start = source.indexOf('async function loadRepositoryBranches(');
const end = source.indexOf('\nasync function loadOpenMergeRequests(', start);
vm.runInContext(source.slice(start, end), context);

(async () => {
  await context.loadRepositoryBranches();
  assert.equal(context.baseBranchInput.value, 'Develop');
  assert.deepEqual(picker.options.map(option => option.value), ['Develop', 'main']);

  context.sourceControlInput.value = 'github';
  context.api = async (url, options) => {
    assert.equal(JSON.parse(options.body).accessToken, 'test-token');
    return ['develop'];
  };
  await context.loadRepositoryBranches();
  assert.equal(context.baseBranchInput.value, 'develop');

  let finishOldRequest;
  context.api = () => new Promise(resolve => { finishOldRequest = resolve; });
  const oldRequest = context.loadRepositoryBranches();
  context.api = async () => ['main'];
  await context.loadRepositoryBranches();
  finishOldRequest(['stale-branch']);
  await oldRequest;
  assert.deepEqual(picker.options.map(option => option.value), ['main']);

  context.api = async () => { throw new Error('Authentication failed'); };
  await context.loadRepositoryBranches();
  assert.equal(picker.options.length, 0);
  assert.match(context.branchStatus.textContent, /Authentication failed/);
  assert.equal(context.baseBranchInput.value, 'develop');

  controls['#github-repository'].value = '';
  context.api = () => { throw new Error('Must not fetch without a repository'); };
  await context.loadRepositoryBranches();
  assert.match(context.branchStatus.textContent, /Select a repository/);
  console.log('Repository branch picker checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
