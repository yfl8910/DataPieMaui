// Keep keyboard focus in the modal and return it to its opener on close.
window.tableEditorModal = (() => {
    let current = null;
    let opener = null;
    let normalBounds = null;
    let maximized = false;
    let gesture = null;
    const movable = () => current?.dataset.movable === 'true';
    const interactive = () => movable() && current.dataset.busy !== 'true';
    function viewport() {
        const margin = Math.min(12, window.innerWidth / 8, window.innerHeight / 8);
        return { margin, width: Math.max(1, window.innerWidth - margin * 2), height: Math.max(1, window.innerHeight - margin * 2) };
    }
    function clamp(bounds) {
        const area = viewport();
        const width = Math.min(area.width, Math.max(Math.min(400, area.width), bounds.width));
        const height = Math.min(area.height, Math.max(Math.min(280, area.height), bounds.height));
        return { width, height, left: Math.max(area.margin, Math.min(bounds.left, window.innerWidth - area.margin - width)), top: Math.max(area.margin, Math.min(bounds.top, window.innerHeight - area.margin - height)) };
    }
    function applyBounds() {
        if (!movable()) return;
        const area = viewport();
        if (!normalBounds) {
            const width = Math.min(760, area.width), height = Math.min(640, area.height);
            normalBounds = { width, height, left: (window.innerWidth - width) / 2, top: (window.innerHeight - height) / 2 };
        }
        normalBounds = clamp(normalBounds);
        const bounds = maximized ? { width: area.width, height: area.height, left: area.margin, top: area.margin } : normalBounds;
        for (const name of ['left', 'top', 'width', 'height']) current.style[name] = `${bounds[name]}px`;
        current.dataset.maximized = String(maximized);
        const button = current.querySelector('[data-dialog-maximize]');
        if (button) {
            button.textContent = maximized ? '❐' : '□';
            button.setAttribute('aria-label', maximized ? 'Restore dialog' : 'Maximize dialog');
            button.title = maximized ? 'Restore dialog' : 'Maximize dialog';
            button.setAttribute('aria-pressed', String(maximized));
        }
    }
    function toggleMaximize() {
        if (!interactive()) return;
        gesture = null; maximized = !maximized; applyBounds();
    }
    document.addEventListener('click', event => {
        if (current?.contains(event.target) && event.target.closest('[data-dialog-maximize]')) toggleMaximize();
    });
    document.addEventListener('pointerdown', event => {
        if (!interactive() || maximized || event.button !== 0 || !current.contains(event.target)) return;
        const resize = event.target.closest('[data-dialog-resize]');
        const header = event.target.closest('[data-dialog-drag]');
        if (!resize && (!header || event.target.closest('button,input,textarea,select,a'))) return;
        const handle = resize || header;
        gesture = { id: event.pointerId, x: event.clientX, y: event.clientY, resize: !!resize, bounds: { ...normalBounds } };
        handle.setPointerCapture(event.pointerId);
        event.preventDefault();
    });
    document.addEventListener('pointermove', event => {
        if (!gesture || gesture.id !== event.pointerId || !interactive()) return;
        const dx = event.clientX - gesture.x, dy = event.clientY - gesture.y;
        const bounds = gesture.bounds;
        normalBounds = clamp(gesture.resize
            ? { ...bounds, width: bounds.width + dx, height: bounds.height + dy }
            : { ...bounds, left: bounds.left + dx, top: bounds.top + dy });
        if (gesture.resize) {
            // Resizing never moves the opposite corner, even at a viewport edge.
            const area = viewport();
            normalBounds.left = bounds.left; normalBounds.top = bounds.top;
            normalBounds.width = Math.min(normalBounds.width, window.innerWidth - area.margin - bounds.left);
            normalBounds.height = Math.min(normalBounds.height, window.innerHeight - area.margin - bounds.top);
        }
        applyBounds(); event.preventDefault();
    });
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture'])
        document.addEventListener(name, event => { if (gesture?.id === event.pointerId) gesture = null; });
    window.addEventListener('resize', () => { gesture = null; applyBounds(); });
    function focusable(dialog) {
        return [...dialog.querySelectorAll('button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex="0"]')].filter(element => element.getClientRects().length > 0);
    }
    function keydown(event) {
        if (!current) return;
        if (interactive() && !maximized && event.key.startsWith('Arrow') && (event.target.matches('[data-dialog-drag]') || event.target.matches('[data-dialog-resize]'))) {
            const step = event.shiftKey ? 40 : 10;
            const dx = event.key === 'ArrowRight' ? step : event.key === 'ArrowLeft' ? -step : 0;
            const dy = event.key === 'ArrowDown' ? step : event.key === 'ArrowUp' ? -step : 0;
            const resize = event.target.matches('[data-dialog-resize]');
            normalBounds = clamp(resize ? { ...normalBounds, width: normalBounds.width + dx, height: normalBounds.height + dy }
                : { ...normalBounds, left: normalBounds.left + dx, top: normalBounds.top + dy });
            applyBounds(); event.preventDefault(); return;
        }
        if (event.key === 'Escape') {
            event.preventDefault();
            current.querySelector('[data-dialog-close]:not(:disabled)')?.click();
        } else if (event.key === 'Tab') {
            const items = focusable(current);
            const first = items[0], last = items[items.length - 1];
            if (!first) { event.preventDefault(); current.focus(); return; }
            if (event.shiftKey && (document.activeElement === first || document.activeElement === current)) {
                event.preventDefault(); last.focus();
            } else if (!event.shiftKey && (document.activeElement === last || document.activeElement === current)) {
                event.preventDefault(); first.focus();
            }
        }
    }
    document.addEventListener('keydown', keydown);
    return {
        sync() {
            const selectAll = document.querySelector('[data-page-select-all]');
            if (selectAll) selectAll.indeterminate = selectAll.dataset.partial === 'true';
            const dialog = document.querySelector('.editor-dialog');
            if (dialog && dialog !== current) {
                opener = document.activeElement;
                current = dialog;
                gesture = null;
                applyBounds();
                // Focus the dialog heading area, never the destructive action.
                current.focus();
            } else if (!dialog && current) {
                gesture = null;
                current = null;
                if (opener?.isConnected) opener.focus();
                opener = null;
            } else if (dialog && !dialog.contains(document.activeElement)) {
                dialog.focus();
            }
            if (dialog) { if (dialog.dataset.busy === 'true') gesture = null; applyBounds(); }
        }
    };
})();
