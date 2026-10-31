(() => {
  const dialog = document.querySelector('#run-history-dialog');
  const list = document.querySelector('#run-history-list');
  const details = document.querySelector('#run-history-details');
  const message = document.querySelector('#run-history-message');
  const errorBox = document.querySelector('#run-history-error');
  const refresh = document.querySelector('#run-history-refresh');
  const limit = document.querySelector('#run-history-limit');
  const previous = document.querySelector('#run-history-prev');
  const next = document.querySelector('#run-history-next');
  const pageLabel = document.querySelector('#run-history-page');
  let page = 0;
  let listVersion = 0;
  let detailVersion = 0;

  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text != null) element.textContent = String(text);
    if (className) element.className = className;
    return element;
  }

  function date(value) {
    if (!value) return '—';
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleString();
  }

  function statusBadge(value) {
    const status = String(value || 'unknown');
    const tone = /^(completed|committed|pushed|pull_request_created|success)$/.test(status) ? 'success'
      : /^(failed|approval_failed|error)$/.test(status) ? 'danger'
      : /^(running|cloning|approving|creating_pull_request|in_progress)$/.test(status) ? 'active'
      : /^(awaiting_approval|awaiting_input|pending)$/.test(status) ? 'warning' : 'neutral';
    return node('span', status.replace(/_/g, ' '), `history-status history-status-${tone}`);
  }

  function duration(start, end) {
    if (!start || !end) return '—';
    const milliseconds = new Date(end) - new Date(start);
    if (!Number.isFinite(milliseconds) || milliseconds < 0) return '—';
    const seconds = Math.floor(milliseconds / 1000);
    return `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m ${seconds % 60}s`;
  }

  async function get(url) {
    const response = await fetch(url, { headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`Request failed (${response.status}).`);
    return response.json();
  }

  function table(headers, rows) {
    const wrapper = node('div', null, 'run-history-table-wrap');
    wrapper.tabIndex = 0;
    wrapper.setAttribute('role', 'region');
    wrapper.setAttribute('aria-label', headers.join(', '));
    const result = node('table', null, 'run-history-table');
    const head = node('thead');
    const heading = node('tr');
    headers.forEach(text => {
      const cell = node('th', text);
      cell.scope = 'col';
      heading.append(cell);
    });
    head.append(heading);
    const body = node('tbody');
    rows.forEach(values => {
      const row = node('tr');
      values.forEach(value => {
        const cell = node('td');
        if (value && typeof value === 'object') cell.append(value);
        else cell.textContent = value == null || value === '' ? '—' : String(value);
        row.append(cell);
      });
      body.append(row);
    });
    result.append(head, body);
    wrapper.append(result);
    return wrapper;
  }

  function recordDetails(entry) {
    const disclosure = node('details');
    disclosure.append(node('summary', 'Input / output / error'));
    [['Input', entry.inputJson], ['Output', entry.responseJson], ['Error', entry.errorJson]]
      .forEach(([label, value]) => {
        if (value == null || value === '') return;
        disclosure.append(node('h4', label));
        let text = value;
        try { text = JSON.stringify(JSON.parse(value), null, 2); } catch { /* Plain text is also stored. */ }
        disclosure.append(node('pre', text));
      });
    return disclosure;
  }

  async function selectRun(id) {
    const version = ++detailVersion;
    details.hidden = false;
    details.replaceChildren(node('p', 'Loading run details…'));
    const reducedMotion = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    details.scrollIntoView({ block: 'start', behavior: reducedMotion ? 'auto' : 'smooth' });
    try {
      const log = await get(`/api/runs/${encodeURIComponent(id)}`);
      if (version !== detailVersion) return;
      const run = log.run;
      details.replaceChildren(node('h3', `Run ${run.id}`));
      details.append(table(['Status', 'Created', 'Started', 'Finished', 'Updated', 'Error'], [[
        statusBadge(run.status), date(run.createdAt), date(run.startedAt), date(run.finishedAt), date(run.updatedAt), run.error,
      ]]));
      const agents = log.agentRuns || [];
      details.append(node('h3', `Agent runs (${agents.length})`));
      if (agents.length) details.append(table(
        ['Agent', 'Model', 'Status', 'Attempt', 'Started', 'Completed', 'Duration', 'Details'],
        agents.map(entry => [entry.agent, entry.model, statusBadge(entry.status), entry.attempt, date(entry.startedAt),
          date(entry.completedAt), duration(entry.startedAt, entry.completedAt), recordDetails(entry)])));
      else details.append(node('p', 'No agent runs were recorded.'));
      const tools = log.toolCalls || [];
      details.append(node('h3', `Tool calls (${tools.length})`));
      if (tools.length) details.append(table(
        ['Agent', 'Tool', 'Status', 'Started', 'Duration (ms)', 'Details'],
        tools.map(entry => [entry.agent, entry.tool, statusBadge(entry.status), date(entry.startedAt),
          entry.durationMilliseconds, recordDetails(entry)])));
      else details.append(node('p', 'No tool calls were recorded.'));
    } catch (error) {
      if (version !== detailVersion) return;
      const failure = node('p', `Could not load run details: ${error.message}`, 'form-error');
      failure.setAttribute('role', 'alert');
      const retry = node('button', 'Retry details', 'secondary');
      retry.type = 'button';
      retry.addEventListener('click', () => selectRun(id));
      details.replaceChildren(failure, retry);
    }
  }

  async function loadRuns() {
    const version = ++listVersion;
    const pageSize = Number(limit.value);
    const offset = page * pageSize;
    ++detailVersion;
    details.hidden = true;
    details.replaceChildren();
    list.replaceChildren();
    errorBox.hidden = true;
    message.textContent = 'Loading previous runs…';
    refresh.disabled = true;
    previous.disabled = true;
    next.disabled = true;
    dialog.setAttribute('aria-busy', 'true');
    pageLabel.textContent = `Page ${page + 1}`;
    try {
      // One extra record tells us whether another page exists without loading all history.
      const results = await get(`/api/runs?limit=${pageSize + 1}&offset=${offset}`);
      if (version !== listVersion) return;
      const runs = results.slice(0, pageSize);
      next.disabled = results.length <= pageSize;
      message.textContent = runs.length
        ? `Showing runs ${offset + 1}–${offset + runs.length}`
        : page === 0 ? 'No previous runs have been saved yet.' : 'No runs on this page. Go back or refresh the history.';
      if (!runs.length) return;
      list.append(table(['Run ID', 'Status', 'Created', 'Started', 'Finished', 'Duration', 'Error', 'Details'],
        runs.map(run => {
          const button = node('button', 'Explore →', 'secondary history-explore');
          button.type = 'button';
          button.setAttribute('aria-label', `View details for run ${run.id}`);
          button.addEventListener('click', () => {
            list.querySelectorAll('.history-explore').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
            selectRun(run.id);
          });
          button.setAttribute('aria-pressed', 'false');
          const identity = node('code', run.id, 'history-run-id');
          return [identity, statusBadge(run.status), date(run.createdAt), date(run.startedAt), date(run.finishedAt),
            duration(run.startedAt, run.finishedAt), run.error, button];
        })));
    } catch (error) {
      if (version !== listVersion) return;
      message.textContent = '';
      errorBox.textContent = `Could not load previous runs: ${error.message} Use Refresh to try again.`;
      errorBox.hidden = false;
    } finally {
      if (version === listVersion) {
        refresh.disabled = false;
        previous.disabled = page === 0;
        dialog.setAttribute('aria-busy', 'false');
      }
    }
  }

  document.querySelector('#previous-runs-button').addEventListener('click', () => {
    dialog.showModal();
    page = 0;
    loadRuns();
  });
  document.querySelector('#run-history-close').addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => { ++listVersion; ++detailVersion; });
  refresh.addEventListener('click', () => { page = 0; return loadRuns(); });
  limit.addEventListener('change', () => { page = 0; return loadRuns(); });
  previous.addEventListener('click', () => { if (page > 0) --page; return loadRuns(); });
  next.addEventListener('click', () => { ++page; return loadRuns(); });
})();
