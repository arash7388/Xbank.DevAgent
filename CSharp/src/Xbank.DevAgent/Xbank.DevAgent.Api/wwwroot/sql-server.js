(() => {
  const field = name => document.querySelector(`#sqlserver-${name}`);
  const status = field('status');
  const form = field('form');
  const toggle = field('enabled');
  const connect = field('connect');
  const result = field('result');
  let busy = false;
  let revision = 0;
  async function request(path, options) {
    const response = await fetch(`/api/sql-server${path}`, options);
    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(error.error || error.message || 'SQL Server request failed.');
    }
    return response.status === 204 ? null : response.json();
  }
  const json = body => ({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  async function load() {
    if (busy) return;
    const current = ++revision;
    try {
      const settings = await request('');
      if (current !== revision) return;
      toggle.checked = settings.enabled;
      status.textContent = settings.enabled ? 'Access enabled. Connect to verify the database.'
        : 'Access disabled. Save a connection in Settings, then enable access.';
      result.textContent = '';
    } catch (error) { if (current === revision) status.textContent = error.message; }
  }
  async function work(action) {
    if (busy) return;
    busy = true;
    revision++;
    toggle.disabled = connect.disabled = true;
    form.querySelectorAll('button, input, select, textarea').forEach(item => { item.disabled = true; });
    status.textContent = 'Connecting to SQL Server...';
    result.textContent = '';
    try { await action(); }
    catch (error) { status.textContent = error.message; }
    finally {
      busy = false;
      toggle.disabled = connect.disabled = false;
      form.querySelectorAll('button, input, select, textarea').forEach(item => { item.disabled = false; });
    }
  }
  toggle.addEventListener('change', () => {
    const enabled = toggle.checked;
    work(async () => {
      try {
        await request('/enabled', { ...json({ enabled }), method: 'PUT' });
        status.textContent = enabled ? 'Access enabled for new operations and agent runs.' : 'Access disabled for new operations and agent runs.';
      } catch (error) { toggle.checked = !enabled; throw error; }
    });
  });
  connect.addEventListener('click', () => work(async () => {
    const data = await request('/execute', json({ operation: 'query', sql: 'SELECT DB_NAME() AS database_name, @@VERSION AS server_version' }));
    result.textContent = JSON.stringify(data, null, 2);
    status.textContent = 'Connected. SQL Server answered through Alyio MCP.';
  }));
  function showFields() {
    const operation = field('operation').value;
    field('object-fields').hidden = !['schema', 'object', 'migrations'].includes(operation);
    field('query-fields').hidden = !['query', 'analyze'].includes(operation);
    field('name').required = operation === 'object';
  }
  field('operation').addEventListener('change', showFields);
  form.addEventListener('submit', event => {
    event.preventDefault();
    const operation = field('operation').value;
    let parameters;
    try {
      if (['query', 'analyze'].includes(operation)) {
        parameters = JSON.parse(field('parameters').value || '{}');
        if (!parameters || Array.isArray(parameters) || typeof parameters !== 'object') throw new Error();
      }
    } catch { status.textContent = 'Parameters must be a JSON object.'; return; }
    work(async () => {
      const data = await request('/execute', json({ operation, sql: field('query').value,
        parameters, schema: field('schema').value.trim() || null, name: field('name').value.trim() || null,
        kind: field('kind').value, estimated: true }));
      result.textContent = JSON.stringify(data, null, 2);
      status.textContent = 'Operation complete. Results may be capped by the configured row limit.';
    });
  });
  document.querySelector('[data-app-mode="sqlserver"]').addEventListener('click', load);
  window.addEventListener('sql-server-settings-changed', load);
  showFields();
  if (new URLSearchParams(window.location.search).get('view') === 'sqlserver') load();
})();
