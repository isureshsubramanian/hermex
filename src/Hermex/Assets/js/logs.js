/* Hermex dashboard — Logs page. */
(function () {
    'use strict';
    var M = window.Hermex;

    var LEVEL_KEYS = ['debug', 'info', 'warning', 'error'];
    var LEVEL_NAMES = ['Debug', 'Info', 'Warning', 'Error'];
    var MAX_ENTRIES = 500;

    var state = { minLevel: 0, entries: [] };
    var el = {};

    function cache() {
        el.body = document.getElementById('m4d-log-body');
        el.empty = document.getElementById('m4d-log-empty');
        el.level = document.getElementById('m4d-log-level');
        el.refresh = document.getElementById('m4d-log-refresh');
        el.clear = document.getElementById('m4d-log-clear');
    }

    async function loadLogs() {
        try {
            var logs = await M.api('/logs?limit=' + MAX_ENTRIES);
            state.entries = logs || [];
            render();
        } catch (e) {
            M.toast('Could not load logs.', 'error');
        }
    }

    function render() {
        var filtered = state.entries.filter(function (l) {
            return (l.level || 0) >= state.minLevel;
        });
        el.empty.hidden = filtered.length > 0;
        el.body.innerHTML = filtered.map(rowHtml).join('');
    }

    function rowHtml(l) {
        var level = Number(l.level) || 0;
        var key = LEVEL_KEYS[level] || 'info';
        var name = LEVEL_NAMES[level] || 'Info';
        return '<tr>' +
            '<td class="m4d-mono">' + M.escapeHtml(M.formatDate(l.timestampUtc)) + '</td>' +
            '<td><span class="m4d-level m4d-level--' + key + '">' + name + '</span></td>' +
            '<td>' + M.escapeHtml(l.category || '') + '</td>' +
            '<td>' + M.escapeHtml(l.message || '') + '</td>' +
            '<td class="m4d-mono">' + M.escapeHtml(l.sessionId || '—') + '</td>' +
            '</tr>';
    }

    async function clearLogs() {
        if (!window.confirm('Delete every diagnostic log entry?')) return;
        try {
            await M.api('/logs', { method: 'DELETE' });
            state.entries = [];
            render();
            M.toast('Logs cleared.');
        } catch (e) {
            M.toast('Could not clear the logs.', 'error');
        }
    }

    M.ready(function () {
        cache();

        el.level.addEventListener('change', function () {
            state.minLevel = Number(el.level.value) || 0;
            render();
        });
        el.refresh.addEventListener('click', loadLogs);
        el.clear.addEventListener('click', clearLogs);

        M.onHub('logAdded', function (entry) {
            if (!entry) return;
            state.entries.unshift(entry);
            if (state.entries.length > MAX_ENTRIES) state.entries.pop();
            render();
        });
        M.onHub('poll', loadLogs);

        loadLogs();
    });
})();
