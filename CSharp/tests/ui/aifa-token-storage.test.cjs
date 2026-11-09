const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname,
  '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/aifa-token-storage.js'), 'utf8');
const storageKey = 'maf-aifa-token-v1';
const saved = new Map();
const storage = {
  getItem: key => saved.get(key) ?? null,
  setItem: (key, value) => saved.set(key, value),
  removeItem: key => saved.delete(key),
};
function load(localStorage = storage) {
  function node() {
    return {
      value: '', checked: false, textContent: 'Storage help', handlers: {}, events: [],
      addEventListener(type, handler) { this.handlers[type] = handler; },
      dispatchEvent(event) { this.events.push(event.type); this.handlers[event.type]?.(event); },
    };
  }
  const input = node(), remember = node(), status = node();
  const nodes = { '#aifa-key': input, '#remember-aifa-token': remember, '#aifa-token-storage-status': status };
  vm.runInNewContext(source, {
    document: { querySelector: selector => nodes[selector] }, localStorage,
    Event: class { constructor(type) { this.type = type; } },
  });
  return { input, remember, status };
}

let page = load();
assert.equal(page.remember.checked, false);
page.input.value = ' token ';
page.input.dispatchEvent({ type: 'input' });
assert.equal(saved.size, 0, 'Typing must not persist a token without consent');
page.remember.checked = true;
page.remember.dispatchEvent({ type: 'change' });
assert.equal(saved.get(storageKey), 'token');

page = load();
assert.equal(page.input.value, 'token', 'Restore the token on a new visit');
assert.equal(page.remember.checked, true);
assert.deepEqual(page.input.events, ['input'], 'Restoration follows the existing provider selection behavior');
page.input.value = 'replacement';
page.input.dispatchEvent({ type: 'input' });
assert.equal(saved.get(storageKey), 'replacement');
page.input.value = '';
page.input.dispatchEvent({ type: 'input' });
assert.equal(saved.size, 0, 'Clearing the input removes the old token');
page.input.value = 'replacement';
page.input.dispatchEvent({ type: 'input' });
page.remember.checked = false;
page.remember.dispatchEvent({ type: 'change' });
assert.equal(saved.size, 0);
assert.equal(page.input.value, 'replacement', 'Forgetting must keep the current run usable');
assert.equal(load().input.value, '');

page = load({ getItem() { throw new Error('disabled'); } });
assert.match(page.status.textContent, /storage is unavailable/i);
page.input.value = 'current-run';
page.remember.checked = true;
page.remember.dispatchEvent({ type: 'change' });
assert.match(page.status.textContent, /could not be updated/);
assert.equal(page.input.value, 'current-run');
console.log('AIFA token opt-in, restoration, edits, removal, and unavailable storage checks passed.');
