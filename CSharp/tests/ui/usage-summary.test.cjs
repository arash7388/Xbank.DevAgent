const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const start = source.indexOf('function renderUsageCard(');
const end = source.indexOf('\nfunction updateTeamMap(', start);
function node() {
  return {
    children: [], textContent: '', hidden: true,
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
  };
}
const context = {
  document: { createElement: node },
  formatNumber: value => String(value || 0),
  formatUsd: value => `$${value.toFixed(3)}`,
  isTerminal: status => status === 'failed',
};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);
function text(element) {
  return [element.textContent, ...element.children.map(text)].join(' ');
}
const jira = { agent: 'jira', llmRequestCount: 0, checkpointHits: 13 };
const summary = {
  hasRequests: true, llmRequestCount: 29, checkpointHits: 13,
  totalTokens: 1690162, inputTokens: 1651828, cachedInputTokens: 1508736,
  outputTokens: 38334, reasoningTokens: 24002, estimatedCostUsd: 0.048990408,
  byAgent: [jira, { agent: 'developer', llmRequestCount: 16, checkpointHits: 0 }],
};
let card = node();
context.renderUsageCard(card, summary, 'awaiting_approval', true);
assert.equal(card.hidden, false);
assert.match(text(card), /29 AI requests \| 13 local checkpoint replays/);
assert.match(text(card), /jira: 0 AI requests, 13 checkpoint replays, \$0 additional model cost/);
assert.match(text(card), /1508736 provider-cached/);
assert.match(text(card), /Estimated cost: \$0.049/);

card = node();
context.renderUsageCard(card, { ...summary, hasRequests: false, llmRequestCount: 0,
  totalTokens: 0, inputTokens: 0, cachedInputTokens: 0, outputTokens: 0,
  reasoningTokens: 0, estimatedCostUsd: 0, byAgent: [jira] }, 'awaiting_approval', true);
assert.equal(card.hidden, false);
assert.match(text(card), /0 AI requests \| 13 local checkpoint replays/);
assert.doesNotMatch(text(card), /unavailable/);
console.log('Usage summary checks passed.');
