const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
}
const elements = new Map();
const get = selector => {
  if (!elements.has(selector)) elements.set(selector, new Element('div'));
  return elements.get(selector);
};
const all = root => [root, ...root.children.flatMap(all)];
const context = {
  document: { querySelector: get, createElement: tag => new Element(tag) },
  codeGraphIcon: () => new Element('svg'),
};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('function renderCodeGraphFreshness('), source.indexOf('function renderKindList(')), context);
const message = "Local folder matches. Runs verify their own checkout separately; update the indexed folder to the run's branch/revision and reindex.";
context.renderCodeGraphFreshness([{ sourcePath: 'C:/repo/App.sln', state: 'fresh', message, currentHash: 'abc', indexedHash: 'abc' }]);
const nodes = () => all(get('#codegraph-freshness'));
assert.equal(get('#codegraph-freshness-summary').textContent, 'Indexed local folder');
assert.ok(nodes().some(node => node.textContent === 'Local source matches index'));
assert.ok(nodes().some(node => node.textContent === message), 'Show the backend explanation instead of replacing it with a generic success message');
assert.ok(nodes().some(node => (node.textContent || '').includes('Database index SHA-256: abc')));
context.renderCodeGraphFreshness([{ sourcePath: 'C:/repo/App.sln', state: 'fresh' }]);
assert.ok(nodes().some(node => (node.textContent || '').includes('Each run verifies its own checkout separately')));
const unsafe = '<img src=x onerror=alert(1)>';
context.renderCodeGraphFreshness([{ sourcePath: unsafe, state: 'stale', message: unsafe }]);
assert.ok(nodes().some(node => node.textContent === unsafe));
assert.ok(!nodes().some(node => node.tag === 'img'));
assert.ok(nodes().some(node => node.textContent === 'Local source differs from index'));
console.log('CodeGraph freshness keeps local-check scope, server explanations, hashes, and safe text.');
