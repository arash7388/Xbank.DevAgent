(() => {
  const dialog = document.querySelector('#developer-settings-dialog');
  const form = document.querySelector('#developer-settings-form');
  const rows = document.querySelector('#settings-repositories');
  const status = document.querySelector('#developer-settings-status');
  const field = name => document.querySelector(`#settings-${name}`);
  const secret = (name, clear) => clear.checked ? '' : (name.value.trim() || null);
  let revision = 0;
  let repositoryNumber = 0;
  function setStatus(message, state = '') {
    status.textContent = message;
    status.dataset.state = state;
  }
  function bindClear(input, checkbox) {
    checkbox.addEventListener('change', () => { input.disabled = checkbox.checked; });
  }
  function refreshCredential(input, clear, fallback, saved = 'Token saved — leave blank to keep') {
    if (clear.checked) input.placeholder = fallback;
    else if (input.value.trim()) input.placeholder = saved;
    input.value = '';
    input.disabled = false;
    clear.checked = false;
  }
  function syncJiraAuth() { field('jira-user').required = field('jira-auth').value === 'Basic'; }
  function setBusy(busy) {
    form.querySelectorAll('fieldset').forEach(section => { section.disabled = busy; });
  }
  bindClear(field('gitlab-token'), field('gitlab-clear'));
  bindClear(field('jira-token'), field('jira-clear'));
  field('jira-auth').addEventListener('change', syncJiraAuth);
  async function request(options) {
    const response = await fetch('/api/settings', options);
    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(error.error || error.message || 'Settings could not be saved or loaded.');
    }
    return response.status === 204 ? null : response.json();
  }
  function addRepository(repository = {}) {
    const row = document.createElement('fieldset');
    row.className = 'settings-repository';
    const legend = document.createElement('legend');
    legend.textContent = `Repository ${++repositoryNumber}`;
    row.append(legend);
    const inputs = {};
    for (const [key, label, type] of [['key', 'Repository name', 'text'], ['repoPath', 'Absolute local path', 'text'], ['gitLabProjectPath', 'GitLab repository URL', 'url'], ['token', 'Repository access token', 'password']]) {
      const wrapper = document.createElement('label');
      wrapper.textContent = label;
      const input = document.createElement('input');
      input.type = type;
      input.value = key === 'token' ? '' : repository[key] || '';
      input.required = key === 'key' || key === 'gitLabProjectPath';
      if (key === 'token') {
        input.autocomplete = 'new-password';
        input.placeholder = repository.hasToken ? 'Token saved — leave blank to keep' : 'Use shared token';
      }
      if (key === 'repoPath') input.placeholder = 'e.g. C:\\work\\my-repository (optional)';
      if (key === 'gitLabProjectPath') input.placeholder = 'https://gitlab.example.com/team/repository.git';
      if (key === 'key') input.placeholder = 'e.g. my-service';
      wrapper.append(input);
      row.append(wrapper);
      inputs[key] = input;
    }
    const clearLabel = document.createElement('label');
    clearLabel.className = 'settings-check';
    const clear = document.createElement('input');
    clear.type = 'checkbox';
    clearLabel.append(clear, document.createTextNode(' Clear repository token'));
    bindClear(inputs.token, clear);
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'settings-remove';
    remove.textContent = 'Remove repository';
    remove.addEventListener('click', () => row.remove());
    row.append(clearLabel, remove);
    row.readSettings = () => ({ key: inputs.key.value.trim(), repoPath: inputs.repoPath.value.trim(),
      gitLabProjectPath: inputs.gitLabProjectPath.value.trim(), token: secret(inputs.token, clear) });
    row.refreshCredential = () => refreshCredential(inputs.token, clear, 'Use shared token');
    rows.append(row);
  }
  document.querySelector('#developer-settings-button').addEventListener('click', async () => {
    const current = ++revision;
    form.reset();
    field('gitlab-token').disabled = false;
    field('jira-token').disabled = false;
    repositoryNumber = 0;
    rows.replaceChildren();
    setBusy(true);
    setStatus('Loading settings…');
    form.querySelector('[type="submit"]').disabled = true;
    field('add-repository').disabled = true;
    field('reload').hidden = true;
    dialog.showModal();
    try {
      const settings = await request();
      if (current !== revision) return;
      for (const [name, key] of [['gitlab-host', 'gitLabHost'], ['jira-url', 'jiraBaseUrl'], ['jira-user', 'jiraUser'], ['jira-requester', 'jiraRequester'], ['jira-requester-field', 'jiraRequesterField'], ['jira-auth', 'jiraAuthMode']]) field(name).value = settings[key] || '';
      field('gitlab-token').placeholder = settings.hasGitLabToken ? 'Token saved — leave blank to keep' : 'No shared token saved';
      field('jira-token').placeholder = settings.hasJiraToken ? 'Credential saved — leave blank to keep' : 'No credential saved';
      settings.repositories.forEach(addRepository);
      syncJiraAuth();
      setBusy(false);
      setStatus('');
      form.querySelector('[type="submit"]').disabled = false;
      field('add-repository').disabled = false;
    } catch (error) { if (current === revision) setStatus(error.message, 'error'); }
  });
  document.querySelector('#developer-settings-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => { revision++; form.reset(); rows.replaceChildren(); });
  field('add-repository').addEventListener('click', () => { addRepository(); rows.lastElementChild.querySelector('input').focus(); });
  field('reload').addEventListener('click', () => window.location.reload());
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const button = form.querySelector('[type="submit"]');
    const current = revision;
    button.disabled = true;
    setStatus('Saving settings…');
    const snapshot = [...rows.children];
    setBusy(true);
    try {
      await request({ method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        gitLabHost: field('gitlab-host').value.trim(), gitLabToken: secret(field('gitlab-token'), field('gitlab-clear')),
        jiraBaseUrl: field('jira-url').value.trim(), jiraUser: field('jira-user').value.trim(), jiraRequester: field('jira-requester').value.trim(),
        jiraRequesterField: field('jira-requester-field').value.trim(),
        jiraAuthMode: field('jira-auth').value, jiraToken: secret(field('jira-token'), field('jira-clear')),
        repositories: snapshot.map(row => row.readSettings()),
      }) });
      if (current !== revision) return;
      refreshCredential(field('gitlab-token'), field('gitlab-clear'), 'No shared token saved');
      refreshCredential(field('jira-token'), field('jira-clear'), 'No credential saved', 'Credential saved — leave blank to keep');
      snapshot.forEach(row => row.refreshCredential());
      setStatus('Settings saved. Reload to refresh repository lists.', 'success');
      field('reload').hidden = false;
    } catch (error) { if (current === revision) setStatus(error.message, 'error'); }
    finally { if (current === revision) { button.disabled = false; setBusy(false); } }
  });
})();
