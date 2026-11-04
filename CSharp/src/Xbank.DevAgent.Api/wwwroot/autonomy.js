(() => {
  const slider = document.querySelector('#autonomy-level');
  if (!slider) return;
  const levels = {
    0: ['Guided', 'Ask before choosing a major approach or making significant changes.'],
    25: ['Assisted', 'Make routine decisions; ask when requirements or a major approach are unclear.'],
    50: ['Collaborative', 'Resolve minor ambiguity; ask about decisions that significantly change scope or expected behavior.'],
    75: ['Highly autonomous', 'Choose an approach and record assumptions; ask only for essential decisions that cannot reasonably be inferred.'],
    100: ['Fully autonomous', 'Decide, implement, review, and repair without waiting for your answers. Record assumptions and finish with the patch or your selected completion actions.'],
  };
  function update() {
    const [name, description] = levels[slider.value] || levels[100];
    const label = `${slider.value}% — ${name}`;
    document.querySelector('#autonomy-label').textContent = label;
    document.querySelector('#autonomy-description').textContent = description;
    slider.setAttribute('aria-valuetext', label);
  }
  slider.addEventListener('input', update);
  update();

  const commit = document.querySelector('#auto-commit');
  const push = document.querySelector('#auto-push');
  const request = document.querySelector('#auto-create-pull-request');
  commit.addEventListener('change', () => {
    if (!commit.checked) push.checked = request.checked = false;
  });
  push.addEventListener('change', () => {
    if (push.checked) commit.checked = true;
    else request.checked = false;
  });
  request.addEventListener('change', () => {
    if (request.checked) commit.checked = push.checked = true;
  });
})();
