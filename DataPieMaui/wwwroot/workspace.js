window.dataPie = {
    async copyText(text) { await navigator.clipboard.writeText(text); },
    showDialog(id) {
        const dialog = document.getElementById(id);
        if (!dialog || dialog.open) return;
        dialog.addEventListener('cancel', event => {
            event.preventDefault();
            (dialog.querySelector('[data-dismiss]') || dialog.querySelector('[data-accept]')).click();
        }, { once: true });
        dialog.showModal();
        (dialog.querySelector('[data-dismiss]') || dialog.querySelector('[data-accept]')).focus();
    }
};
