// Keep hidden controls enabled so Simple Run defaults are included in FormData.
(() => {
  const choice = document.querySelector('#run-experience');
  if (!choice) return;
  const advanced = element => element.setAttribute('data-advanced-only', '');
  document.querySelectorAll([
    '#autonomy-settings', '#workflow-retry-settings', '#build-verification-settings',
    '.llm-pricing-settings', '.jira-additional-actions',
    '#task-complexity', 'label[for="task-complexity"]', '#task-complexity-label',
    '#complexity-stops', '#task-complexity-description', '#agent-turn-limits .autonomy-scale',
  ].join(', ')).forEach(advanced);
  ['includeResolvedDiscussions', 'commentIssuesOnSourceCode'].forEach(name =>
    advanced(document.querySelector(`[name="${name}"]`).closest('label')));
  ['feedback', 'combined'].forEach(mode =>
    advanced(document.querySelector(`[name="mrReviewMode"][value="${mode}"]`).closest('label')));
  ['openai', 'claude'].forEach(provider => advanced(providerKeys[provider].parentElement));
  advanced(document.querySelector('#jira-include-comments').closest('label'));
  agentModels.querySelectorAll('.agent-model-card').forEach(card => {
    card.querySelectorAll('[data-field="tools"], [data-field="max-agent-turns"], [data-field="prompt"]')
      .forEach(advanced);
    // The turn/guidance labels and turn help text are siblings of their inputs.
    const turns = card.querySelector('[data-field="max-agent-turns"]');
    advanced(turns.previousElementSibling);
    advanced(turns.nextElementSibling);
    advanced(card.querySelector('[data-field="prompt"]').previousElementSibling);
  });

  function applyExperience() {
    const simple = document.querySelector('[name="runExperience"]:checked').value === 'simple';
    document.documentElement.dataset.runExperience = simple ? 'simple' : 'advanced';
    document.querySelector('[name="includeCodingRules"]').checked = !simple;
    if (simple) {
      document.querySelector('[name="mrReviewMode"][value="independent"]').checked = true;
      document.querySelector('#mr-review-mode').dispatchEvent(new Event('change'));
      [
        ['includeResolvedDiscussions', false], ['commentIssuesOnSourceCode', false],
        ['releaseManagerValidationEnabled', false], ['jiraValidationEnabled', true],
        ['autoCommit', false], ['autoPush', false], ['autoCreatePullRequest', false],
        ['buildProject', true], ['retryOnWorkflowAgent', false],
        ['includeCodingRules', false],
      ].forEach(([name, checked]) => { document.querySelector(`[name="${name}"]`).checked = checked; });
      const codeGraph = document.querySelector('[name="codeGraphEnabled"]');
      codeGraph.checked = !codeGraph.disabled;
      const autonomy = document.querySelector('#autonomy-level');
      autonomy.value = '100';
      autonomy.dispatchEvent(new Event('input'));
      const complexity = document.querySelector('#task-complexity');
      complexity.value = '1';
      complexity.dispatchEvent(new Event('input'));
      agentModels.querySelectorAll('.agent-model-card').forEach(card => {
        const turns = card.querySelector('[data-field="max-agent-turns"]');
        turns.value = card.dataset.agent === 'developer' ? '120' : '60';
        turns.dispatchEvent(new Event('input'));
        card.querySelector('[data-field="prompt"]').value = '';
      });
    }
    // Only offer providers whose credentials can be entered in the current mode.
    agentModels.querySelectorAll('.agent-model-card').forEach(card => {
      const provider = card.querySelector('[data-field="provider"]');
      [...provider.options].forEach(option => {
        option.hidden = option.disabled = simple && !['aifa', 'deepseek'].includes(option.value);
      });
      if (simple && !['aifa', 'deepseek'].includes(provider.value)) {
        provider.value = 'deepseek';
        syncAgentCard(card);
      }
    });
    syncRequiredProviderKeys();
    if (currentSetupStep === 'confirm') renderRunReview();
  }
  choice.addEventListener('change', applyExperience);
  applyExperience();
})();
