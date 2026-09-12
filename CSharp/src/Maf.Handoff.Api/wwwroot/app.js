const form = document.querySelector('#run-form');
const agentModels = document.querySelector('#agent-models');
const pricingStatus = document.querySelector('#pricing-status');
const providerKeys = Object.fromEntries(
  [...document.querySelectorAll('[data-provider-key]')].map(input => [input.dataset.providerKey, input]));
const runButton = document.querySelector('#run-button');
const cancelButton = document.querySelector('#cancel-button');
const rejectButton = document.querySelector('#reject-button');
const approveButton = document.querySelector('#approve-button');
const generatePrButton = document.querySelector('#generate-pr-button');
const pullRequestLink = document.querySelector('#pull-request-link');
const errorBox = document.querySelector('#form-error');
const runErrorBox = document.querySelector('#run-error');
const statusBadge = document.querySelector('#status-badge');
const elapsedTimer = document.querySelector('#elapsed-timer');
const elapsedTimerValue = elapsedTimer.querySelector('time');
const sessionLabel = document.querySelector('#session-label');
const emptyActivity = document.querySelector('#empty-activity');
const activity = document.querySelector('#activity');
const usageSummary = document.querySelector('#usage-summary');
const finalUsageSummary = document.querySelector('#final-usage-summary');
const diff = document.querySelector('#diff code');
const diffSummary = document.querySelector('#diff-summary');
const dialog = document.querySelector('#approval-dialog');
const approvalForm = document.querySelector('#approval-form');
const workflowPanels = document.querySelectorAll('[data-workflow-panel]');
const workflowSteps = document.querySelectorAll('.step');
const teamMap = document.querySelector('#team-map');
const teamMapSection = document.querySelector('.team-map');
const setupHeading = document.querySelector('.setup-panel > .section-heading');
const activityHeading = document.querySelector('.activity-panel > .section-heading');
const referenceFilesInput = document.querySelector('#reference-files');
const referenceFolderInput = document.querySelector('#reference-folder');
const qdrantUrlInput = document.querySelector('#qdrant-url');
const qdrantApiKeyInput = document.querySelector('#qdrant-api-key');
const embeddingProviderInput = document.querySelector('#embedding-provider');
const embeddingApiKeyInput = document.querySelector('#embedding-api-key');
const embeddingApiKeyField = document.querySelector('#embedding-api-key-field');
const embeddingProviderGuide = document.querySelector('#embedding-provider-guide');
const referenceStatus = document.querySelector('#reference-status');
const connectionStepButton = document.querySelector('#connection-step-button');
const runStepButton = document.querySelector('#run-step-button');

let sessionId = null;
let pollTimer = null;
let elapsedTimerInterval = null;
let runStartedAt = null;
let elapsedStoppedAt = null;
let eventFingerprint = '';

function initializeSetupSteps() {
  const repositorySettings = document.querySelector('#repository').closest('fieldset');
  const agentSettings = document.querySelector('#agent-models').closest('fieldset');
  const providerSettings = document.querySelector('#openai-key').closest('fieldset');
  const instructionSettings = document.querySelector('#task').closest('fieldset');
  const referenceSettings = referenceFilesInput.closest('fieldset');
  const formError = document.querySelector('#form-error');
  const formActions = form.querySelector('.form-actions');

  const connectionSettings = document.createElement('section');
  connectionSettings.id = 'connection-settings';
  connectionSettings.className = 'setup-step-content';
  connectionSettings.setAttribute('role', 'tabpanel');
  connectionSettings.setAttribute('aria-labelledby', 'connection-step-button');

  const runSettings = document.createElement('section');
  runSettings.id = 'run-settings';
  runSettings.className = 'setup-step-content';
  runSettings.setAttribute('role', 'tabpanel');
  runSettings.setAttribute('aria-labelledby', 'run-step-button');

  form.prepend(connectionSettings, runSettings);
  connectionSettings.append(repositorySettings, providerSettings);

  const qdrantSettings = document.createElement('fieldset');
  qdrantSettings.className = 'qdrant-settings';
  const qdrantLegend = document.createElement('legend');
  qdrantLegend.textContent = 'Qdrant and embeddings';
  const qdrantGuide = document.createElement('p');
  qdrantGuide.className = 'field-guide';
  qdrantGuide.textContent = 'Used only when reference documents are attached in Run setup.';
  qdrantSettings.append(qdrantLegend, qdrantGuide);
  [
    document.querySelector('label[for="qdrant-url"]'), qdrantUrlInput,
    document.querySelector('label[for="qdrant-api-key"]'), qdrantApiKeyInput,
    document.querySelector('label[for="embedding-provider"]'), embeddingProviderInput,
    embeddingApiKeyField, embeddingProviderGuide,
  ].forEach(element => qdrantSettings.append(element));
  connectionSettings.append(qdrantSettings);

  runSettings.append(teamMapSection, agentSettings, instructionSettings, referenceSettings, formError, formActions);
  setSetupStep('connection');
}

function setSetupStep(step) {
  const isConnection = step === 'connection';
  const connectionSettings = document.querySelector('#connection-settings');
  const runSettings = document.querySelector('#run-settings');
  connectionSettings.hidden = !isConnection;
  runSettings.hidden = isConnection;
  connectionStepButton.classList.toggle('active', isConnection);
  runStepButton.classList.toggle('active', !isConnection);
  connectionStepButton.setAttribute('aria-selected', String(isConnection));
  runStepButton.setAttribute('aria-selected', String(!isConnection));
}

initializeSetupSteps();
connectionStepButton.addEventListener('click', () => setSetupStep('connection'));
runStepButton.addEventListener('click', () => setSetupStep('run'));

const eventDateFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});

const providerCatalog = {
  openai: {
    name: 'OpenAI', url: 'https://api.openai.com/v1',
    toolCalling: true, vision: true, thinking: true,
    maxInputTokens: 1050000, maxOutputTokens: 128000,
    reasoningEffort: 'high', supportsReasoningEffort: ['low', 'high', 'max'],
    models: [
      ['gpt-5.4-mini', 'Gpt5.4-Mini'],
      ['gpt-5.6-sol', 'Gpt-5.6-Sol'],
      ['gpt-5.6-terra', 'Gpt-5.6.Terra'],
    ],
  },
  deepseek: {
    name: 'DeepSeek', url: 'https://api.deepseek.com/chat/completions',
    toolCalling: true, vision: true, maxInputTokens: 128000, maxOutputTokens: 160000,
    thinking: true, reasoningEffort: 'high', supportsReasoningEffort: ['low', 'medium', 'high', 'max'],
    // Conservative fallback values; replaced from DeepSeek's pricing page on load.
    defaultPricing: {
      'deepseek-v4-flash': { cachedInput: 0.014, input: 0.44, output: 1.32 },
      'deepseek-v4-pro': { cachedInput: 0.044, input: 1.32, output: 3.96 },
    },
    models: [
      ['deepseek-v4-flash', 'deepseek-v4-flash'],
      ['deepseek-v4-pro', 'deepseek-v4-pro'],
    ],
  },
  claude: {
    name: 'Claude', url: 'https://api.anthropic.com',
    toolCalling: true, vision: true, maxInputTokens: 200000, maxOutputTokens: 64000,
    thinking: false, reasoningEffort: null, supportsReasoningEffort: [],
    models: [
      ['claude-sonnet-5', 'Sonnet-5'],
      ['claude-opus-5', 'Opus-5'],
    ],
  },
};

const agentCatalog = [
  { id: 'intake', name: 'Intake', description: 'Intake and routing', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low', required: true },
  { id: 'jira', name: 'Jira', description: 'Read-only Jira issue and project context', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low', enabled: false },
  { id: 'planner', name: 'Planner', description: 'Architecture and acceptance criteria', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low' },
  { id: 'developer', name: 'Developer', description: 'Implementation and remediation', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low', required: true },
  { id: 'reviewer', name: 'Reviewer', description: 'Correctness and quality gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low' },
  { id: 'security', name: 'Security', description: 'Threat modeling and security gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low', enabled: false },
  { id: 'release_manager', name: 'Release manager', description: 'Final evidence gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'low', required: true },
];

function createAgentModelCard(agent) {
  const card = document.createElement('section');
  card.className = 'agent-model-card';
  card.dataset.agent = agent.id;

  const header = document.createElement('div');
  header.className = 'agent-card-header';
  const title = document.createElement('h3');
  title.textContent = agent.name;
  const enabledLabel = document.createElement('label');
  enabledLabel.className = 'agent-toggle';
  const enabledInput = document.createElement('input');
  enabledInput.type = 'checkbox';
  enabledInput.dataset.field = 'enabled';
  enabledInput.checked = agent.enabled !== false;
  enabledInput.disabled = Boolean(agent.required);
  enabledInput.setAttribute('aria-label', `${agent.name} enabled`);
  const enabledText = document.createElement('span');
  enabledText.textContent = agent.required ? 'Required' : 'Enabled';
  enabledLabel.append(enabledInput, enabledText);
  header.append(title, enabledLabel);
  const description = document.createElement('p');
  description.textContent = agent.description;
  const tools = document.createElement('details');
  tools.className = 'agent-tools';
  tools.dataset.field = 'tools';
  const toolsSummary = document.createElement('summary');
  toolsSummary.textContent = 'Available tools · loading…';
  const toolsList = document.createElement('ul');
  tools.append(toolsSummary, toolsList);
  const controls = document.createElement('div');
  controls.className = 'agent-model-controls';

  const providerGroup = document.createElement('div');
  const providerLabel = document.createElement('label');
  providerLabel.textContent = 'Provider';
  const providerSelect = document.createElement('select');
  providerSelect.dataset.field = 'provider';
  providerSelect.dataset.configField = '';
  providerSelect.setAttribute('aria-label', `${agent.name} provider`);
  Object.entries(providerCatalog).forEach(([value, definition]) =>
    providerSelect.add(new Option(definition.name, value)));
  providerSelect.value = agent.provider;
  providerGroup.append(providerLabel, providerSelect);

  const effortGroup = document.createElement('div');
  const effortLabel = document.createElement('label');
  effortLabel.textContent = 'Reasoning effort';
  const effortSelect = document.createElement('select');
  effortSelect.dataset.field = 'effort';
  effortSelect.dataset.configField = '';
  effortSelect.setAttribute('aria-label', `${agent.name} reasoning effort`);
  effortGroup.append(effortLabel, effortSelect);

  const modelLabel = document.createElement('label');
  modelLabel.textContent = 'Model';
  const modelSelect = document.createElement('select');
  modelSelect.dataset.field = 'model';
  modelSelect.dataset.configField = '';
  modelSelect.required = true;
  modelSelect.setAttribute('aria-label', `${agent.name} model`);

  const promptLabel = document.createElement('label');
  promptLabel.textContent = 'Additional agent guidance (optional)';
  const promptInput = document.createElement('textarea');
  promptInput.dataset.field = 'prompt';
  promptInput.dataset.configField = '';
  promptInput.className = 'agent-prompt';
  promptInput.maxLength = 50000;
  promptInput.rows = 4;
  promptInput.placeholder = 'Added below the protected server-owned role and CodeGraph policies; cannot override them.';
  promptInput.setAttribute('aria-label', `${agent.name} additional guidance`);

  controls.append(providerGroup, effortGroup);
  card.append(header, description, tools, controls, modelLabel, modelSelect, promptLabel, promptInput);
  providerSelect.addEventListener('change', () => syncAgentCard(card));
  modelSelect.addEventListener('change', () => syncUsagePricingCard(agent.id, true));
  effortSelect.addEventListener('change', () => syncUsagePricingCard(agent.id, true));
  enabledInput.addEventListener('change', () => syncAgentCardEnabled(card));
  agentModels.append(card);
  syncAgentCard(card, agent.model, agent.effort);
}

function createUsagePricingCard(agent) {
  const modelCard = agentModels.querySelector(`[data-agent="${agent.id}"]`);
  if (!modelCard) return;
  const card = document.createElement('section');
  card.className = 'usage-price-card';
  card.dataset.usageAgent = agent.id;
  const title = document.createElement('h3');
  title.textContent = 'Token pricing';
  const model = document.createElement('p');
  model.dataset.field = 'pricing-model';
  const modeNote = document.createElement('p');
  modeNote.dataset.field = 'pricing-mode-note';
  const inputLabel = document.createElement('label');
  inputLabel.textContent = 'Uncached input USD / 1M';
  const input = document.createElement('input');
  input.type = 'number';
  input.min = '0';
  input.step = '0.0001';
  input.inputMode = 'decimal';
  input.placeholder = 'Optional';
  input.dataset.field = 'input-cost';
  input.setAttribute('aria-label', `${agent.name} input token price in USD per million tokens`);
  const cachedInputLabel = document.createElement('label');
  cachedInputLabel.textContent = 'Cached input USD / 1M';
  const cachedInput = document.createElement('input');
  cachedInput.type = 'number';
  cachedInput.min = '0';
  cachedInput.step = '0.0001';
  cachedInput.inputMode = 'decimal';
  cachedInput.placeholder = 'Optional';
  cachedInput.dataset.field = 'cached-input-cost';
  cachedInput.setAttribute('aria-label', `${agent.name} cached input token price in USD per million tokens`);
  const outputLabel = document.createElement('label');
  outputLabel.textContent = 'Output USD / 1M tokens';
  const output = document.createElement('input');
  output.type = 'number';
  output.min = '0';
  output.step = '0.0001';
  output.inputMode = 'decimal';
  output.placeholder = 'Optional';
  output.dataset.field = 'output-cost';
  output.setAttribute('aria-label', `${agent.name} output token price in USD per million tokens`);
  card.append(title, model, modeNote, inputLabel, input, cachedInputLabel, cachedInput, outputLabel, output);
  modelCard.append(card);
  syncUsagePricingCard(agent.id);
}

function syncUsagePricingCard(agentId, forceProviderRates = false) {
  const modelCard = agentModels.querySelector(`[data-agent="${agentId}"]`);
  const priceCard = agentModels.querySelector(`[data-usage-agent="${agentId}"]`);
  if (!modelCard || !priceCard) return;
  const provider = providerCatalog[modelCard.querySelector('[data-field="provider"]').value];
  const model = modelCard.querySelector('[data-field="model"]').value;
  const effort = modelCard.querySelector('[data-field="effort"]');
  const thinkingMode = effort?.disabled ? 'none' : effort.value;
  const thinking = effort?.disabled ? '' : ` · ${thinkingMode} thinking`;
  priceCard.querySelector('[data-field="pricing-model"]').textContent = `${provider?.name || 'Provider'} · ${model || 'No model selected'}${thinking}`;
  const defaultPricing = provider?.pricingByThinking?.[model]?.[thinkingMode]
    ?? provider?.defaultPricing?.[model];
  priceCard.querySelector('[data-field="pricing-mode-note"]').textContent = provider?.name === 'DeepSeek'
    ? `DeepSeek publishes the same per-token rates for ${thinkingMode} thinking; reasoning tokens are charged at the output rate.`
    : 'Rates are resolved for the selected model and thinking mode.';
  syncDefaultTokenPrice(priceCard.querySelector('[data-field="input-cost"]'), defaultPricing?.input, forceProviderRates);
  syncDefaultTokenPrice(priceCard.querySelector('[data-field="cached-input-cost"]'), defaultPricing?.cachedInput, forceProviderRates);
  syncDefaultTokenPrice(priceCard.querySelector('[data-field="output-cost"]'), defaultPricing?.output, forceProviderRates);
  priceCard.hidden = !isAgentCardEnabled(modelCard);
}

function syncDefaultTokenPrice(input, nextDefault, forceProviderRate = false) {
  const previousDefault = input.dataset.defaultPrice;
  const canReplace = forceProviderRate || input.value === '' || input.value === previousDefault;
  input.dataset.defaultPrice = nextDefault == null ? '' : String(nextDefault);
  if (canReplace) input.value = input.dataset.defaultPrice;
}

function syncAgentCard(card, preferredModel = null, preferredEffort = null) {
  const providerSelect = card.querySelector('[data-field="provider"]');
  const modelSelect = card.querySelector('[data-field="model"]');
  const effortSelect = card.querySelector('[data-field="effort"]');
  const definition = providerCatalog[providerSelect.value];
  if (!definition) {
    modelSelect.replaceChildren(new Option('No models available', ''));
    modelSelect.disabled = true;
    effortSelect.replaceChildren(new Option('Not supported', ''));
    effortSelect.disabled = true;
    showError('The selected provider is not configured.');
    return;
  }

  modelSelect.disabled = false;
  modelSelect.replaceChildren(...definition.models.map(([value, label]) => new Option(label, value)));
  if (preferredModel && definition.models.some(([value]) => value === preferredModel)) modelSelect.value = preferredModel;
  effortSelect.replaceChildren();
  if (definition.thinking) {
    definition.supportsReasoningEffort.forEach(value => effortSelect.add(new Option(value, value)));
    effortSelect.value = preferredEffort && definition.supportsReasoningEffort.includes(preferredEffort)
      ? preferredEffort
      : definition.reasoningEffort;
    effortSelect.disabled = false;
  } else {
    effortSelect.add(new Option('Not supported', ''));
    effortSelect.disabled = true;
  }
  syncRequiredProviderKeys();
  syncAgentCardEnabled(card);
  syncUsagePricingCard(card.dataset.agent, true);
}

function isAgentCardEnabled(card) {
  return card.querySelector('[data-field="enabled"]').checked;
}

function syncAgentCardEnabled(card) {
  const enabled = isAgentCardEnabled(card);
  card.classList.toggle('is-disabled', !enabled);
  const toggleText = card.querySelector('.agent-toggle span');
  if (!card.querySelector('[data-field="enabled"]').disabled) toggleText.textContent = enabled ? 'Enabled' : 'Disabled';
  card.querySelectorAll('[data-config-field]').forEach(control => { control.disabled = !enabled; });
  syncRequiredProviderKeys();
}

const requestedProvider = new URLSearchParams(window.location.search).get('provider');
if (providerCatalog[requestedProvider]) {
  agentCatalog.forEach(agent => { agent.provider = requestedProvider; agent.model = null; agent.effort = null; });
}
agentCatalog.forEach(createAgentModelCard);
agentCatalog.forEach(createUsagePricingCard);
runButton.disabled = true;

async function loadDefaultPrompts() {
  const prompts = await api('/api/agents/default-prompts');
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    const prompt = prompts[card.dataset.agent];
    if (!prompt) throw new Error(`No default prompt is configured for ${card.dataset.agent}.`);
    card.querySelector('[data-field="prompt"]').dataset.protectedPromptLoaded = 'true';
  });
}

async function loadAgentTools() {
  const toolsByAgent = await api('/api/agents/tools');
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    const tools = toolsByAgent[card.dataset.agent];
    if (!Array.isArray(tools)) throw new Error(`No tool catalog is configured for ${card.dataset.agent}.`);
    const panel = card.querySelector('[data-field="tools"]');
    const summary = panel.querySelector('summary');
    const list = panel.querySelector('ul');
    const conditionalCount = tools.filter(tool => tool.availability !== 'always').length;
    const alwaysCount = tools.length - conditionalCount;
    summary.textContent = `Available tools · ${alwaysCount}${conditionalCount ? ` + ${conditionalCount} conditional` : ''}`;
    list.replaceChildren();

    if (!tools.length) {
      const empty = document.createElement('li');
      empty.className = 'agent-tool-empty';
      empty.textContent = 'No callable tools';
      list.append(empty);
      return;
    }

    tools.forEach(tool => {
      const item = document.createElement('li');
      const heading = document.createElement('div');
      const name = document.createElement('code');
      name.textContent = tool.name;
      heading.append(name);
      if (tool.availability === 'with_reference_documents') {
        const availability = document.createElement('span');
        availability.className = 'agent-tool-availability';
        availability.textContent = 'with reference docs';
        heading.append(availability);
      }
      const description = document.createElement('span');
      description.textContent = tool.description;
      item.append(heading, description);
      list.append(item);
    });
  });
}

async function loadDeepSeekPricing() {
  try {
    const snapshot = await api('/api/providers/deepseek/pricing');
    providerCatalog.deepseek.defaultPricing = Object.fromEntries(snapshot.models.map(price => [price.model, {
      cachedInput: price.cachedInputUsdPerMillion,
      input: price.inputUsdPerMillion,
      output: price.outputUsdPerMillion,
    }]));
    providerCatalog.deepseek.pricingByThinking = Object.fromEntries(snapshot.models.map(price => {
      const rates = {
        cachedInput: price.cachedInputUsdPerMillion,
        input: price.inputUsdPerMillion,
        output: price.outputUsdPerMillion,
      };
      return [price.model, Object.fromEntries(
        ['low', 'medium', 'high', 'max'].map(mode => [mode, { ...rates }]))];
    }));
    agentCatalog.forEach(agent => syncUsagePricingCard(agent.id));
    const effective = new Date(snapshot.effectiveAt).toLocaleString();
    pricingStatus.textContent = `DeepSeek ${snapshot.priceBand} prices loaded from the provider at ${effective}. Prices remain editable.`;
  } catch (error) {
    pricingStatus.textContent = `Could not refresh DeepSeek pricing; using conservative fallback prices. ${error.message}`;
  }
}

Promise.all([loadDefaultPrompts(), loadAgentTools(), loadDeepSeekPricing()])
  .then(() => { runButton.disabled = false; })
  .catch(error => showError(`Could not initialize the run form: ${error.message}`));

function syncRequiredProviderKeys() {
  const selected = new Set(
    [...agentModels.querySelectorAll('.agent-model-card')]
      .filter(isAgentCardEnabled)
      .map(card => card.querySelector('[data-field="provider"]').value));
  Object.entries(providerKeys).forEach(([providerId, input]) => {
    input.required = selected.has(providerId);
    input.classList.toggle('required-key', selected.has(providerId));
  });
}

function applyProviderToAllAgents(providerId) {
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    if (!isAgentCardEnabled(card)) return;
    card.querySelector('[data-field="provider"]').value = providerId;
    syncAgentCard(card);
  });
}

Object.entries(providerKeys).forEach(([providerId, input]) => {
  input.addEventListener('input', () => {
    if (!input.value.trim()) return;
    const populatedProviders = Object.entries(providerKeys)
      .filter(([, keyInput]) => keyInput.value.trim())
      .map(([id]) => id);
    if (populatedProviders.length === 1) applyProviderToAllAgents(providerId);
  });
});

document.querySelector('#copy-planner-settings').addEventListener('click', () => {
  const planner = agentModels.querySelector('[data-agent="planner"]');
  const selectedProvider = planner.querySelector('[data-field="provider"]').value;
  const selectedModel = planner.querySelector('[data-field="model"]').value;
  const selectedEffort = planner.querySelector('[data-field="effort"]').value;
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    if (!isAgentCardEnabled(card)) return;
    card.querySelector('[data-field="provider"]').value = selectedProvider;
    syncAgentCard(card, selectedModel, selectedEffort);
  });
});

referenceFilesInput.addEventListener('change', renderReferenceStatus);
referenceFolderInput.addEventListener('change', renderReferenceStatus);
embeddingProviderInput.addEventListener('change', syncEmbeddingProvider);

function syncEmbeddingProvider() {
  const usesOpenAi = embeddingProviderInput.value === 'openai';
  embeddingApiKeyField.hidden = !usesOpenAi;
  embeddingApiKeyInput.required = false;
  embeddingProviderGuide.textContent = usesOpenAi
    ? 'Uses OpenAI embeddings. Enter a key below or reuse the OpenAI provider key above.'
    : 'Uses local Ollama with the configured embedding model. No API key is sent.';
}

syncEmbeddingProvider();

function selectedReferenceFiles() {
  const files = [...referenceFilesInput.files, ...referenceFolderInput.files];
  const paths = new Set();
  return files.filter(file => {
    const path = file.webkitRelativePath || file.name;
    if (paths.has(path)) return false;
    paths.add(path);
    return true;
  });
}

function renderReferenceStatus(message = null) {
  if (message) {
    referenceStatus.textContent = message;
    return;
  }
  const files = selectedReferenceFiles();
  const totalBytes = files.reduce((sum, file) => sum + file.size, 0);
  referenceStatus.textContent = files.length
    ? `${files.length} reference ${files.length === 1 ? 'file' : 'files'} selected (${formatBytes(totalBytes)}).`
    : 'No reference documents attached.';
}

async function uploadReferenceDocuments() {
  const files = selectedReferenceFiles();
  if (!files.length) return null;

  if (!qdrantUrlInput || !qdrantApiKeyInput) {
    throw new Error('The reference-document form is out of date. Restart the API, then reload this page.');
  }

  const provider = embeddingProviderInput.value;
  const qdrantUrl = qdrantUrlInput.value.trim();
  const qdrantApiKey = qdrantApiKeyInput.value.trim();
  if (!qdrantUrl) {
    throw new Error('A Qdrant endpoint is required when reference documents are attached. Paste your Qdrant Cloud cluster URL.');
  }
  if (new URL(qdrantUrl).hostname.endsWith('.cloud.qdrant.io') && !qdrantApiKey) {
    throw new Error('A Qdrant Cloud database API key is required when reference documents are attached.');
  }
  const apiKey = provider === 'openai'
    ? embeddingApiKeyInput.value.trim() || providerKeys.openai.value.trim()
    : '';
  if (provider === 'openai' && !apiKey) {
    throw new Error('An OpenAI embedding API key is required when OpenAI embeddings are selected.');
  }

  renderReferenceStatus(`Indexing ${files.length} reference ${files.length === 1 ? 'file' : 'files'}…`);
  const body = new FormData();
  body.append('qdrantUrl', qdrantUrl);
  body.append('qdrantApiKey', qdrantApiKey);
  body.append('embeddingProvider', provider);
  body.append('embeddingApiKey', apiKey);
  files.forEach(file => body.append('files', file, file.webkitRelativePath || file.name));
  const response = await fetch('/api/reference-collections', { method: 'POST', body });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error || `Document indexing failed (${response.status})`);
  renderReferenceStatus(`${payload.fileCount} reference files indexed as ${payload.chunkCount} searchable chunks.`);
  return payload.id;
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearError();
  stopPolling();
  setBusy(true);
  resetOutput();
  startElapsedTimer();

  const values = new FormData(form);
  let agents;
  try {
    agents = readAgentConfigurations();
  } catch (error) {
    showError(error.message);
    setBusy(false);
    return;
  }

  let referenceCollectionId;
  try {
    referenceCollectionId = await uploadReferenceDocuments();
  } catch (error) {
    showError(error.message);
    resetElapsedTimer();
    setBusy(false);
    return;
  }

  const request = {
    repositoryUrl: values.get('repository').trim(),
    baseBranch: values.get('branch').trim(),
    task: values.get('task').trim(),
    agents,
    githubToken: values.get('githubToken').trim() || null,
    referenceCollectionId,
  };

  try {
    setActiveStep('running');
    const snapshot = await api('/api/sessions', { method: 'POST', body: JSON.stringify(request) });
    sessionId = snapshot.id;
    render(snapshot);
    pollTimer = setInterval(refresh, 1000);
  } catch (error) {
    showError(error.message);
    resetElapsedTimer();
    setBusy(false);
    setActiveStep('setup');
  }
});

function readAgentConfigurations() {
  return Object.fromEntries([...agentModels.querySelectorAll('.agent-model-card')]
    .filter(isAgentCardEnabled)
    .map(card => {
    const providerId = card.querySelector('[data-field="provider"]').value;
    const model = card.querySelector('[data-field="model"]').value;
    const effort = card.querySelector('[data-field="effort"]');
    const prompt = card.querySelector('[data-field="prompt"]').value.trim();
    const priceCard = agentModels.querySelector(`[data-usage-agent="${card.dataset.agent}"]`);
    const inputCostPerMillionTokens = readTokenPrice(priceCard.querySelector('[data-field="input-cost"]'), `${card.dataset.agent} input price`);
    const cachedInputCostPerMillionTokens = readTokenPrice(priceCard.querySelector('[data-field="cached-input-cost"]'), `${card.dataset.agent} cached input price`);
    const outputCostPerMillionTokens = readTokenPrice(priceCard.querySelector('[data-field="output-cost"]'), `${card.dataset.agent} output price`);
    const preset = providerCatalog[providerId];
    const apiKey = providerKeys[providerId].value.trim();
    if (!apiKey) throw new Error(`An API key for ${preset.name} is required by the ${card.dataset.agent} agent.`);
    return [card.dataset.agent, {
      apiKey,
      prompt: prompt || null,
      provider: {
        id: model,
        name: preset.name,
        url: preset.url,
        toolCalling: preset.toolCalling,
        vision: preset.vision,
        maxInputTokens: preset.maxInputTokens,
        maxOutputTokens: preset.maxOutputTokens,
        thinking: preset.thinking,
        reasoningEffort: effort.disabled ? null : effort.value,
        supportsReasoningEffort: preset.supportsReasoningEffort,
        inputCostPerMillionTokens,
        cachedInputCostPerMillionTokens,
        outputCostPerMillionTokens,
      },
    }];
  }));
}

function readTokenPrice(input, label) {
  if (!input.value.trim()) return null;
  const value = Number(input.value);
  if (!Number.isFinite(value) || value < 0) throw new Error(`${label} must be a non-negative number.`);
  return value;
}

cancelButton.addEventListener('click', async () => {
  if (!sessionId) return;
  await action(`/api/sessions/${sessionId}/cancel`);
});

rejectButton.addEventListener('click', async () => {
  if (!sessionId || !confirm('Reject this patch? The isolated clone will not be committed or pushed.')) return;
  await action(`/api/sessions/${sessionId}/reject`);
});

approveButton.addEventListener('click', () => {
  const suffix = sessionId ? sessionId.replaceAll('-', '').slice(0, 8) : 'change';
  document.querySelector('#target-branch').value = `agent/change-${suffix}`;
  dialog.showModal();
});

document.querySelector('#close-dialog').addEventListener('click', () => dialog.close());
document.querySelector('#dialog-cancel').addEventListener('click', () => dialog.close());

approvalForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const payload = {
    branch: document.querySelector('#target-branch').value.trim(),
    commitMessage: document.querySelector('#commit-message').value.trim(),
    push: document.querySelector('#push-branch').checked,
  };
  dialog.close();
  approveButton.disabled = true;
  await action(`/api/sessions/${sessionId}/approve`, payload);
});

generatePrButton.addEventListener('click', async () => {
  if (!sessionId) return;
  generatePrButton.disabled = true;
  generatePrButton.textContent = 'Generating…';
  await action(`/api/sessions/${sessionId}/pull-request`);
  if (!generatePrButton.hidden) {
    generatePrButton.disabled = false;
    generatePrButton.textContent = 'Generate PR';
  }
});

async function action(url, body = {}) {
  clearError();
  try {
    const snapshot = await api(url, { method: 'POST', body: JSON.stringify(body) });
    render(snapshot);
  } catch (error) {
    showError(error.message);
  }
}

async function refresh() {
  if (!sessionId) return;
  try {
    const snapshot = await api(`/api/sessions/${sessionId}`);
    render(snapshot);
    if (isTerminal(snapshot.status) || snapshot.status === 'awaiting_approval') stopPolling();
  } catch (error) {
    showError(error.message);
    stopPolling();
  }
}

async function api(url, options = {}) {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error || `Request failed (${response.status})`);
  return payload;
}

function render(snapshot) {
  setElapsedTimer(snapshot.startedAt, snapshot.status);
  sessionLabel.textContent = `${shortRepo(snapshot.repositoryUrl)} · ${snapshot.baseBranch}`;
  setStatus(snapshot.status);
  renderEvents(snapshot.events || []);
  renderUsage(snapshot.usage, snapshot.status);
  renderDiff(snapshot.diff || '');
  if (snapshot.error) showRunError(snapshot.error);
  else clearRunError();

  const awaiting = snapshot.status === 'awaiting_approval';
  approveButton.hidden = !awaiting;
  rejectButton.hidden = !awaiting;
  approveButton.disabled = false;
  const canGeneratePullRequest = ['committed', 'pushed'].includes(snapshot.status) && !snapshot.pullRequest;
  generatePrButton.hidden = !canGeneratePullRequest;
  generatePrButton.disabled = snapshot.status === 'creating_pull_request';
  generatePrButton.textContent = snapshot.status === 'creating_pull_request' ? 'Generating…' : 'Generate PR';
  pullRequestLink.hidden = !snapshot.pullRequest;
  if (snapshot.pullRequest) {
    pullRequestLink.href = snapshot.pullRequest.url;
    pullRequestLink.textContent = `View PR #${snapshot.pullRequest.number}`;
  } else {
    pullRequestLink.removeAttribute('href');
  }
  cancelButton.hidden = !['cloning', 'running'].includes(snapshot.status);
  runButton.disabled = ['cloning', 'running', 'approving'].includes(snapshot.status);

  if (snapshot.approval) {
    diffSummary.textContent = `${snapshot.approval.pushed ? 'Pushed' : 'Committed'} ${snapshot.approval.commit.slice(0, 10)} on ${snapshot.approval.branch}`;
  }
  if (snapshot.pullRequest) {
    diffSummary.textContent = `Pull request #${snapshot.pullRequest.number} created from ${snapshot.approval.branch}`;
  }
}

function renderEvents(events) {
  const fingerprint = JSON.stringify(events.map(e => [e.type, e.agent, e.text, e.source, e.target, e.status, e.category, e.usage, e.occurredAt]));
  if (fingerprint === eventFingerprint) return;
  eventFingerprint = fingerprint;
  updateTeamMap(events);
  const visibleEvents = events.filter(e => e.type !== 'run_finished').slice().reverse();
  emptyActivity.hidden = visibleEvents.length > 0;
  activity.classList.toggle('visible', visibleEvents.length > 0);
  activity.replaceChildren(...visibleEvents.map((entry) => {
    const element = document.createElement('article');
    element.className = `event ${entry.type}`;
    const header = document.createElement('header');
    const name = document.createElement('strong');
    const labels = { handoff: 'Handoff', message: 'Agent response', tool: 'Tool activity', trace: 'Workflow trace', usage: 'Token usage', decision: 'Terminal decision', error: 'Error' };
    name.textContent = entry.agent || labels[entry.type] || entry.type.replaceAll('_', ' ');
    const metadata = document.createElement('span');
    metadata.className = 'event-metadata';
    const kind = document.createElement('small');
    kind.textContent = entry.type === 'handoff' ? `${entry.source} → ${entry.target}` : 'Agent response';
    const timestamp = document.createElement('time');
    timestamp.textContent = formatEventTime(entry.occurredAt);
    if (entry.occurredAt) timestamp.dateTime = entry.occurredAt;
    const text = document.createElement('p');
    if (entry.type !== 'handoff') kind.textContent = labels[entry.type] || entry.category || 'Event';
    text.textContent = entry.type === 'handoff' ? `Control transferred to ${entry.target}.` : (entry.text || '');
    metadata.append(kind, timestamp);
    header.append(name, metadata);
    element.append(header, text);
    return element;
  }));
  activity.scrollTop = 0;
}

function renderUsage(summary, status) {
  renderUsageCard(usageSummary, summary, status, false);
  renderUsageCard(finalUsageSummary, summary, status, true);
}

function renderUsageCard(container, summary, status, finalCard) {
  const finished = status === 'awaiting_approval' || isTerminal(status);
  if (!summary || !summary.hasRequests) {
    if (finalCard && finished) {
      const title = document.createElement('h3');
      title.textContent = 'Final token usage unavailable';
      const details = document.createElement('p');
      details.textContent = `${formatNumber(summary?.llmRequestCount)} AI requests were sent, but DeepSeek did not return token-usage metadata for this run, so the application cannot calculate its token cost.`;
      container.replaceChildren(title, details);
      container.hidden = false;
    } else {
      container.hidden = true;
      container.replaceChildren();
    }
    return;
  }
  const heading = document.createElement('header');
  const title = document.createElement('h3');
  title.textContent = finalCard || finished ? 'Final token usage and cost' : 'Token usage and cost';
  const total = document.createElement('strong');
  total.textContent = summary.estimatedCostUsd == null
    ? 'Cost unavailable'
    : formatUsd(summary.estimatedCostUsd);
  heading.append(title, total);
  const details = document.createElement('p');
  const cost = summary.estimatedCostUsd == null
    ? 'Cost estimate unavailable — add input and output USD-per-million rates for every model used.'
    : `Estimated cost: ${formatUsd(summary.estimatedCostUsd)}.`;
  const agents = summary.byAgent.map(item =>
    `${item.agent}: ${formatNumber(item.inputTokens)} in (${formatNumber(item.cachedInputTokens)} cached) / ${formatNumber(item.outputTokens)} out (${formatNumber(item.reasoningTokens)} reasoning)${item.estimatedCostUsd == null ? '' : ` (${formatUsd(item.estimatedCostUsd)})`}`).join(' · ');
  details.textContent = `${formatNumber(summary.totalTokens)} total tokens · ${formatNumber(summary.inputTokens)} input (${formatNumber(summary.cachedInputTokens)} cached) · ${formatNumber(summary.outputTokens)} output (${formatNumber(summary.reasoningTokens)} reasoning). ${cost} ${agents}`;
  const requestDetails = summary.byAgent.map(item =>
    `${item.agent}: ${formatNumber(item.llmRequestCount)} requests`).join(' | ');
  details.textContent = `${formatNumber(summary.llmRequestCount)} AI requests | ${formatNumber(summary.totalTokens)} total tokens | ${formatNumber(summary.inputTokens)} input (${formatNumber(summary.cachedInputTokens)} cached) | ${formatNumber(summary.outputTokens)} output (${formatNumber(summary.reasoningTokens)} reasoning). ${cost} ${requestDetails}`;
  container.replaceChildren(heading, details);
  container.hidden = finalCard && !finished;
}

function updateTeamMap(events) {
  if (!teamMap) return;
  const relevant = events.filter(event => event.type === 'handoff' || event.agent);
  const latest = relevant.at(-1);
  const activeAgent = latest?.type === 'handoff' ? latest.target : latest?.agent;
  const activeEdge = latest?.type === 'handoff' && latest.source && latest.target
    ? `${latest.source}-${latest.target}` : null;

  teamMap.querySelectorAll('.map-node').forEach(node => {
    node.classList.toggle('active', node.dataset.agent === activeAgent);
    node.classList.toggle('dimmed', Boolean(activeAgent) && node.dataset.agent !== activeAgent);
  });
  teamMap.querySelectorAll('.handoff-edge').forEach(edge => {
    edge.classList.toggle('active', edge.dataset.edge === activeEdge);
    edge.classList.toggle('dimmed', Boolean(activeEdge) && edge.dataset.edge !== activeEdge);
  });
}

function formatEventTime(value) {
  if (!value) return 'Time unavailable';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Time unavailable' : eventDateFormatter.format(date);
}

function renderDiff(value) {
  if (!value) {
    diff.innerHTML = 'No diff available.';
    diffSummary.textContent = 'Waiting for agent changes';
    return;
  }
  const lines = value.split('\n');
  diff.innerHTML = lines.map(line => {
    let className = '';
    if (line.startsWith('diff --git') || line.startsWith('+++') || line.startsWith('---')) className = 'file';
    else if (line.startsWith('+')) className = 'add';
    else if (line.startsWith('-')) className = 'del';
    else if (line.startsWith('@@')) className = 'hunk';
    return `<span class="${className}">${escapeHtml(line)}</span>`;
  }).join('');
  const additions = lines.filter(line => line.startsWith('+') && !line.startsWith('+++')).length;
  const deletions = lines.filter(line => line.startsWith('-') && !line.startsWith('---')).length;
  diffSummary.textContent = `+${additions} additions · −${deletions} deletions`;
}

function setStatus(status) {
  const labels = {
    cloning: 'Cloning', running: 'Agents working', awaiting_approval: 'Needs approval',
    approving: 'Committing', committed: 'Committed', pushed: 'Pushed',
    creating_pull_request: 'Creating PR', pull_request_created: 'PR created', rejected: 'Rejected',
    cancelled: 'Cancelled', failed: 'Failed', approval_failed: 'Commit failed', no_changes: 'No changes',
  };
  statusBadge.textContent = labels[status] || status;
  statusBadge.className = 'status ' + (
    ['cloning', 'running', 'approving'].includes(status) ? 'working' :
    status === 'awaiting_approval' ? 'review' :
    ['committed', 'pushed', 'pull_request_created'].includes(status) ? 'success' :
    ['failed', 'approval_failed'].includes(status) ? 'failed' : 'idle');
  const active = ['cloning', 'running', 'failed', 'approval_failed'].includes(status) ? 'running' :
    status === 'awaiting_approval' ? 'review' :
    ['committed', 'pushed', 'creating_pull_request', 'pull_request_created'].includes(status) ? 'finish' : 'setup';
  setActiveStep(active);
}

function setActiveStep(active) {
  const order = ['setup', 'running', 'review', 'finish'];
  workflowSteps.forEach(step => {
    const index = order.indexOf(step.dataset.step);
    const activeIndex = order.indexOf(active);
    step.classList.toggle('active', index === activeIndex);
    step.classList.toggle('done', index < activeIndex);
    if (index === activeIndex) step.setAttribute('aria-current', 'step');
    else step.removeAttribute('aria-current');
  });
  workflowPanels.forEach(panel => {
    panel.hidden = !panel.dataset.workflowPanel.split(' ').includes(active);
  });
  if (active === 'running') {
    activityHeading?.after(teamMapSection);
  } else {
    document.querySelector('#run-settings')?.prepend(teamMapSection);
  }
}

function setBusy(value) {
  runButton.disabled = value;
  runButton.textContent = value ? 'Starting…' : 'Start agent run';
  cancelButton.hidden = !value;
}

function resetOutput() {
  eventFingerprint = '';
  activity.replaceChildren();
  activity.classList.remove('visible');
  emptyActivity.hidden = false;
  diff.innerHTML = 'No diff available.';
  diffSummary.textContent = 'Waiting for agent changes';
  approveButton.hidden = true;
  rejectButton.hidden = true;
  generatePrButton.hidden = true;
  pullRequestLink.hidden = true;
  updateTeamMap([]);
  usageSummary.hidden = true;
  usageSummary.replaceChildren();
  finalUsageSummary.hidden = true;
  finalUsageSummary.replaceChildren();
  resetElapsedTimer();
  clearRunError();
}

function setElapsedTimer(startedAt, status) {
  const parsedStart = startedAt ? new Date(startedAt).getTime() : Number.NaN;
  if (!Number.isNaN(parsedStart)) runStartedAt = parsedStart;
  if (!runStartedAt) return;

  if (isTerminal(status) || status === 'awaiting_approval') elapsedStoppedAt ??= Date.now();
  else elapsedStoppedAt = null;

  elapsedTimer.hidden = false;
  elapsedTimer.classList.toggle('is-running', !elapsedStoppedAt);
  updateElapsedTimer();
  if (!elapsedStoppedAt && !elapsedTimerInterval) elapsedTimerInterval = setInterval(updateElapsedTimer, 1000);
  if (elapsedStoppedAt && elapsedTimerInterval) {
    clearInterval(elapsedTimerInterval);
    elapsedTimerInterval = null;
  }
}

function startElapsedTimer() {
  runStartedAt = Date.now();
  elapsedStoppedAt = null;
  elapsedTimer.hidden = false;
  elapsedTimer.classList.add('is-running');
  updateElapsedTimer();
  if (!elapsedTimerInterval) elapsedTimerInterval = setInterval(updateElapsedTimer, 1000);
}

function updateElapsedTimer() {
  if (!runStartedAt) return;
  const elapsedSeconds = Math.max(0, Math.floor(((elapsedStoppedAt || Date.now()) - runStartedAt) / 1000));
  const hours = Math.floor(elapsedSeconds / 3600);
  const minutes = Math.floor((elapsedSeconds % 3600) / 60);
  const seconds = elapsedSeconds % 60;
  elapsedTimerValue.textContent = hours
    ? `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`
    : `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

function resetElapsedTimer() {
  if (elapsedTimerInterval) clearInterval(elapsedTimerInterval);
  elapsedTimerInterval = null;
  runStartedAt = null;
  elapsedStoppedAt = null;
  elapsedTimer.hidden = true;
  elapsedTimer.classList.remove('is-running');
  elapsedTimerValue.textContent = '00:00';
}

function isTerminal(status) {
  return ['committed', 'pushed', 'pull_request_created', 'rejected', 'cancelled', 'failed', 'approval_failed', 'no_changes'].includes(status);
}

function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
  setBusy(false);
}

function showError(message) {
  errorBox.textContent = message;
  errorBox.hidden = false;
}

function clearError() {
  errorBox.hidden = true;
  errorBox.textContent = '';
}

function showRunError(message) {
  runErrorBox.textContent = message;
  runErrorBox.hidden = false;
}

function clearRunError() {
  runErrorBox.hidden = true;
  runErrorBox.textContent = '';
}

function shortRepo(url) {
  try { return new URL(url).pathname.replace(/^\//, '').replace(/\.git$/, ''); }
  catch { return url; }
}

function escapeHtml(value) {
  return value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}

function formatBytes(value) {
  if (value < 1024 * 1024) return `${Math.ceil(value / 1024)} KB`;
  return `${(value / 1024 / 1024).toFixed(1)} MB`;
}

function formatNumber(value) {
  return new Intl.NumberFormat().format(value || 0);
}

function formatUsd(value) {
  return new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(value);
}

// CodeGraph MCP workbench
const codeGraphPanel = document.querySelector('#codegraph-panel');
const codeGraphDb = document.querySelector('#codegraph-db');
const codeGraphSource = document.querySelector('#codegraph-source');
const codeGraphIndexForm = document.querySelector('#codegraph-index-form');
const codeGraphIndexButton = document.querySelector('#codegraph-index-button');
const codeGraphIndexBadge = document.querySelector('#codegraph-index-badge');
const codeGraphIndexLog = document.querySelector('#codegraph-index-log');
const codeGraphError = document.querySelector('#codegraph-error');
const codeGraphOverview = document.querySelector('#codegraph-overview');
const codeGraphToolForm = document.querySelector('#codegraph-tool-form');
const codeGraphToolResult = document.querySelector('#codegraph-tool-result');
const codeGraphQuery = document.querySelector('#codegraph-query');
const codeGraphLimit = document.querySelector('#codegraph-limit');
const codeGraphBudgetGroup = document.querySelector('#codegraph-budget-group');
const codeGraphTableForm = document.querySelector('#codegraph-table-form');
const codeGraphTableSelect = document.querySelector('#codegraph-table');
const codeGraphTableResult = document.querySelector('#codegraph-table-result');
let activeCodeGraphTool = 'find_symbol';
let codeGraphIndexPoll = null;
let codeGraphTableOffset = 0;
let codeGraphTableRowCount = 0;

document.querySelectorAll('[data-app-mode]').forEach(button => button.addEventListener('click', () => {
  const graphMode = button.dataset.appMode === 'codegraph';
  document.querySelectorAll('[data-app-mode]').forEach(item => {
    item.classList.toggle('active', item === button);
    item.setAttribute('aria-pressed', String(item === button));
  });
  codeGraphPanel.hidden = !graphMode;
  document.querySelector('.steps').hidden = graphMode;
  if (graphMode) workflowPanels.forEach(panel => { panel.hidden = true; });
  else {
    const currentStep = document.querySelector('.step.active')?.dataset.step || 'setup';
    setActiveStep(currentStep);
  }
}));

if (new URLSearchParams(window.location.search).get('view') === 'codegraph')
  document.querySelector('[data-app-mode="codegraph"]').click();

async function initializeCodeGraph() {
  try {
    const config = await api('/api/codegraph/config');
    codeGraphDb.value = config.defaultDatabasePath;
    config.tables.forEach(table => codeGraphTableSelect.add(new Option(table, table)));
    codeGraphTableSelect.value = 'nodes';
    await refreshCodeGraphIndexStatus();
    if (codeGraphDb.value) await refreshCodeGraphDatabase(false);
  } catch (error) {
    showCodeGraphError(error.message);
  }
}

codeGraphIndexForm.addEventListener('submit', async event => {
  event.preventDefault();
  showCodeGraphError();
  codeGraphIndexButton.disabled = true;
  try {
    const status = await api('/api/codegraph/index', {
      method: 'POST',
      body: JSON.stringify({
        sourcePath: codeGraphSource.value.trim(),
        databasePath: codeGraphDb.value.trim(),
        incremental: document.querySelector('#codegraph-incremental').checked,
        includeExternal: document.querySelector('#codegraph-external').checked,
        restoreDependencies: document.querySelector('#codegraph-restore').checked,
      }),
    });
    renderCodeGraphIndexStatus(status);
    startCodeGraphIndexPolling();
  } catch (error) {
    codeGraphIndexButton.disabled = false;
    showCodeGraphError(error.message);
  }
});

document.querySelector('#codegraph-refresh').addEventListener('click', () => refreshCodeGraphDatabase(true));

function startCodeGraphIndexPolling() {
  if (codeGraphIndexPoll) clearInterval(codeGraphIndexPoll);
  codeGraphIndexPoll = setInterval(refreshCodeGraphIndexStatus, 1000);
}

async function refreshCodeGraphIndexStatus() {
  const status = await api('/api/codegraph/index/status');
  renderCodeGraphIndexStatus(status);
  if (status.state === 'running') startCodeGraphIndexPolling();
  else if (codeGraphIndexPoll) {
    clearInterval(codeGraphIndexPoll);
    codeGraphIndexPoll = null;
    if (status.state === 'completed' || status.state === 'completed_with_warnings') await refreshCodeGraphDatabase(false);
  }
}

function renderCodeGraphIndexStatus(status) {
  codeGraphIndexBadge.textContent = status.state.replaceAll('_', ' ');
  codeGraphIndexBadge.className = `status ${status.state === 'running' || status.state === 'completed_with_warnings' ? 'working' : status.state === 'completed' ? 'success' : status.state === 'failed' ? 'failed' : 'idle'}`;
  codeGraphIndexButton.disabled = status.state === 'running';
  const log = status.log || [];
  codeGraphIndexLog.hidden = log.length === 0 && !status.error;
  codeGraphIndexLog.textContent = [...log, status.error ? `ERROR: ${status.error}` : ''].filter(Boolean).join('\n');
  codeGraphIndexLog.scrollTop = codeGraphIndexLog.scrollHeight;
}

async function refreshCodeGraphDatabase(showErrors = true) {
  try {
    const overview = await api(`/api/codegraph/database?dbPath=${encodeURIComponent(codeGraphDb.value.trim())}`);
    codeGraphOverview.hidden = false;
    document.querySelector('#codegraph-db-meta').textContent = `${overview.databasePath} · ${formatBytes(overview.sizeBytes)}`;
    const counts = document.querySelector('#codegraph-counts');
    counts.replaceChildren(...Object.entries(overview.counts).map(([name, count]) => {
      const card = document.createElement('div'); card.className = 'codegraph-count';
      const number = document.createElement('strong'); number.textContent = formatNumber(count);
      const label = document.createElement('span'); label.textContent = name;
      card.append(number, label); return card;
    }));
    renderKindList('#codegraph-node-kinds', overview.nodeKinds, 'kind', 'count');
    renderKindList('#codegraph-edge-kinds', overview.edgeKinds, 'kind', 'count');
    showCodeGraphError();
  } catch (error) {
    codeGraphOverview.hidden = true;
    if (showErrors) showCodeGraphError(error.message);
  }
}

function renderKindList(selector, rows, nameField, countField) {
  const list = document.createElement('div'); list.className = 'codegraph-kind-list';
  rows.forEach(row => { const item = document.createElement('span'); item.textContent = `${row[nameField]} · ${formatNumber(row[countField])}`; list.append(item); });
  document.querySelector(selector).replaceChildren(list);
}

document.querySelectorAll('[data-codegraph-tool]').forEach(button => button.addEventListener('click', () => {
  activeCodeGraphTool = button.dataset.codegraphTool;
  document.querySelectorAll('[data-codegraph-tool]').forEach(item => item.classList.toggle('active', item === button));
  const usesId = activeCodeGraphTool === 'get_neighbors' || activeCodeGraphTool === 'get_context_by_id';
  const usesContext = activeCodeGraphTool.startsWith('get_context');
  document.querySelector('#codegraph-query-label').textContent = usesId ? 'Exact symbol ID' : 'Symbol name';
  codeGraphQuery.placeholder = usesId ? 'M:Namespace.Type.Method' : 'OrderService';
  codeGraphLimit.value = activeCodeGraphTool === 'get_neighbors' ? 100 : activeCodeGraphTool === 'find_symbol' ? 15 : 25;
  codeGraphLimit.max = activeCodeGraphTool === 'get_neighbors' ? 250 : 100;
  codeGraphBudgetGroup.hidden = !usesContext;
}));

codeGraphToolForm.addEventListener('submit', async event => {
  event.preventDefault();
  showCodeGraphError();
  const paths = {
    find_symbol: 'find-symbol', get_neighbors: 'get-neighbors',
    get_context: 'get-context', get_context_by_id: 'get-context-by-id',
  };
  const value = codeGraphQuery.value.trim();
  const payload = { databasePath: codeGraphDb.value.trim() };
  if (activeCodeGraphTool === 'find_symbol') Object.assign(payload, { name: value, limit: Number(codeGraphLimit.value) });
  if (activeCodeGraphTool === 'get_neighbors') Object.assign(payload, { id: value, limit: Number(codeGraphLimit.value) });
  if (activeCodeGraphTool === 'get_context') Object.assign(payload, { query: value, maxNodes: Number(codeGraphLimit.value), tokenBudget: Number(document.querySelector('#codegraph-budget').value) });
  if (activeCodeGraphTool === 'get_context_by_id') Object.assign(payload, { id: value, maxNodes: Number(codeGraphLimit.value), tokenBudget: Number(document.querySelector('#codegraph-budget').value) });
  codeGraphToolResult.replaceChildren(Object.assign(document.createElement('p'), { textContent: `Running ${activeCodeGraphTool}…` }));
  try {
    const result = await api(`/api/codegraph/tools/${paths[activeCodeGraphTool]}`, { method: 'POST', body: JSON.stringify(payload) });
    if (result.content !== undefined) {
      const pre = document.createElement('pre'); pre.textContent = result.content; codeGraphToolResult.replaceChildren(pre);
    } else renderCodeGraphTable(codeGraphToolResult, result, true);
  } catch (error) {
    codeGraphToolResult.replaceChildren(); showCodeGraphError(error.message);
  }
});

codeGraphTableForm.addEventListener('submit', event => { event.preventDefault(); codeGraphTableOffset = 0; loadCodeGraphTable(); });
document.querySelector('#codegraph-table-prev').addEventListener('click', () => { codeGraphTableOffset = Math.max(0, codeGraphTableOffset - 50); loadCodeGraphTable(); });
document.querySelector('#codegraph-table-next').addEventListener('click', () => { codeGraphTableOffset += 50; loadCodeGraphTable(); });

async function loadCodeGraphTable() {
  try {
    const params = new URLSearchParams({ dbPath: codeGraphDb.value.trim(), limit: '50', offset: String(codeGraphTableOffset), search: document.querySelector('#codegraph-table-search').value.trim() });
    const result = await api(`/api/codegraph/tables/${encodeURIComponent(codeGraphTableSelect.value)}?${params}`);
    codeGraphTableRowCount = result.rows.length;
    renderCodeGraphTable(codeGraphTableResult, result.rows, result.table === 'nodes');
    document.querySelector('#codegraph-table-prev').disabled = codeGraphTableOffset === 0;
    document.querySelector('#codegraph-table-next').disabled = codeGraphTableRowCount < 50;
    const last = codeGraphTableOffset + codeGraphTableRowCount;
    document.querySelector('#codegraph-table-page').textContent = codeGraphTableRowCount ? `Rows ${codeGraphTableOffset + 1}–${last}` : 'No rows';
  } catch (error) { showCodeGraphError(error.message); }
}

function renderCodeGraphTable(container, rows, linkSymbolIds) {
  if (!rows.length) { container.replaceChildren(Object.assign(document.createElement('p'), { textContent: 'No matching rows.' })); return; }
  const columns = [...new Set(rows.flatMap(row => Object.keys(row)))];
  const table = document.createElement('table'); table.className = 'codegraph-table';
  const head = document.createElement('thead'); const headRow = document.createElement('tr');
  columns.forEach(column => headRow.append(Object.assign(document.createElement('th'), { textContent: column }))); head.append(headRow);
  const body = document.createElement('tbody');
  rows.forEach(row => {
    const tr = document.createElement('tr');
    columns.forEach(column => {
      const td = document.createElement('td'); const value = row[column] ?? '';
      if (linkSymbolIds && column.toLowerCase() === 'id' && value) {
        const button = document.createElement('button'); button.type = 'button'; button.className = 'codegraph-symbol-link'; button.textContent = value; button.title = 'Use this symbol ID';
        button.addEventListener('click', () => { codeGraphQuery.value = value; document.querySelector('[data-codegraph-tool="get_neighbors"]').click(); codeGraphToolForm.scrollIntoView({ behavior: 'smooth', block: 'center' }); });
        td.append(button);
      } else { td.textContent = typeof value === 'object' ? JSON.stringify(value) : String(value); td.title = td.textContent; }
      tr.append(td);
    }); body.append(tr);
  });
  table.append(head, body); container.replaceChildren(table);
}

function showCodeGraphError(message = '') {
  codeGraphError.hidden = !message;
  codeGraphError.textContent = message;
}

initializeCodeGraph();
