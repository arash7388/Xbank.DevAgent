const assert = require('assert').strict;

// Match only the session payload, independent of checkout line endings and indentation.
module.exports = function requestPayload(source) {
  const match = source.match(/const request = \{\r?\n\s+sourceControl: connection\.sourceControl,[\s\S]*?\r?\n\s*\};/);
  assert.ok(match, 'Could not locate the session request payload in app.js');
  return match[0].replace('const request =', 'globalThis.request =');
};
