(function () {
    const KEY = 'rummihub.sidebar.collapsed';

    function applyStored() {
        const cb = document.getElementById('sidebar-collapse');
        if (!cb || cb.dataset.persistBound === '1') return;
        const stored = localStorage.getItem(KEY);
        if (stored === '1') cb.checked = true;
        cb.addEventListener('change', () => {
            localStorage.setItem(KEY, cb.checked ? '1' : '0');
        });
        cb.dataset.persistBound = '1';
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', applyStored);
    } else {
        applyStored();
    }

    document.addEventListener('enhancedload', applyStored);
})();
