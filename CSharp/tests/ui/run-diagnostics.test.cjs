const assert = require('assert').strict;
const diagnostics = require('../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/run-diagnostics.js');

const text = '## Blockers\nRequest and issue differ.\n\n**Questions**\n1. Which scope should we follow?\nJIRA_INPUT_REQUIRED';
const events = [
  { type: 'message', agent: 'planner', text: 'Earlier output' },
  { type: 'tool', agent: 'jira', text: 'Read issue' },
  ...Array.from(text, character => ({ type: 'message', agent: 'jira', text: character })),
  { type: 'trace', agent: 'jira', text: 'Response complete' },
  { type: 'run_finished', status: 'input_required' },
];
const fallback = diagnostics.fromSnapshot({ error: 'Additional input required', events });
const loopMessage = 'WORKFLOW_LOOP_DETECTED Recent path: developer -> reviewer -> developer.';
const loop = diagnostics.fromSnapshot({ error: 'Stopped without progress', events: [
  { type: 'message', agent: 'developer', text: loopMessage },
  { type: 'run_finished', status: 'loop_detected' },
] });
assert.equal(loop.title, 'Run stopped without progress');
assert.ok(!loop.message.includes('WORKFLOW_LOOP_DETECTED'));
assert.equal(loop.technicalDetails, loopMessage);
assert.equal(loop.agent, undefined);
assert.equal(fallback.title, 'Clarification needed');
assert.equal(fallback.agent, 'jira');
assert.equal(fallback.message, text);
assert.equal(fallback.status, 'input_required');

const details = { title: 'Release blocked', message: 'The API contract was violated.' };
assert.equal(diagnostics.fromSnapshot({ error: 'Blocked', failureDetails: details }), details);
assert.equal(diagnostics.fromSnapshot({ events }), null);
assert.equal(diagnostics.fromSnapshot({ error: 'Git clone failed', events: [] }).message, 'Git clone failed');

// Minimal document interface for testing DOM construction without a browser or external packages.
// Agent-controlled text must stay text; no renderer is allowed to assign innerHTML.
class TestNode {
  constructor(doc, tag, value = '') {
    this.ownerDocument = doc; this.localName = tag; this.children = []; this.value = value; this.listeners = {};
    this.classes = new Set();
    this.classList = {
      add: name => this.classes.add(name),
      toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name),
    };
  }
  set innerHTML(_) { throw new Error('Untrusted HTML assignment'); }
  set textContent(value) { this.value = value; this.children = []; }
  get textContent() { return this.value + this.children.map(child => child.textContent).join(''); }
  get lastElementChild() { return [...this.children].reverse().find(child => child.localName); }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.value = ''; this.children = children; }
  addEventListener(type, listener) { this.listeners[type] = listener; }
  all(tag) { return this.children.flatMap(child => [...(child.localName === tag ? [child] : []), ...child.all(tag)]); }
}
const doc = {
  createElement: tag => new TestNode(doc, tag),
  createTextNode: value => new TestNode(doc, null, value),
};

async function testRendering(snapshot) {
  const panel = doc.createElement('section');
  let edited = false;
  let copied = null;
  const assessment = diagnostics.fromSnapshot(snapshot);
  diagnostics.render(panel, assessment, {
    edit: () => { edited = true; },
    copy: async message => { copied = message; },
  });
  assert.equal(panel.hidden, false);
  assert.equal(panel.all('pre').slice(-1)[0].textContent, assessment.message);
  assert.equal(panel.all('h3')[0].textContent, 'Clarification needed');
  assert.ok(panel.classes.has('clarification'));
  assert.ok(panel.all('ol').length > 0, 'Questions should be rendered as an ordered list');
  panel.all('button')[0].listeners.click();
  assert.equal(edited, true);
  await panel.all('button')[1].listeners.click();
  assert.equal(copied, assessment.message, 'Copy must preserve the entire original response');
  return panel;
}

(async () => {
  const loopPanel = doc.createElement('section');
  let copiedTechnical;
  diagnostics.render(loopPanel, loop, { copy: async value => { copiedTechnical = value; } });
  assert.ok(!loopPanel.all('div').find(node => node.className === 'run-diagnostic-body').textContent.includes('WORKFLOW_'));
  assert.equal(loopPanel.all('details')[0].open, undefined, 'Technical details should start collapsed');
  assert.equal(loopPanel.all('summary')[0].textContent, 'Technical details');
  await loopPanel.all('button')[0].listeners.click();
  assert.equal(copiedTechnical, loopMessage);
  await testRendering({ error: 'Additional input required', events });
  const output = doc.createElement('div');
  diagnostics.renderMessage(output, '## Blockers\n<img src=x onerror=alert(1)>\n\n**Questions**\n1. Check `API` and **scope**.\n2. [Unsafe](javascript:alert%281%29) [Safe](https://example.com/issue)\nJIRA_INPUT_REQUIRED');
  assert.equal(output.all('img').length, 0);
  assert.equal(output.all('script').length, 0);
  assert.ok(output.textContent.includes('<img src=x onerror=alert(1)>'));
  assert.equal(output.all('a').length, 1);
  assert.equal(output.all('a')[0].href, 'https://example.com/issue');
  assert.equal(output.all('a')[0].rel, 'noopener noreferrer');
  assert.equal(output.all('code')[0].textContent, 'API');
  assert.ok(!output.textContent.includes('JIRA_INPUT_REQUIRED'));
  if (process.argv[2]) {
    const fs = require('fs');
    const snapshot = JSON.parse(fs.readFileSync(process.argv[2], 'utf8').replace(/^\uFEFF/, ''));
    const actual = await testRendering(snapshot);
    assert.equal(actual.all('ol').slice(-1)[0].children.length, 4, 'All four questions from the reported run must be visible');
    console.log('Passed: full assessment and all four questions from the reported Jira run.');
  }
  console.log('Passed: archive reconstruction, server details, formatted sections, copy/edit controls, and injection-safe text/links.');
})().catch(error => { console.error(error); process.exitCode = 1; });
