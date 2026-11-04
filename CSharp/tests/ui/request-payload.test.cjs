const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const payload = require('./request-payload.cjs');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');

for (const newline of ['\n', '\r\n']) {
  for (const provider of ['github', 'gitlab']) {
    for (const validationEnabled of [true, false]) {
      const context = {
        connection: { sourceControl: provider, repositoryUrl: 'https://example.test/repository', accessToken: 'test-token' },
        values: new Map([['releaseManagerValidationEnabled', validationEnabled ? 'on' : null], ['jiraValidationEnabled', validationEnabled ? 'on' : null], ['branch', ' main '], ['task', ' WEPOD-42 Update '], ['maxAiCalls', '1'], ['maxBudgetUsd', '0.01']]),
        agents: { developer: { provider: 'test' } }, referenceCollectionId: 'collection', cognia: null,
        changeRequestSource: 'jira', selectedJiraKey: 'WEPOD-42', selectedJiraAttachmentIds: ['7'],
        selectedJiraIncludeComments: false,
      };
      vm.createContext(context);
      vm.runInContext(payload(source.replace(/\r?\n/g, newline)), context);
      const request = context.request;
      assert.equal(request.sourceControl, provider);
      assert.equal(request.githubToken, provider === 'github' ? 'test-token' : null);
      assert.equal(request.gitLabToken, provider === 'gitlab' ? 'test-token' : null);
      assert.equal(request.baseBranch, 'main');
      assert.equal(request.task, 'WEPOD-42 Update');
      assert.equal(request.maxAiCalls, 1);
      assert.equal(request.maxBudgetUsd, 0.01);
      assert.equal(request.jiraIssueKey, 'WEPOD-42');
      assert.deepEqual(request.jiraAttachmentIds, ['7']);
      assert.equal(request.jiraIncludeComments, false);
      assert.equal(request.jiraValidationEnabled, validationEnabled);
      assert.equal(request.releaseManagerValidationEnabled, validationEnabled);
      assert.equal(request.referenceCollectionId, 'collection');
      assert.equal(request.autoCommit, false);
      assert.equal(request.autoPush, false);
      assert.equal(request.autoCreatePullRequest, false);
    }
  }
}
assert.throws(() => payload('const unrelated = {};'), /Could not locate/);
console.log('Request payload checks passed for both checkout line endings and source-control providers.');
