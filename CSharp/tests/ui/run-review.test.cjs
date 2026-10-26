const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');

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

for (const checked of [null, false, true]) {
  const summary = node();
  let editedStep;
  const nodes = {
    '#run-review-summary': summary,
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
    ],
  };
  const context = {
    document: {
      createElement: node,
      querySelector: selector => nodes[selector] || null,
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
  const buildCard = summary.children.find(card => all(card).some(item => item.tag === 'h3' && item.textContent === 'Build verification'));
  assert.ok(buildCard, 'Review must disclose the effective build setting');
  assert.ok(all(buildCard).some(item => item.textContent === (checked ? 'Build after changes — report result' : 'Skipped — compilation will not be verified')));
  const edit = all(buildCard).find(item => item.tag === 'button');
  edit.handlers.click();
  assert.equal(editedStep, 'agents');
}

// Exercise the setup placement with both current and older page markup.
for (const present of [false, true]) {
  const build = node(), autonomy = node(), retry = node(), limits = node(), models = node(), team = node(), agents = node();
  const context = {
    agentsSettings: agents, teamMapSection: team, agentSettings: models,
    document: { querySelector: selector => selector === '#build-verification-settings' ? (present ? build : null)
      : selector === '#autonomy-settings' ? (present ? autonomy : null)
      : selector === '#workflow-retry-settings' ? (present ? retry : null) : limits },
  };
  vm.createContext(context);
  const start = source.indexOf('  agentsSettings.append(teamMapSection);');
  const end = source.indexOf('  runSettings.append(', start);
  vm.runInContext(source.slice(start, end), context);
  assert.deepEqual(agents.children, present ? [team, autonomy, build, retry, limits, models] : [team, limits, models]);
}
console.log('Run review handles missing build controls and places current controls in the Agents step.');
