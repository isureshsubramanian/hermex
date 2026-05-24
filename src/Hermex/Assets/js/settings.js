/* Hermex dashboard — Settings & status page. */
(function () {
    'use strict';
    var M = window.Hermex;

    /* ---------- helpers ---------- */
    function set(id, value) {
        var node = document.getElementById(id);
        if (node) node.textContent = (value == null || value === '') ? '—' : String(value);
    }
    function val(id, value) {
        var node = document.getElementById(id);
        if (node) node.value = value == null ? '' : String(value);
    }
    function check(id, value) {
        var node = document.getElementById(id);
        if (node) node.checked = !!value;
    }
    function readVal(id) { var n = document.getElementById(id); return n ? n.value : ''; }
    function readInt(id) { return parseInt(readVal(id), 10) || 0; }
    function readNum(id) { return parseFloat(readVal(id)) || 0; }
    function readBool(id) { var n = document.getElementById(id); return n ? n.checked : false; }

    function formatUptime(seconds) {
        seconds = Number(seconds) || 0;
        if (seconds < 60) return seconds + ' sec';
        var d = Math.floor(seconds / 86400);
        var h = Math.floor((seconds % 86400) / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        var parts = [];
        if (d) parts.push(d + 'd');
        if (h) parts.push(h + 'h');
        parts.push(m + 'm');
        return parts.join(' ');
    }

    /* ---------- live status ---------- */
    function applyStatus(status) {
        if (!status) return;
        var srv = status.server || {};
        var stats = status.stats || {};

        set('m4d-set-endpoint', (srv.listenAddress || '—') + '  :  ' + (srv.listeningPort || srv.configuredPort || '—'));
        set('m4d-set-imap', srv.imapEnabled
            ? (srv.listenAddress || '—') + '  :  ' + (srv.imapListeningPort || '—') + '  (' + (srv.imapStatus || '') + ')'
            : 'Disabled');
        set('m4d-set-host', srv.host);
        set('m4d-set-uptime', srv.startedAtUtc ? formatUptime(srv.uptimeSeconds) : 'Not running');
        set('m4d-set-active', srv.activeConnections);
        set('m4d-set-received', srv.messagesReceived);
        set('m4d-set-rejected', srv.messagesRejected);
        set('m4d-set-accepted', srv.acceptedConnections);

        set('m4d-set-total', (stats.totalMessages || 0).toLocaleString());
        set('m4d-set-unread', (stats.unreadMessages || 0).toLocaleString());
        set('m4d-set-storesize', M.formatBytes(stats.totalSizeBytes || 0));
        set('m4d-set-last', stats.lastReceivedUtc ? M.formatDate(stats.lastReceivedUtc) : 'Never');
        set('m4d-set-db', srv.databasePath);

        var pill = document.getElementById('m4d-settings-status');
        if (pill) {
            var kind = 'ok';
            var label = 'Listening';
            if (srv.status === 'PortConflict') { kind = 'error'; label = 'Port conflict'; }
            else if (srv.status === 'Faulted') { kind = 'error'; label = 'Faulted'; }
            else if (srv.status !== 'Listening') { kind = 'warn'; label = srv.status || 'Unknown'; }
            pill.className = 'm4d-status m4d-status--' + kind;
            var lbl = pill.querySelector('.m4d-status__label');
            if (lbl) lbl.textContent = label;
        }

        var error = document.getElementById('m4d-set-error');
        if (error) {
            if (srv.lastError) { error.hidden = false; error.textContent = srv.lastError; }
            else { error.hidden = true; }
        }
    }

    /* ---------- settings form ---------- */
    async function loadSettings() {
        try {
            var s = await M.api('/settings');
            val('set-smtpPort', s.smtpPort);
            val('set-imapPort', s.imapPort);
            check('set-enableImap', s.enableImap);
            val('set-serverHostName', s.serverHostName);
            val('set-maxMessageSizeMb', Math.round((s.maxMessageSizeBytes || 0) / 1048576));
            check('set-requireAuthentication', s.requireAuthentication);
            check('set-captureSessionTranscript', s.captureSessionTranscript);
            val('set-retentionMaxMessages', s.retentionMaxMessages);
            val('set-retentionMaxAgeHours', s.retentionMaxAgeHours);
            val('set-dashboardTitle', s.dashboardTitle);
            val('set-defaultTheme', s.defaultTheme || 'System');
            check('set-relayEnabled', s.relayEnabled);
            check('set-relayAutomatic', s.relayAutomatic);
            val('set-relayHost', s.relayHost);
            val('set-relayPort', s.relayPort);
        } catch (e) {
            M.toast('Could not load settings.', 'error');
        }
    }

    function gather() {
        return {
            smtpPort: readInt('set-smtpPort'),
            imapPort: readInt('set-imapPort'),
            enableImap: readBool('set-enableImap'),
            serverHostName: readVal('set-serverHostName').trim(),
            maxMessageSizeBytes: Math.round(readNum('set-maxMessageSizeMb') * 1048576),
            requireAuthentication: readBool('set-requireAuthentication'),
            captureSessionTranscript: readBool('set-captureSessionTranscript'),
            retentionMaxMessages: readInt('set-retentionMaxMessages'),
            retentionMaxAgeHours: readNum('set-retentionMaxAgeHours'),
            dashboardTitle: readVal('set-dashboardTitle').trim(),
            defaultTheme: readVal('set-defaultTheme'),
            relayEnabled: readBool('set-relayEnabled'),
            relayAutomatic: readBool('set-relayAutomatic'),
            relayHost: readVal('set-relayHost').trim(),
            relayPort: readInt('set-relayPort')
        };
    }

    async function saveSettings() {
        var button = document.getElementById('m4d-settings-save');
        var result = document.getElementById('m4d-settings-result');
        button.disabled = true;
        result.className = 'm4d-form__status';
        result.textContent = 'Saving…';
        try {
            var response = await M.api('/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(gather())
            });
            result.className = 'm4d-form__status m4d-form__status--ok';
            result.textContent = response && response.listenerRestarted
                ? 'Saved — listeners are re-binding.'
                : 'Settings saved.';
            M.toast('Settings saved.', 'success');
            setTimeout(M.refreshStatus, 600);
        } catch (e) {
            result.className = 'm4d-form__status m4d-form__status--error';
            result.textContent = 'Could not save: ' + e.message;
        } finally {
            button.disabled = false;
        }
    }

    /* ---------- wiring ---------- */
    M.ready(function () {
        document.getElementById('m4d-settings-save').addEventListener('click', saveSettings);
        document.getElementById('m4d-settings-reload').addEventListener('click', loadSettings);

        M.onHub('status', applyStatus);
        M.onHub('poll', function () { M.refreshStatus(); });

        loadSettings();
        M.refreshStatus();
        setInterval(function () { M.refreshStatus(); }, 5000);
    });
})();
