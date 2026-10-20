const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../help/assets/theme.js'), 'utf8');

function setup({ saved = null, systemDark = false, blockedStorage = false } = {}) {
    const root = { dataset: {}, classList: { toggle() {} } };
    const handlers = {}, systemHandlers = {}, windowHandlers = {}, buttonHandlers = {};
    const storage = new Map(saved ? [['docs-theme', saved]] : []);
    const button = { hidden: true, setAttribute(name, value) { this[name] = value; }, addEventListener(name, handler) { buttonHandlers[name] = handler; } };
    const media = { matches: systemDark, addEventListener(name, handler) { systemHandlers[name] = handler; } };
    const color = {};
    const document = {
        documentElement: root,
        querySelector: () => color,
        querySelectorAll: () => [button],
        addEventListener(name, handler) { handlers[name] = handler; }
    };
    vm.runInNewContext(source, {
        document,
        window: { matchMedia: () => media, addEventListener(name, handler) { windowHandlers[name] = handler; } },
        localStorage: {
            getItem(key) { if (blockedStorage) throw Error('blocked'); return storage.get(key) || null; },
            setItem(key, value) { if (blockedStorage) throw Error('blocked'); storage.set(key, value); }
        }
    });
    handlers.DOMContentLoaded();
    return { root, button, storage, color, toggle: buttonHandlers.click,
        system(dark) { media.matches = dark; systemHandlers.change(); },
        otherTab(value) { windowHandlers.storage({ key: 'docs-theme', newValue: value }); }
    };
}

test('theme follows system preference until explicitly changed', () => {
    const app = setup({ systemDark: true });
    assert.equal(app.root.dataset.theme, 'dark');
    assert.equal(app.button['aria-label'], 'Switch to light mode');
    app.system(false);
    assert.equal(app.root.dataset.theme, 'light');
    assert.equal(app.color.content, '#f2f2ef');
});
test('saved preference overrides system and the switch persists its new value', () => {
    const app = setup({ saved: 'light', systemDark: true });
    assert.equal(app.root.dataset.theme, 'light');
    app.toggle();
    assert.equal(app.root.dataset.theme, 'dark');
    assert.equal(app.storage.get('docs-theme'), 'dark');
    app.system(false);
    assert.equal(app.root.dataset.theme, 'dark');
});
test('the switch still works when storage is blocked', () => {
    const app = setup({ blockedStorage: true });
    assert.equal(app.button.hidden, false);
    app.toggle();
    assert.equal(app.root.dataset.theme, 'dark');
});
test('theme changes in another tab are applied', () => {
    const app = setup();
    app.otherTab('dark');
    assert.equal(app.root.dataset.theme, 'dark');
    app.otherTab(null);
    assert.equal(app.root.dataset.theme, 'light');
});
