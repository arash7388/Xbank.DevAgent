const form = document.querySelector('#run-form');
const agentModels = document.querySelector('#agent-models');
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
const embeddingApiKeyInput = document.querySelector('#embedding-api-key');
const referenceStatus = document.querySelector('#reference-status');

let sessionId = null;
let pollTimer = null;
let elapsedTimerInterval = null;
let runStartedAt = null;
let elapsedStoppedAt = null;
let eventFingerprint = '';

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
  { id: 'intake', name: 'Intake', description: 'Intake and routing', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high', required: true },
  { id: 'planner', name: 'Planner', description: 'Architecture and acceptance criteria', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high' },
  { id: 'developer', name: 'Developer', description: 'Implementation and remediation', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high', required: true },
  { id: 'reviewer', name: 'Reviewer', description: 'Correctness and quality gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high' },
  { id: 'security', name: 'Security', description: 'Threat modeling and security gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high' },
  { id: 'release_manager', name: 'Release manager', description: 'Final evidence gate', provider: 'deepseek', model: 'deepseek-v4-flash', effort: 'high', required: true },
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
  enabledInput.checked = true;
  enabledInput.disabled = Boolean(agent.required);
  enabledInput.setAttribute('aria-label', `${agent.name} enabled`);
  const enabledText = document.createElement('span');
  enabledText.textContent = agent.required ? 'Required' : 'Enabled';
  enabledLabel.append(enabledInput, enabledText);
  header.append(title, enabledLabel);
  const description = document.createElement('p');
  description.textContent = agent.description;
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
  promptLabel.textContent = 'Agent prompt';
  const promptInput = document.createElement('textarea');
  promptInput.dataset.field = 'prompt';
  promptInput.dataset.configField = '';
  promptInput.className = 'agent-prompt';
  promptInput.required = true;
  promptInput.maxLength = 50000;
  promptInput.rows = 8;
  promptInput.setAttribute('aria-label', `${agent.name} prompt`);

  controls.append(providerGroup, effortGroup);
  card.append(header, description, controls, modelLabel, modelSelect, promptLabel, promptInput);
  providerSelect.addEventListener('change', () => syncAgentCard(card));
  enabledInput.addEventListener('change', () => syncAgentCardEnabled(card));
  agentModels.append(card);
  syncAgentCard(card, agent.model, agent.effort);
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
runButton.disabled = true;

async function loadDefaultPrompts() {
  const prompts = await api('/api/agents/default-prompts');
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    const prompt = prompts[card.dataset.agent];
    if (!prompt) throw new Error(`No default prompt is configured for ${card.dataset.agent}.`);
    card.querySelector('[data-field="prompt"]').value = prompt;
  });
}

loadDefaultPrompts()
  .then(() => { runButton.disabled = false; })
  .catch(error => showError(`Could not load default agent prompts: ${error.message}`));

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

  const apiKey = embeddingApiKeyInput.value.trim() || providerKeys.openai.value.trim();
  if (!apiKey) throw new Error('An OpenAI embedding API key is required when reference documents are attached.');

  renderReferenceStatus(`Indexing ${files.length} reference ${files.length === 1 ? 'file' : 'files'}…`);
  const body = new FormData();
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
    const preset = providerCatalog[providerId];
    const apiKey = providerKeys[providerId].value.trim();
    if (!apiKey) throw new Error(`An API key for ${preset.name} is required by the ${card.dataset.agent} agent.`);
    if (!prompt) throw new Error(`A prompt is required for the ${card.dataset.agent} agent.`);
    return [card.dataset.agent, {
      apiKey,
      prompt,
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
      },
    }];
  }));
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
  const fingerprint = JSON.stringify(events.map(e => [e.type, e.agent, e.text, e.source, e.target, e.status, e.category, e.occurredAt]));
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
    const labels = { handoff: 'Handoff', message: 'Agent response', tool: 'Tool activity', trace: 'Workflow trace', decision: 'Terminal decision', error: 'Error' };
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
  (active === 'running' ? activityHeading : setupHeading)?.after(teamMapSection);
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
