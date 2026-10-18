const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const documentEvents = new Map(), windowEvents = new Map();
let activeDialog = null;
const document = {
    activeElement: null,
    addEventListener: (name, fn) => documentEvents.set(name, fn),
    querySelector: selector => selector === '.editor-dialog' ? activeDialog : null
};
const window = { innerWidth: 1200, innerHeight: 900, addEventListener: (name, fn) => windowEvents.set(name, fn) };
function element(kind, parent) {
    return {
        kind, parent, style: {}, dataset: {}, isConnected: true, disabled: false,
        focus() { document.activeElement = this; },
        setAttribute(name, value) { this[name] = value; },
        setPointerCapture() {},
        getClientRects: () => [1],
        matches(selector) {
            return selector === `[data-dialog-${kind}]` || (kind === 'maximize' && selector.includes('button'));
        },
        closest(selector) { return this.matches(selector) ? this : this.parent?.closest(selector); }
    };
}
function dialog(movable) {
    const box = element('dialog'); box.dataset = { movable: String(movable), busy: 'false' };
    box.contains = item => item === box || item?.parent === box;
    box.header = element('drag', box); box.handle = element('resize', box); box.maximize = element('maximize', box);
    box.querySelector = selector => selector === '[data-dialog-maximize]' ? box.maximize : null;
    box.querySelectorAll = () => [box.header, box.maximize, box.handle];
    return box;
}
vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../DataPieMaui/wwwroot/table-editor.js'), 'utf8'), { window, document });
const sync = () => window.tableEditorModal.sync();
const event = (target, values = {}) => ({ target, button: 0, pointerId: 1, clientX: 100, clientY: 100, preventDefault() {}, ...values });
const send = (name, target, values) => documentEvents.get(name)(event(target, values));
const rect = () => Object.fromEntries(['left', 'top', 'width', 'height'].map(k => [k, Number.parseFloat(activeDialog.style[k])]));
function bounded() {
    const r = rect();
    assert.ok(r.left >= 0 && r.top >= 0 && r.left + r.width <= window.innerWidth && r.top + r.height <= window.innerHeight);
}
activeDialog = dialog(true); sync(); bounded();
const initial = rect();
send('pointerdown', activeDialog.header);
send('pointermove', activeDialog.header, { clientX: 150, clientY: 140 });
send('pointerup', activeDialog.header);
assert.equal(rect().left, initial.left + 50); assert.equal(rect().top, initial.top + 40);
send('pointerdown', activeDialog.header);
send('pointermove', activeDialog.header, { clientX: -5000, clientY: -5000 });
send('pointerup', activeDialog.header); bounded();
const beforeResize = rect();
send('pointerdown', activeDialog.handle);
send('pointermove', activeDialog.handle, { clientX: 180, clientY: 170 });
send('pointerup', activeDialog.handle);
assert.equal(rect().left, beforeResize.left); assert.equal(rect().width, beforeResize.width + 80);
send('pointerdown', activeDialog.handle);
send('pointermove', activeDialog.handle, { clientX: -5000, clientY: -5000 });
send('pointerup', activeDialog.handle);
assert.equal(rect().width, 400); assert.equal(rect().height, 280); bounded();
const normal = rect();
send('click', activeDialog.maximize); assert.equal(activeDialog.dataset.maximized, 'true'); bounded();
send('click', activeDialog.maximize); assert.deepEqual(rect(), normal);
activeDialog.dataset.busy = 'true'; sync();
send('click', activeDialog.maximize); send('pointerdown', activeDialog.header);
send('pointermove', activeDialog.header, { clientX: 400 }); assert.deepEqual(rect(), normal);
activeDialog.dataset.busy = 'false';
send('keydown', activeDialog.header, { key: 'ArrowRight', shiftKey: false });
assert.equal(rect().left, normal.left + 10);
const remembered = rect(); activeDialog = null; sync(); activeDialog = dialog(true); sync(); assert.deepEqual(rect(), remembered);
window.innerWidth = 300; window.innerHeight = 240; windowEvents.get('resize')(); bounded();
activeDialog = null; sync(); activeDialog = dialog(false); sync();
assert.deepEqual(activeDialog.style, {}); send('click', activeDialog.maximize); assert.deepEqual(activeDialog.style, {});
console.log('PASS dialog dragging, resizing, bounds, maximize/restore, busy guards, keyboard controls, remembered size and fixed delete confirmation');
