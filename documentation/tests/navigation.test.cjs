const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../help/assets/site.js'), 'utf8');

function setup(isMobile) {
    const nodes = new Map();
    function element(id) {
        const listeners = {}, classes = new Set();
        return { id, open: false, hidden: true, children: [],
            classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value) },
            addEventListener(type, handler) { listeners[type] = handler; },
            emit(type, event) { listeners[type]?.(event); },
            setAttribute(name, value) { this[name] = value; },
            appendChild(child) {
                if (child.parent) child.parent.children = child.parent.children.filter(node => node !== child);
                this.children.push(child); child.parent = this;
            },
            showModal() { this.open = true; },
            close() { this.open = false; this.emit('close'); }
        };
    }
    for (const id of ['guide-navigation', 'mobile-menu', 'menu-toggle', 'menu-close', 'docs-search', 'github-link', 'mobile-navigation', 'desktop-navigation', 'mobile-search', 'desktop-search', 'mobile-repository', 'desktop-repository']) nodes.set(id, element(id));
    const document = Object.assign(element('document'), {
        body: element('body'), documentElement: element('html'),
        getElementById: id => nodes.get(id), querySelectorAll: () => []
    });
    const media = Object.assign(element('media'), { matches: isMobile });
    vm.runInNewContext(source, { document, window: { matchMedia: () => media } });
    return { nodes, document, resize(mobile) { media.matches = mobile; media.emit('change'); } };
}

test('mobile menu owns the only search and navigation instances', () => {
    const { nodes, document } = setup(true);
    assert.equal(nodes.get('docs-search').parent.id, 'mobile-search');
    assert.equal(nodes.get('guide-navigation').parent.id, 'mobile-navigation');
    assert.equal(nodes.get('github-link').parent.id, 'mobile-repository');
    nodes.get('menu-toggle').emit('click');
    assert.equal(nodes.get('mobile-menu').open, true);
    assert.equal(nodes.get('menu-toggle')['aria-expanded'], 'true');
    assert.equal(document.body.classList.contains('menu-open'), true);
    nodes.get('menu-close').emit('click');
    assert.equal(nodes.get('mobile-menu').open, false);
    assert.equal(document.body.classList.contains('menu-open'), false);
});
test('search shortcut opens mobile menu and resizing restores desktop controls', () => {
    const app = setup(true);
    app.document.emit('docs:open-search');
    assert.equal(app.nodes.get('mobile-menu').open, true);
    app.resize(false);
    assert.equal(app.nodes.get('mobile-menu').open, false);
    assert.equal(app.nodes.get('docs-search').parent.id, 'desktop-search');
    assert.equal(app.nodes.get('guide-navigation').parent.id, 'desktop-navigation');
    assert.equal(app.nodes.get('github-link').parent.id, 'desktop-repository');
    app.document.emit('docs:open-search');
    assert.equal(app.nodes.get('mobile-menu').open, false);
});
