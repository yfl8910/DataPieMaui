// Keep keyboard focus in the modal and return it to its opener on close.
window.tableEditorModal = (() => {
    let current = null;
    let opener = null;
    function focusable(dialog) {
        return [...dialog.querySelectorAll('button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex="0"]')];
    }
    function keydown(event) {
        if (!current) return;
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
                // Focus the dialog heading area, never the destructive action.
                current.focus();
            } else if (!dialog && current) {
                current = null;
                if (opener?.isConnected) opener.focus();
                opener = null;
            } else if (dialog && !dialog.contains(document.activeElement)) {
                dialog.focus();
            }
        }
    };
})();
