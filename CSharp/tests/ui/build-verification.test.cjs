const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const html = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/index.html'), 'utf8');
const checkbox = html.match(/<input[^>]*id="skip-build-verification"[^>]*>/)[0];
assert.ok(checkbox.includes('name="skipBuildVerification"'));
assert.ok(!checkbox.includes('checked'), 'Build remains required by default');
assert.ok(source.includes("row(section('Build verification', 'agents')"), 'Choice is included in run confirmation');

const context = {
  connection: { sourceControl: 'gitlab', repositoryUrl: 'https://example.test/repo.git' },
  values: new Map([['branch', 'main'], ['task', 'Change the mapping'], ['maxAiCalls', '30'], ['maxBudgetUsd', '0.1']]),
  agents: {}, referenceCollectionId: null, cognia: null, changeRequestSource: 'manual',
};
vm.createContext(context);
const start = source.indexOf('  const request = {\n    sourceControl: connection.sourceControl');
const end = source.indexOf('\n\n  try {', start);
const requestCode = source.slice(start, end).replace('const request =', 'globalThis.request =');
vm.runInContext(requestCode, context);
assert.equal(context.request.skipBuildVerification, false);
context.values.set('skipBuildVerification', 'on');
vm.runInContext(requestCode, context);
assert.equal(context.request.skipBuildVerification, true);

function node() {
  return { children: [], hidden: true, textContent: '',
    set innerHTML(_) { throw new Error('Warning must remain plain text'); },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
  };
}
const notice = node();
let location;
Object.assign(context, {
  document: { createElement: node }, buildWarningNotice: notice,
  finalUsageSummary: { before() { location = 'review'; } },
  runErrorBox: { after() { location = 'activity'; } },
});
vm.runInContext(source.slice(source.indexOf('function renderBuildWarning('), source.indexOf('function renderJiraProblemNotice(')), context);
const events = [{ type: 'build_warning', status: 'skipped', text: 'Build verification skipped. <img src=x>' }];
context.renderBuildWarning(events, 'running');
assert.equal(notice.hidden, false);
assert.equal(location, 'activity');
assert.equal(notice.children[0].textContent, 'Build verification skipped');
assert.equal(notice.children[1].textContent, events[0].text);
context.renderBuildWarning(JSON.parse(JSON.stringify(events)), 'awaiting_approval');
assert.equal(location, 'review', 'Archived and approval snapshots retain the notification');
assert.equal(notice.children.length, 2, 'Polling must not duplicate the notification');
context.renderBuildWarning([], 'running');
assert.equal(notice.hidden, true);
assert.equal(notice.children.length, 0);
console.log('Build option, request payload, and persistent notification checks passed.');
