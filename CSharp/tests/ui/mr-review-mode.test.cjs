const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const nodes = Object.fromEntries(['input[name="runMode"]:checked', '#mr-review-settings', '#merge-request-iid', '#task']
  .map(id => [id, { value: '', required: true }]));
const sourceRadios = [{ value: 'github' }, { value: 'gitlab' }];
const context = {
  document: {
    querySelector: id => id === '#source-control-gitlab' ? sourceRadios[1] : nodes[id],
    querySelectorAll: () => sourceRadios,
  },
  baseBranchInput: {}, sourceControlInput: {}, setRepositorySource(value) { this.provider = value; },
};
vm.createContext(context);
const start = source.indexOf('function syncMergeRequestMode()');
const end = source.indexOf("document.querySelector('#use-saved-gitlab-token')", start);
vm.runInContext(source.slice(start, end), context);
nodes['input[name="runMode"]:checked'].value = 'gitlab_review';
context.syncMergeRequestMode();
assert.equal(nodes['#mr-review-settings'].hidden, false);
assert.equal(nodes['#merge-request-iid'].required, true);
assert.equal(nodes['#task'].required, false);
assert.equal(context.sourceControlInput.value, 'gitlab');
assert.equal(sourceRadios[1].checked, true);
assert.ok(sourceRadios.every(input => input.disabled));
assert.equal(context.baseBranchInput.disabled, true);
nodes['input[name="runMode"]:checked'].value = 'change';
context.syncMergeRequestMode();
assert.equal(nodes['#task'].required, true);
assert.equal(context.baseBranchInput.disabled, false);
assert.equal(context.sourceControlInput.disabled, false);
assert.ok(sourceRadios.every(input => !input.disabled));
const requestContext = {
  connection: { sourceControl: 'gitlab', repositoryUrl: 'https://gitlab.com/acme/orders.git', accessToken: 'token' },
  values: new Map([['runMode', 'gitlab_review'], ['mergeRequestIid', '17'], ['task', ''], ['includeResolvedDiscussions', 'on']]),
  agents: {}, referenceCollectionId: null, cognia: null, changeRequestSource: 'manual',
};
vm.createContext(requestContext);
vm.runInContext(require('./request-payload.cjs')(source), requestContext);
assert.equal(requestContext.request.runMode, 'gitlab_review');
assert.equal(requestContext.request.mergeRequestIid, 17);
assert.equal(requestContext.request.includeResolvedDiscussions, true);
assert.equal(requestContext.request.baseBranch, '');
assert.equal(requestContext.request.task, '');
console.log('MR review mode selects GitLab and permits branch and task discovery.');
