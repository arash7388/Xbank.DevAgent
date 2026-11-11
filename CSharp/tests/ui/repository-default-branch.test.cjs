const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const projects = [
  { key: 'first', gitLabProjectPath: 'https://gitlab.test/first.git', defaultBaseBranch: 'develop' },
  { key: 'second', gitLabProjectPath: 'https://gitlab.test/second.git', defaultBaseBranch: 'release/service' },
  { key: 'legacy', gitLabProjectPath: 'https://gitlab.test/legacy.git' },
];
const loaded = [];
const context = {
  api: async () => projects,
  document: { querySelector: () => ({}), createElement: () => ({ append() {} }), createTextNode: text => text },
  sourceControlInput: { value: 'gitlab' },
  projectKeyInput: { append() {}, addEventListener(type, handler) { this.change = handler; } },
  gitLabRepositoryInput: { value: '' },
  baseBranchInput: { value: '', defaultValue: '' },
  loadRepositoryBranches() { loaded.push([context.gitLabRepositoryInput.value, context.baseBranchInput.value]); },
};
vm.createContext(context);
const start = source.indexOf('async function initializeProjectSelector()');
vm.runInContext(source.slice(start, source.indexOf('\nconst eventDateFormatter', start)), context);

(async () => {
  await context.initializeProjectSelector();
  assert.deepEqual(loaded.pop(), [projects[0].gitLabProjectPath, 'develop']);
  context.projectKeyInput.change({ target: { value: 'second' } });
  assert.deepEqual(loaded.pop(), [projects[1].gitLabProjectPath, 'release/service']);
  context.baseBranchInput.value = 'manual-override';
  context.projectKeyInput.change({ target: { value: 'first' } });
  assert.deepEqual(loaded.pop(), [projects[0].gitLabProjectPath, 'develop']);
  context.projectKeyInput.change({ target: { value: 'legacy' } });
  assert.deepEqual(loaded.pop(), [projects[2].gitLabProjectPath, '']);
  console.log('Repository default base branch checks passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
