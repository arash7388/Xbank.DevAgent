const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Maf.Handoff.Api/wwwroot/app.js'), 'utf8');
const field = value => ({ value, disabled: false });
const semanticFields = { hidden: false };
const context = {
  referenceSearchModeInput: { value: 'exact', querySelector() { return this; } },
  qdrantUrlInput: field('invalid stale URL'),
  qdrantApiKeyInput: field(''),
  embeddingProviderInput: field('openai'),
  embeddingApiKeyInput: field(''),
  providerKeys: { openai: field('') },
  selectedReferenceFiles: () => [{ name: 'requirements.md' }],
  document: { querySelector: () => semanticFields },
  URL,
  AbortSignal: { timeout(ms) { assert.equal(ms, 300_000); return { timeoutMs: ms }; } },
  renderReferenceStatus: () => {},
  FormData: class {
    constructor() { this.fields = {}; }
    append(key, value) { this.fields[key] = value; }
  },
};
vm.createContext(context);
const syncStart = source.indexOf('function syncReferenceSearchMode(');
const syncEnd = source.indexOf('\nsyncReferenceSearchMode();', syncStart);
vm.runInContext(source.slice(syncStart, syncEnd), context);
const uploadStart = source.indexOf('async function uploadReferenceDocuments(');
const uploadEnd = source.indexOf("\nform.addEventListener('submit'", uploadStart);
vm.runInContext(source.slice(uploadStart, uploadEnd), context);

context.syncReferenceSearchMode();
assert.equal(semanticFields.hidden, true);
assert.equal(context.qdrantUrlInput.disabled, true);
assert.equal(context.embeddingProviderInput.disabled, true);
assert.equal(context.validateReferenceDocuments().searchMode, 'exact');

context.referenceSearchModeInput.value = 'semantic';
context.syncReferenceSearchMode();
assert.equal(semanticFields.hidden, false);
assert.equal(context.qdrantUrlInput.disabled, false);
context.qdrantUrlInput.value = '';
assert.throws(() => context.validateReferenceDocuments(), /Qdrant endpoint is required/);
context.qdrantUrlInput.value = 'https://test.cloud.qdrant.io';
assert.throws(() => context.validateReferenceDocuments(), /database API key is required/);
context.qdrantApiKeyInput.value = 'test-key';
assert.throws(() => context.validateReferenceDocuments(), /OpenAI embedding API key is required/);
context.embeddingProviderInput.value = 'ollama';
assert.equal(context.validateReferenceDocuments().searchMode, 'semantic');

context.referenceSearchModeInput.value = 'exact';
context.fetch = async (url, options) => {
  assert.equal(url, '/api/reference-collections');
  assert.equal(options.signal.timeoutMs, 300_000);
  assert.equal(options.body.fields.searchMode, 'exact');
  assert.equal(options.body.fields.qdrantUrl, '');
  assert.equal(options.body.fields.embeddingApiKey, '');
  assert.equal(options.body.fields.files.name, 'requirements.md');
  return { ok: true, json: async () => ({ id: 'collection-id', fileCount: 1, chunkCount: 1 }) };
};
context.uploadReferenceDocuments().then(id => {
  assert.equal(id, 'collection-id');
  console.log('Reference search UI checks passed.');
}).catch(error => {
  console.error(error);
  process.exitCode = 1;
});
