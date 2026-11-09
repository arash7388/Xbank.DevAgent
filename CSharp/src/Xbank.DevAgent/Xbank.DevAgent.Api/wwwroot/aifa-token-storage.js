(() => {
  const input = document.querySelector('#aifa-key');
  const remember = document.querySelector('#remember-aifa-token');
  const status = document.querySelector('#aifa-token-storage-status');
  const storageKey = 'maf-aifa-token-v1';
  const help = status.textContent;

  function saveToken() {
    try {
      const token = input.value.trim();
      if (remember.checked && token) localStorage.setItem(storageKey, token);
      else localStorage.removeItem(storageKey);
      status.textContent = help;
    } catch {
      status.textContent = 'Browser storage is unavailable. The saved token could not be updated; you can still enter a token for this run.';
    }
  }

  try {
    const token = localStorage.getItem(storageKey);
    if (token) {
      input.value = token;
      remember.checked = true;
      // Match entering a key manually, including selecting AIFA for the agents.
      input.dispatchEvent(new Event('input', { bubbles: true }));
    }
  } catch {
    status.textContent = 'Browser storage is unavailable. Enter your AIFA token for this run.';
  }

  remember.addEventListener('change', saveToken);
  input.addEventListener('input', () => {
    if (remember.checked) saveToken();
  });
})();
