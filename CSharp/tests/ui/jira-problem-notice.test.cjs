const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function renderJiraProblemNotice(');
const end = source.indexOf('\nfunction renderAgentSummaries(', start);
function node() {
  return {
    children: [], textContent: '', hidden: true, dataset: {},
    set innerHTML(_) { throw new Error('Jira findings must remain plain text'); },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
  };
}
const panel = node();
let location;
const context = {
  document: { createElement: node }, jiraProblemNotice: panel,
  finalUsageSummary: { before() { location = 'diff'; } },
  runErrorBox: { after() { location = 'activity'; } },
};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);
const problem = { type: 'jira_problem', status: 'posted', source: 'WEPOD-42',
  text: 'درخواست مبهم است؛ لطفاً مقدار صحیح را مشخص کنید. <img src=x onerror=alert(1)>',
  target: 'https://jira.example.test/browse/WEPOD-42?focusedCommentId=321' };
context.renderJiraProblemNotice([problem], 'running');
assert.equal(panel.hidden, false);
assert.equal(location, 'activity');
assert.equal(panel.dataset.status, 'posted');
assert.match(panel.children[0].textContent, /گزارش محصول و ملاحظات فنی در Jira ثبت شد/);
assert.equal(panel.children[1].textContent, problem.text);
assert.equal(panel.children[2].href, problem.target);
assert.equal(panel.children[2].rel, 'noopener noreferrer');
context.renderJiraProblemNotice(JSON.parse(JSON.stringify([problem])), 'awaiting_approval');
assert.equal(location, 'diff');
context.renderJiraProblemNotice([problem, { ...problem, status: 'failed' }], 'failed');
assert.equal(location, 'activity');
assert.match(panel.children[0].textContent, /ثبت گزارش محصول و ملاحظات فنی تأیید نشد/);
assert.equal(panel.dataset.status, 'failed');
context.renderJiraProblemNotice([{ ...problem, target: 'javascript:alert(1)' }], 'running');
assert.equal(panel.children.length, 2);
context.renderJiraProblemNotice([], 'running');
assert.equal(panel.hidden, true);
assert.equal(panel.children.length, 0);
context.renderJiraProblemNotice([{ ...problem, type: 'jira_scope_change' }], 'awaiting_approval');
assert.match(panel.children[0].textContent, /تغییر دامنهٔ محصول/);
assert.equal(panel.dataset.status, 'posted');
console.log('Jira problem notice checks passed.');
