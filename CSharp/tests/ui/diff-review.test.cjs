const assert = require('assert').strict;
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Xbank.DevAgent/Xbank.DevAgent.Api/wwwroot/app.js'), 'utf8');
const start = source.indexOf('const DIFF_VIEW_STORAGE_KEY');
const end = source.indexOf('\nfunction setStatus(', start);
assert.ok(start >= 0 && end > start, 'Diff review functions must exist in app.js.');

function classList() {
  const set = new Set();
  return {
    toggle(name, force) { if (force) set.add(name); else set.delete(name); },
    add(name) { set.add(name); },
    remove(name) { set.delete(name); },
    contains(name) { return set.has(name); },
  };
}

function element() {
  return {
    innerHTML: '', textContent: '', hidden: false,
    classList: classList(),
    attributes: {},
    listeners: {},
    addEventListener(name, handler) { this.listeners[name] = handler; },
    setAttribute(name, value) { this.attributes[name] = String(value); },
    replaceChildren() {},
  };
}

const storage = { items: new Map(), getItem(key) { return this.items.has(key) ? this.items.get(key) : null; }, setItem(key, value) { this.items.set(key, String(value)); } };

const context = {
  localStorage: storage,
  escapeHtml: value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'),
  escapeAttribute: value => String(value ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;'),
  diffToolbar: element(),
  diffBody: element(),
  diffEmpty: element(),
  diffTree: element(),
  diffFiles: element(),
  diffFileCount: element(),
  diffLineStats: element(),
  diffSummary: element(),
  diffCollapseAll: element(),
  diffViewInline: element(),
  diffViewSplit: element(),
};
vm.createContext(context);
vm.runInContext(source.slice(start, end), context);

const sample = [
  'diff --git a/src/app.js b/src/app.js',
  'index 1111111..2222222 100644',
  '--- a/src/app.js',
  '+++ b/src/app.js',
  '@@ -10,3 +10,4 @@ function foo() {',
  ' const a = 1;',
  '-const b = 2;',
  '+const b = 3;',
  '+const c = 4;',
  ' context;',
  '@@ -30,2 +31,2 @@ function bar() {',
  '-old',
  '+new',
  'diff --git a/src/new-file.js b/src/new-file.js',
  'new file mode 100644',
  'index 0000000..3333333',
  '--- /dev/null',
  '+++ b/src/new-file.js',
  '@@ -0,0 +1,2 @@',
  '+line one',
  '+line two',
  'diff --git a/docs/old.md b/docs/old.md',
  'deleted file mode 100644',
  'index 4444444..0000000',
  '--- a/docs/old.md',
  '+++ /dev/null',
  '@@ -1,2 +0,0 @@',
  '-# Title',
  '-body',
  'diff --git a/lib/moved.js b/lib/moved-renamed.js',
  'similarity index 100%',
  'rename from lib/moved.js',
  'rename to lib/moved-renamed.js',
  'diff --git a/assets/logo.png b/assets/logo.png',
  'index 5555555..6666666 100644',
  'Binary files a/assets/logo.png and b/assets/logo.png differ',
  '',
].join('\n');

const files = context.parseUnifiedDiff(sample);
assert.equal(files.length, 5);

const [app] = files;
assert.equal(app.path, 'src/app.js');
assert.equal(app.status, 'modified');
assert.equal(app.additions, 3);
assert.equal(app.deletions, 2);
assert.equal(app.hunks.length, 2);
assert.equal(app.hunks[0].lines[1].type, 'del');
assert.equal(app.hunks[0].lines[1].oldNumber, 11);
assert.equal(app.hunks[0].lines[2].type, 'add');
assert.equal(app.hunks[0].lines[2].newNumber, 11);
assert.equal(app.hunks[0].lines[3].newNumber, 12);
assert.equal(app.hunks[1].lines[0].oldNumber, 30);
assert.equal(app.hunks[1].lines[0].newNumber, null);
assert.equal(app.hunks[1].lines[1].newNumber, 31);

assert.equal(files[1].path, 'src/new-file.js');
assert.equal(files[1].status, 'added');
assert.equal(files[1].additions, 2);
assert.equal(files[2].path, 'docs/old.md');
assert.equal(files[2].status, 'deleted');
assert.equal(files[2].deletions, 2);
assert.equal(files[3].status, 'renamed');
assert.equal(files[3].path, 'lib/moved-renamed.js');
assert.equal(files[3].oldPath, 'lib/moved.js');
assert.equal(files[4].binary, true);
assert.equal(files[4].hunks.length, 0);

// Reproduce the backend output from existing sessions: added files had no @@ header.
const legacyAdded = [
  'diff --git a/Added.cs b/Added.cs', 'new file mode 100644',
  '--- /dev/null', '+++ b/Added.cs', '+class Added {', '+}', '',
].join('\n');
const legacyFile = context.parseUnifiedDiff(legacyAdded)[0];
assert.equal(legacyFile.status, 'added');
assert.equal(legacyFile.additions, 2);
assert.equal(legacyFile.hunks[0].lines[0].newNumber, 1);
assert.equal(legacyFile.hunks[0].lines[1].newNumber, 2);
assert.match(context.renderInlineDiff(legacyFile), /class Added/);
assert.match(context.renderSplitDiff(legacyFile), /class Added/);
const legacyPlus = context.parseUnifiedDiff(legacyAdded.replace('+class Added {', '+++ content'))[0];
assert.equal(legacyPlus.hunks[0].lines[0].text, '++ content');
assert.equal(legacyPlus.path, 'Added.cs');
assert.match(context.renderDiffFileSection(files[3], 3, 'inline'), /No text changes/);
assert.match(context.renderDiffFileSection(files[4], 4, 'split'), /Binary file/);
assert.match(context.renderDiffFileSection(legacyFile, 0, 'inline'),
  /<button[^>]+data-diff-toggle="0"[^>]*>[\s\S]*diff-file-status[\s\S]*diff-file-stats[\s\S]*<\/button>/);

const tree = context.buildDiffTree(files);
assert.deepEqual([...tree.children].map(child => child.name), ['assets', 'docs', 'lib', 'src']);
assert.deepEqual([...tree.children[3].children].map(child => child.name), ['app.js', 'new-file.js']);

const compressed = context.buildDiffTree([{ path: 'src/pkg/deep/file.js', additions: 0, deletions: 0, status: 'modified', binary: false, hunks: [] }]);
assert.equal(compressed.children.length, 1);
assert.equal(compressed.children[0].name, 'src/pkg/deep');
assert.equal(compressed.children[0].children[0].name, 'file.js');

const inline = context.renderInlineDiff(app);
assert.match(inline, /diff-line diff-line-del/);
assert.match(inline, /diff-line diff-line-add/);
assert.match(inline, /diff-gutter-old">11</);
assert.match(inline, /diff-line-marker">\+/);

const rows = context.buildSplitRows(app.hunks[0].lines);
assert.equal(rows.length, 4);
assert.equal(rows[1].left.type, 'del');
assert.equal(rows[1].right.type, 'add');
assert.equal(rows[2].left, null);
assert.equal(rows[2].right.type, 'add');

const split = context.renderSplitDiff(app);
assert.match(split, /diff-split-side del/);
assert.match(split, /diff-split-side add/);
assert.match(split, /diff-split-side empty/);

const escaping = context.parseUnifiedDiff('diff --git a/x.js b/x.js\n--- a/x.js\n+++ b/x.js\n@@ -1 +1 @@\n-<script>\n+</script>\n');
assert.match(context.renderInlineDiff(escaping[0]), /&lt;script&gt;/);
assert.doesNotMatch(context.renderInlineDiff(escaping[0]), /<script>/);

context.renderDiff(sample);
assert.equal(context.diffToolbar.hidden, false);
assert.equal(context.diffBody.hidden, false);
assert.equal(context.diffEmpty.hidden, true);
assert.equal(context.diffFileCount.textContent, '5 files changed');
assert.match(context.diffLineStats.innerHTML, /\+5/);
assert.match(context.diffLineStats.innerHTML, /−4/);
assert.match(context.diffSummary.textContent, /\+5 additions · −4 deletions/);
assert.match(context.diffTree.innerHTML, /diff-tree-folder/);
assert.match(context.diffFiles.innerHTML, /diff-file-path">src\/app\.js/);
assert.equal(context.diffViewInline.classList.contains('active'), true);

context.setDiffViewMode('split');
assert.equal(context.diffViewSplit.classList.contains('active'), true);
assert.equal(context.diffViewInline.classList.contains('active'), false);
assert.match(context.diffFiles.innerHTML, /diff-split-row/);
assert.equal(storage.getItem('maf.diffView'), 'split');

// The status endpoint is polled every second, so re-rendering an unchanged diff must be a no-op
// that keeps the reviewer's collapse state instead of rebuilding the panel from scratch.
const isCollapsed = index => vm.runInContext(`diffModel.expanded.get(${index}) === false`, context);
vm.runInContext('diffModel.expanded.set(0, false)', context);
const treeHtml = context.diffTree.innerHTML;
const filesHtml = context.diffFiles.innerHTML;
context.renderDiff(sample);
assert.equal(context.diffTree.innerHTML, treeHtml);
assert.equal(context.diffFiles.innerHTML, filesHtml);
assert.equal(isCollapsed(0), true);

// Changing the layout re-renders the file list but keeps the scroll position and collapse state.
context.diffFiles.scrollTop = 42;
context.setDiffViewMode('inline');
assert.equal(context.diffFiles.scrollTop, 42);
assert.match(context.diffFiles.innerHTML, /diff-file collapsed/);

// A genuinely changed diff rebuilds the panel and starts with every file expanded.
context.renderDiff(sample.replace('+const b = 3;', '+const b = 4;'));
assert.equal(isCollapsed(0), false);
assert.doesNotMatch(context.diffFiles.innerHTML, /diff-file collapsed/);

context.renderDiff('');
assert.equal(context.diffToolbar.hidden, true);
assert.equal(context.diffBody.hidden, true);
assert.equal(context.diffEmpty.hidden, false);
assert.equal(context.diffEmpty.textContent, 'No diff available.');

// Resetting the run must clear the memoised text, so the next run renders even with identical content.
context.renderDiff(sample);
assert.equal(context.diffToolbar.hidden, false);
assert.equal(context.diffEmpty.hidden, true);

// Exercise the actual delegated click handlers, including the Added badge and tree navigation.
const controlsStart = source.indexOf("diffViewInline.addEventListener('click'");
const controlsEnd = source.indexOf("approvalForm.addEventListener", controlsStart);
vm.runInContext(source.slice(controlsStart, controlsEnd), context);
const section = element();
const toggle = element();
toggle.dataset = { diffToggle: '1' };
section.querySelector = () => toggle;
section.scrollIntoView = options => { section.scrolled = options.block; };
context.diffFiles.querySelector = () => section;
const clickAdded = () => context.diffFiles.listeners.click({ target: { closest: () => toggle } });
clickAdded();
assert.equal(isCollapsed(1), true);
assert.equal(section.classList.contains('collapsed'), true);
assert.equal(toggle.attributes['aria-expanded'], 'false');
context.renderDiff(sample); // Status polling must preserve the user's choice.
assert.equal(isCollapsed(1), true);
clickAdded();
assert.equal(isCollapsed(1), false);
assert.equal(section.classList.contains('collapsed'), false);
assert.equal(toggle.attributes['aria-expanded'], 'true');
context.diffCollapseAll.listeners.click();
assert.equal(context.diffCollapseAll.textContent, 'Expand all');
assert.equal(isCollapsed(1), true);
context.diffCollapseAll.listeners.click();
assert.equal(context.diffCollapseAll.textContent, 'Collapse all');
assert.equal(isCollapsed(1), false);
clickAdded();
const treeButton = element();
treeButton.dataset = { diffIndex: '1' };
context.document = { getElementById: () => section };
context.diffTree.querySelectorAll = () => [];
context.diffTree.listeners.click({ target: { closest: () => treeButton } });
assert.equal(isCollapsed(1), false);
assert.equal(toggle.attributes['aria-expanded'], 'true');
assert.equal(section.scrolled, 'start');
assert.equal(treeButton.classList.contains('active'), true);

console.log('diff-review.test.cjs passed');
