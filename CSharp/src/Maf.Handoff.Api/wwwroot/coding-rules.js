(() => {
  const dialog = document.querySelector('#coding-rules-dialog');
  const form = document.querySelector('#coding-rule-form');
  const title = document.querySelector('#coding-rule-title');
  const content = document.querySelector('#coding-rule-content');
  const list = document.querySelector('#coding-rules-list');
  const status = document.querySelector('#coding-rules-status');
  const error = document.querySelector('#coding-rules-error');
  const editorTitle = document.querySelector('#coding-rule-editor-title');
  let editingId = null;
  let busy = false;

  function reset() {
    editingId = null;
    form.reset();
    editorTitle.textContent = 'Add coding rule';
  }

  async function request(path = '', method = 'GET', body) {
    const response = await fetch(`/api/coding-rules${path}`, {
      method,
      ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
    });
    if (!response.ok) {
      const detail = await response.json().catch(() => ({}));
      throw new Error(detail.error || detail.message || `Could not ${method === 'GET' ? 'load' : 'save changes to'} Coding Rules (${response.status}).`);
    }
    return response.status === 204 ? null : response.json();
  }

  function render(rules) {
    list.replaceChildren();
    status.textContent = rules.length ? `${rules.length} saved coding rule${rules.length === 1 ? '' : 's'}.` : 'No coding rules yet. Add your first rule below.';
    if (!rules.length) return;
    const wrapper = document.createElement('div');
    wrapper.className = 'run-history-table-wrap coding-rules-table-wrap';
    const table = document.createElement('table');
    table.className = 'run-history-table coding-rules-table';
    table.setAttribute('aria-label', 'Saved coding rules');
    const head = document.createElement('thead');
    const headings = document.createElement('tr');
    for (const label of ['Title', 'Rule', 'Enabled', 'Actions']) {
      const cell = document.createElement('th');
      cell.setAttribute('scope', 'col');
      cell.textContent = label;
      headings.append(cell);
    }
    head.append(headings);
    const body = document.createElement('tbody');
    for (const rule of rules) {
      const item = document.createElement('tr');
      item.className = 'coding-rule-item';
      const heading = document.createElement('th');
      heading.setAttribute('scope', 'row');
      heading.className = 'coding-rule-name';
      heading.textContent = rule.title;
      const text = document.createElement('td');
      text.className = 'coding-rule-text';
      text.textContent = rule.content;
      const enabledCell = document.createElement('td');
      enabledCell.className = 'coding-rule-enabled';
      const enabled = document.createElement('input');
      enabled.type = 'checkbox';
      enabled.checked = rule.enabled;
      enabled.setAttribute('aria-label', `Enable ${rule.title}`);
      enabled.addEventListener('change', () => {
        if (busy) { enabled.checked = rule.enabled; return; }
        const selected = enabled.checked;
        perform(async () => {
          try {
            await request(`/${rule.id}/enabled`, 'PUT', { enabled: selected });
            rule.enabled = selected;
          } catch (failure) { enabled.checked = rule.enabled; throw failure; }
        });
      });
      enabledCell.append(enabled);
      const actions = document.createElement('div');
      actions.className = 'coding-rule-actions';
      const actionCell = document.createElement('td');
      const edit = document.createElement('button');
      edit.type = 'button';
      edit.className = 'secondary coding-rule-edit';
      edit.textContent = 'Edit';
      edit.setAttribute('aria-label', `Edit ${rule.title}`);
      edit.addEventListener('click', () => {
        editingId = rule.id;
        title.value = rule.title;
        content.value = rule.content;
        editorTitle.textContent = 'Edit coding rule';
        title.focus();
      });
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'secondary coding-rule-delete';
      remove.textContent = 'Delete';
      remove.setAttribute('aria-label', `Delete ${rule.title}`);
      remove.addEventListener('click', () => {
        if (!window.confirm(`Delete coding rule “${rule.title}”?`)) return;
        perform(async () => {
          await request(`/${rule.id}`, 'DELETE');
          if (editingId === rule.id) reset();
          render(await request());
        });
      });
      actions.append(edit, remove);
      actionCell.append(actions);
      item.append(heading, text, enabledCell, actionCell);
      body.append(item);
    }
    table.append(head, body);
    wrapper.append(table);
    list.append(wrapper);
  }

  async function perform(action) {
    if (busy) return;
    busy = true;
    error.hidden = true;
    dialog.setAttribute('aria-busy', 'true');
    const setDisabled = disabled => dialog.querySelectorAll('input, textarea, button').forEach(control => {
      if (control.id !== 'coding-rules-close') control.disabled = disabled;
    });
    setDisabled(true);
    try { await action(); }
    catch (failure) { status.textContent = ''; error.textContent = failure.message; error.hidden = false; }
    finally { busy = false; setDisabled(false); dialog.setAttribute('aria-busy', 'false'); }
  }

  document.querySelector('#coding-rules-button').addEventListener('click', () => {
    dialog.showModal();
    perform(async () => {
      status.textContent = 'Loading Coding Rules…';
      list.replaceChildren();
      render(await request());
    });
  });
  document.querySelector('#coding-rules-close').addEventListener('click', () => dialog.close());
  document.querySelector('#coding-rule-reset').addEventListener('click', reset);
  form.addEventListener('submit', event => {
    event.preventDefault();
    const body = { title: title.value, content: content.value };
    perform(async () => {
      await request(editingId ? `/${editingId}` : '', editingId ? 'PUT' : 'POST', body);
      reset();
      render(await request());
    });
  });
})();
