(() => {
  const get = id => document.getElementById(id);
  const dialog = get('task-analysis-dialog');
  const form = get('task-analysis-form');
  const provider = get('analysis-provider');
  const model = get('analysis-model');
  const effort = get('analysis-effort');
  const result = get('analysis-result');
  const status = get('analysis-status');
  const error = get('analysis-error');
  const notices = get('analysis-notices');
  const repository = get('analysis-repository');
  const repositoryStatus = get('analysis-repository-status');
  const storageKey = 'maf-task-analysis-settings-v2';
  const fields = ['analyse', 'summarize', 'sample', 'language', 'provider', 'model', 'effort',
    'endpoint', 'custom-model', 'input-limit', 'output-limit', 'prompt', 'repository'];
  let pending = null;
  let savedRepository = '';
  let repositoriesLoading = false;
  Object.entries(providerCatalog).forEach(([id, item]) => provider.add(new Option(item.name, id)));
  provider.value = 'deepseek';

  function syncProvider() {
    const definition = providerCatalog[provider.value];
    model.replaceChildren(...definition.models.map(([id, label]) => new Option(label, id)));
    effort.replaceChildren(...(definition.supportsReasoningEffort.length
      ? definition.supportsReasoningEffort.map(value => new Option(value, value))
      : [new Option('Unavailable', '')]));
    effort.disabled = !definition.thinking;
    effort.value = definition.reasoningEffort || '';
    get('analysis-endpoint').value = definition.url;
    get('analysis-custom-model').value = '';
    get('analysis-input-limit').value = 1000000;
    get('analysis-output-limit').value = 285000;
  }
  function save() {
    const settings = Object.fromEntries(fields.map(name => {
      const control = get(`analysis-${name}`);
      return [name, control.type === 'checkbox' ? control.checked : control.value];
    }));
    try { localStorage.setItem(storageKey, JSON.stringify(settings)); } catch { /* Storage may be disabled. */ }
  }
  syncProvider();
  try {
    const current = localStorage.getItem(storageKey);
    const saved = JSON.parse(current || localStorage.getItem('maf-task-analysis-settings-v1') || 'null');
    if (saved && providerCatalog[saved.provider]) {
      provider.value = saved.provider;
      syncProvider();
      fields.forEach(name => {
        if (!current && ['input-limit', 'output-limit'].includes(name)) return;
        const control = get(`analysis-${name}`);
        if (saved[name] !== undefined) {
          if (control.type === 'checkbox') control.checked = Boolean(saved[name]);
          else control.value = saved[name];
        }
      });
      savedRepository = saved.repository || '';
    }
  } catch { /* Ignore corrupt or unavailable preferences. */ }
  provider.addEventListener('change', syncProvider);
  form.addEventListener('change', save);
  async function loadRepositories() {
    if (repositoriesLoading) return;
    repositoriesLoading = true;
    const selected = repository.value || savedRepository;
    repository.disabled = true;
    repositoryStatus.textContent = 'Loading configured repositories…';
    try {
      const response = await fetch('/api/projects');
      if (!response.ok) throw new Error('Repositories could not be loaded. Use Refresh repositories to retry.');
      const projects = await response.json();
      repository.replaceChildren(new Option('Task context only', ''),
        ...projects.map(project => new Option(project.key, project.key)));
      repository.value = projects.some(project => project.key === selected) ? selected : '';
      savedRepository = repository.value;
      updateRepositoryStatus();
      if (!projects.length) repositoryStatus.textContent = 'No repositories are configured. Add a project with a local RepoPath in server settings.';
    } catch (failure) { repositoryStatus.textContent = failure.message; }
    finally { repository.disabled = false; repositoriesLoading = false; }
  }
  function updateRepositoryStatus() {
    repositoryStatus.textContent = repository.value
      ? `${repository.value}: validate against its configured local checkout, including local changes. Source access is read-only.`
      : 'Task context only. Select a repository to validate requirements against its current local codebase.';
  }
  repository.addEventListener('change', () => { savedRepository = repository.value; updateRepositoryStatus(); });
  get('analysis-repositories-refresh').addEventListener('click', loadRepositories);
  get('task-analysis-button').addEventListener('click', () => { dialog.showModal(); loadRepositories(); });
  function stop() { pending?.abort(); }
  get('task-analysis-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', stop);
  dialog.addEventListener('cancel', stop);
  get('analysis-stop').addEventListener('click', stop);
  get('analysis-copy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(result.textContent + (notices.textContent ? `\n\nEvidence limitations:\n${[...notices.children].map(item => item.textContent).join('\n')}` : ''));
      status.textContent = 'Result copied.';
    } catch { status.textContent = 'Copy unavailable. Select and copy the result text.'; }
  });
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (pending) return;
    error.hidden = true;
    if (repositoriesLoading) {
      error.textContent = 'Wait for the repository list to finish loading before generating.';
      error.hidden = false;
      return;
    }
    if (!get('analysis-analyse').checked && !get('analysis-summarize').checked) {
      error.textContent = 'Select Analyse task or Summarize task.';
      error.hidden = false;
      return;
    }
    save();
    const definition = providerCatalog[provider.value];
    const outputLanguage = get('analysis-language').value;
    const issueKey = get('analysis-issue').value.trim().toUpperCase();
    const payload = {
      issueKey, outputLanguage, projectKey: repository.value || null,
      analyseTask: get('analysis-analyse').checked,
      summarizeTask: get('analysis-summarize').checked,
      includeSample: get('analysis-sample').checked,
      model: {
        apiKey: get('analysis-key').value.trim(),
        prompt: get('analysis-prompt').value.trim(),
        provider: {
          id: get('analysis-custom-model').value.trim() || model.value,
          name: definition.name, url: get('analysis-endpoint').value.trim(),
          toolCalling: true, vision: definition.vision,
          maxInputTokens: Number(get('analysis-input-limit').value),
          maxOutputTokens: Number(get('analysis-output-limit').value),
          thinking: definition.thinking,
          reasoningEffort: definition.thinking ? effort.value : null,
          supportsReasoningEffort: definition.supportsReasoningEffort,
        },
      },
    };
    pending = new AbortController();
    get('analysis-submit').disabled = true;
    get('analysis-stop').hidden = false;
    get('analysis-copy').disabled = true;
    result.hidden = true;
    notices.replaceChildren();
    status.textContent = `Reading ${issueKey}, comments, and attachments${payload.projectKey ? `; inspecting ${payload.projectKey}` : ''}, then generating…`;
    try {
      const response = await fetch('/api/task-analysis', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload), signal: pending.signal,
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.error || data.message || `Task Analysis failed (${response.status}).`);
      result.textContent = data.text;
      result.lang = data.outputLanguage === 'Persian' ? 'fa' : 'en';
      result.dir = data.outputLanguage === 'Persian' ? 'rtl' : 'ltr';
      result.hidden = false;
      for (const message of data.notices || []) {
        const item = document.createElement('li');
        item.textContent = message;
        notices.append(item);
      }
      get('analysis-copy').disabled = false;
      status.textContent = `${data.issueKey} · ${data.outputLanguage} · Complete`;
    } catch (failure) {
      if (failure.name === 'AbortError') status.textContent = 'Task Analysis stopped.';
      else {
        error.textContent = failure.message;
        error.hidden = false;
        status.textContent = 'Task Analysis failed.';
      }
    } finally {
      pending = null;
      get('analysis-submit').disabled = false;
      get('analysis-stop').hidden = true;
    }
  });
})();
