const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');

function node(tag = '') {
  return {
    tag, children: [], textContent: '', attributes: {}, handlers: {},
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    setAttribute(name, value) { this.attributes[name] = value; },
    addEventListener(name, handler) { this.handlers[name] = handler; },
    querySelectorAll() { return []; },
  };
}
function all(root) { return [root, ...root.children.flatMap(all)]; }

for (const [checked, experience] of [null, false, true].flatMap(checked =>
  ['simple', 'advanced'].map(experience => [checked, experience]))) {
  const summary = node();
  let editedStep;
  let scrolledCategory;
  let focusedCategory;
  const nodes = {
    '#run-review-summary': summary,
    'input[name="runExperience"]:checked': { value: experience },
    '#task': { value: 'Change the requested value' },
    '#agent-turn-limits': node(),
    '#build-project': checked === null ? null : { checked },
  };
  const repository = {
    dataset: { configCategory: 'repository' },
    querySelector: selector => selector === 'legend' ? { textContent: 'Repository' } : {},
    querySelectorAll: () => [
      { type: 'radio', name: 'sourceControl', checked: false, value: 'github', labels: [{ textContent: 'GitHub' }] },
      { type: 'radio', name: 'sourceControl', checked: true, value: 'gitlab', labels: [{ textContent: 'GitLab' }] },
      { type: 'text', value: 'Advanced setting', labels: [{ textContent: 'Hidden control' }], closest: () => ({}) },
    ],
  };
  const context = {
    document: {
      createElement: node,
      querySelector: selector => {
        const destination = selector.match(/^\[data-setup-step="([^"]+)"\] \[data-config-category="([^"]+)"\]$/);
        if (destination) {
          assert.equal(editedStep, destination[1], 'Reveal the destination step before scrolling');
          return {
            setAttribute() {},
            focus: options => { assert.equal(options.preventScroll, true); focusedCategory = destination[2]; },
            scrollIntoView: options => { assert.equal(options.block, 'start'); scrolledCategory = destination[2]; },
          };
        }
        return nodes[selector] || null;
      },
      querySelectorAll: selector => selector === '#connection-settings > fieldset' ? [repository] : [],
    },
    agentModels: node(), changeRequestSource: 'manual', selectedReferenceFiles: () => [],
    setSetupStep: step => { editedStep = step; },
  };
  vm.createContext(context);
  const start = source.indexOf('function renderRunReview()');
  const end = source.indexOf('// Native constraint validation', start);
  vm.runInContext(source.slice(start, end), context);
  assert.doesNotThrow(() => context.renderRunReview(), 'Review must work with current or older page markup');
  const repositoryCard = summary.children.find(card => card.attributes['data-review-category'] === 'repository');
  assert.ok(all(repositoryCard).some(item => item.textContent === 'GitLab'));
  assert.ok(!all(repositoryCard).some(item => item.textContent === 'GitHub'));
  assert.equal(all(repositoryCard).some(item => item.textContent === 'Advanced setting'), experience === 'advanced',
    'Step 5 must follow the visibility of controls in earlier steps');
  const buildCard = summary.children.find(card => all(card).some(item => item.tag === 'h3' && item.textContent === 'Build verification'));
  assert.ok(buildCard, 'Review must disclose the effective build setting');
  assert.equal(buildCard.attributes['data-advanced-only'], '', 'Build summary is hidden in Simple Run');
  assert.ok(all(buildCard).some(item => item.textContent === (checked ? 'Build after changes — report result' : 'Skipped — compilation will not be verified')));
  const edit = all(buildCard).find(item => item.tag === 'button');
  edit.handlers.click();
  assert.equal(editedStep, 'agents');
  assert.equal(scrolledCategory, 'verification');
  for (const [title, step, category] of [
    ['Requested changes', 'request', 'instruction'],
    ['Repository', 'connection', 'repository'],
    ['Agentic turn limits', 'agents', 'limits'],
    ['Agents and model settings', 'agents', 'models'],
    ['Attachments', 'request', 'references'],
  ]) {
    all(summary).find(item => item.attributes['aria-label'] === `Edit ${title}`).handlers.click();
    assert.equal(editedStep, step);
    assert.equal(scrolledCategory, category);
    assert.equal(focusedCategory, category);
  }
}

// Exercise the setup placement with both current and older page markup.
for (const present of [false, true]) {
  const build = node(), autonomy = node(), retry = node(), codingRules = node(), limits = node(), models = node(), team = node(), agents = node();
  const context = {
    agentsSettings: agents, teamMapSection: team, agentSettings: models,
    document: { querySelector: selector => selector === '#build-verification-settings' ? (present ? build : null)
      : selector === '#autonomy-settings' ? (present ? autonomy : null)
      : selector === '#workflow-retry-settings' ? (present ? retry : null)
      : selector === '#coding-rules-settings' ? (present ? codingRules : null) : limits },
  };
  vm.createContext(context);
  const start = source.indexOf('  agentsSettings.append(teamMapSection);');
  const end = source.indexOf('  runSettings.append(', start);
  vm.runInContext(source.slice(start, end), context);
  assert.deepEqual(agents.children, present ? [team, autonomy, build, retry, codingRules, limits, models] : [team, limits, models]);
}
console.log('Run review handles missing build controls and places current controls in the Agents step.');
