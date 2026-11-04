(function (root) {
  function fromSnapshot(snapshot) {
    if (!snapshot.error) return null;
    if (snapshot.failureDetails) return snapshot.failureDetails;
    // Support older servers and archived runs with streaming message fragments.
    const messages = [];
    let current = null;
    for (const event of snapshot.events || []) {
      if (event.type !== 'message') { current = null; continue; }
      if (!current || current.agent !== event.agent) {
        current = { agent: event.agent, text: '' };
        messages.push(current);
      }
      current.text += event.text || '';
    }
    const terminal = [...(snapshot.events || [])].reverse().find(event => event.type === 'run_finished');
    const status = terminal?.status;
    const source = [...messages].reverse().find(message =>
      status === 'loop_detected' ? message.text.includes('WORKFLOW_LOOP_DETECTED')
      : status === 'input_required'
        ? ['jira', 'software-delivery-jira'].includes(message.agent) && message.text.includes('JIRA_INPUT_REQUIRED')
        : ['rejected_by_release', 'review_approval_missing'].includes(status)
          && ['release_manager', 'software-delivery-release_manager'].includes(message.agent))
      || (status === 'input_required' ? messages[messages.length - 1] : null);
    if (status === 'loop_detected') return {
      title: 'Run stopped without progress', status,
      summary: 'The proposed changes are preserved and have not been approved.',
      message: 'The agents repeated the same work without resolving the remaining issue. Review the last build or review findings in the activity log before starting a new run.',
      technicalDetails: source?.text || snapshot.error,
    };
    return {
      title: status === 'input_required' ? 'Clarification needed'
        : status === 'loop_detected' ? 'Workflow loop detected'
        : status === 'rejected_by_release' ? 'Release blocked'
          : status?.includes('limit') ? 'Workflow limit reached' : 'Workflow stopped',
      summary: snapshot.error, status, agent: source?.agent, message: source?.text || snapshot.error,
    };
  }

  function inline(container, text, doc) {
    // Build DOM nodes directly. Model output never becomes HTML or executable markup.
    const tokens = /(`[^`\n]+`|\*\*[^*\n]+\*\*|\*[^*\n]+\*|\[[^\]\n]+\]\([^\s)]+\))/g;
    let offset = 0;
    for (const match of text.matchAll(tokens)) {
      container.append(doc.createTextNode(text.slice(offset, match.index)));
      const value = match[0];
      let element;
      if (value.startsWith('`')) {
        element = doc.createElement('code'); element.textContent = value.slice(1, -1);
      } else if (value.startsWith('**')) {
        element = doc.createElement('strong'); element.textContent = value.slice(2, -2);
      } else if (value.startsWith('*')) {
        element = doc.createElement('em'); element.textContent = value.slice(1, -1);
      } else {
        const link = /^\[([^\]]+)\]\(([^)]+)\)$/.exec(value);
        let safe = false;
        try { safe = ['https:', 'http:'].includes(new URL(link[2]).protocol); } catch {}
        element = doc.createElement(safe ? 'a' : 'span');
        element.textContent = safe ? link[1] : value;
        if (safe) { element.href = link[2]; element.target = '_blank'; element.rel = 'noopener noreferrer'; }
      }
      container.append(element);
      offset = match.index + value.length;
    }
    container.append(doc.createTextNode(text.slice(offset)));
  }

  function renderMessage(container, message) {
    const doc = container.ownerDocument;
    const text = message.replace(/\r\n?/g, '\n')
      .replace(/^\s*(?:JIRA_INPUT_REQUIRED|WORKFLOW_REJECTED|WORKFLOW_COMPLETE)\s*$/gm, '').trim();
    let section = container;
    let paragraph = null;
    let list = null;
    let code = null;
    function block() { paragraph = null; list = null; }
    for (const line of text.split('\n')) {
      if (/^\s*```/.test(line)) {
        block();
        if (code) { code = null; continue; }
        const pre = doc.createElement('pre'); code = doc.createElement('code');
        pre.append(code); section.append(pre); continue;
      }
      if (code) { code.append(doc.createTextNode(line + '\n')); continue; }
      if (!line.trim()) { block(); continue; }
      const heading = /^(?:#{1,6}\s+(.+?)\s*#*|\*\*(.+?)\*\*)$/.exec(line.trim());
      if (heading) {
        block();
        section = doc.createElement('section'); section.className = 'run-diagnostic-section';
        const label = heading[1] || heading[2];
        if (/questions|blockers|next steps|clarification/i.test(label)) section.classList.add('actionable');
        const title = doc.createElement('h4'); inline(title, label, doc);
        section.append(title); container.append(section); continue;
      }
      const item = /^\s*(?:([-*])\s+|(\d+)[.)]\s+)(.*)$/.exec(line);
      if (item) {
        paragraph = null;
        const tag = item[2] ? 'ol' : 'ul';
        if (!list || list.localName !== tag) {
          list = doc.createElement(tag);
          if (item[2]) list.start = Number(item[2]);
          section.append(list);
        }
        const li = doc.createElement('li'); inline(li, item[3], doc); list.append(li); continue;
      }
      if (list && /^\s+/.test(line) && list.lastElementChild) {
        list.lastElementChild.append(doc.createTextNode('\n')); inline(list.lastElementChild, line.trim(), doc); continue;
      }
      list = null;
      if (!paragraph) { paragraph = doc.createElement('p'); section.append(paragraph); }
      else paragraph.append(doc.createTextNode('\n'));
      inline(paragraph, line, doc);
    }
  }

  function render(container, details, actions = {}) {
    const doc = container.ownerDocument;
    const clarification = details.status === 'input_required';
    container.classList.toggle('clarification', clarification);
    const header = doc.createElement('header'); header.className = 'run-diagnostic-header';
    const heading = doc.createElement('div');
    const badge = doc.createElement('span'); badge.className = 'run-diagnostic-badge';
    badge.textContent = clarification ? 'Action needed' : 'Run stopped';
    const title = doc.createElement('h3'); title.textContent = details.title;
    heading.append(badge, title);
    if (details.agent) {
      const source = doc.createElement('p'); source.className = 'run-diagnostic-source';
      source.textContent = `Reported by ${details.agent.replace(/^software-delivery-/, '').replace(/_/g, ' ')}`;
      heading.append(source);
    }
    const controls = doc.createElement('div'); controls.className = 'run-diagnostic-actions';
    if (actions.edit) {
      const edit = doc.createElement('button'); edit.type = 'button'; edit.className = 'secondary';
      edit.textContent = clarification ? 'Clarify request' : 'Edit request'; edit.addEventListener('click', actions.edit); controls.append(edit);
    }
    if (actions.copy) {
      const copy = doc.createElement('button'); copy.type = 'button'; copy.className = 'secondary'; copy.textContent = 'Copy full message';
      copy.addEventListener('click', async () => {
        try { await actions.copy(details.technicalDetails || details.message); copy.textContent = 'Copied'; }
        catch { copy.textContent = 'Select the original message to copy'; original.open = true; }
      });
      controls.append(copy);
    }
    header.append(heading, controls);
    const body = doc.createElement('div'); body.className = 'run-diagnostic-body';
    renderMessage(body, details.message);
    const original = doc.createElement('details'); original.className = 'run-diagnostic-original';
    const summary = doc.createElement('summary'); summary.textContent = details.technicalDetails ? 'Technical details' : 'Original message';
    const raw = doc.createElement('pre'); raw.textContent = details.technicalDetails || details.message; original.append(summary, raw);
    const children = [header];
    if (clarification) {
      const next = doc.createElement('p'); next.className = 'run-diagnostic-next';
      next.textContent = 'Resolve the blockers and answer the questions below, then update your request and start a new run.';
      children.push(next);
    }
    children.push(body);
    if (details.summary && details.summary !== details.message) {
      const context = doc.createElement('p'); context.className = 'run-diagnostic-context';
      context.textContent = details.summary; children.push(context);
    }
    children.push(original);
    container.replaceChildren(...children);
    container.hidden = false;
  }

  const api = { fromSnapshot, renderMessage, render };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RunDiagnostics = api;
})(typeof window !== 'undefined' ? window : globalThis);
