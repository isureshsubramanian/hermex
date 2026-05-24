/* Hermex dashboard — shared client runtime (theme, API, realtime, helpers). */
(function () {
    'use strict';

    var API = '/hermex/api';
    var HUB = '/hermex/hub';

    /* ---------- theme ---------- */
    function effectiveTheme(choice) {
        if (choice === 'system' || !choice) {
            return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        }
        return choice;
    }
    function storedChoice() {
        try { return localStorage.getItem('hermex-theme') || 'system'; }
        catch (e) { return 'system'; }
    }
    function syncThemePicker(choice) {
        document.querySelectorAll('[data-theme-choice]').forEach(function (opt) {
            opt.classList.toggle('m4d-theme-opt--active',
                opt.getAttribute('data-theme-choice') === choice);
        });
    }
    function setTheme(choice) {
        try { localStorage.setItem('hermex-theme', choice); } catch (e) { /* ignore */ }
        document.documentElement.setAttribute('data-theme', effectiveTheme(choice));
        syncThemePicker(choice);
    }
    function initTheme() {
        var toggle = document.getElementById('m4d-theme-toggle');
        if (toggle) {
            toggle.addEventListener('click', function () {
                var current = document.documentElement.getAttribute('data-theme');
                setTheme(current === 'dark' ? 'light' : 'dark');
            });
        }
        document.querySelectorAll('[data-theme-choice]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                setTheme(btn.getAttribute('data-theme-choice'));
            });
        });
        syncThemePicker(storedChoice());
    }

    /* ---------- formatting helpers ---------- */
    function escapeHtml(value) {
        if (value == null) return '';
        return String(value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }
    function formatBytes(n) {
        n = Number(n) || 0;
        if (n < 1024) return n + ' B';
        var units = ['KB', 'MB', 'GB', 'TB'];
        var i = -1;
        do { n /= 1024; i++; } while (n >= 1024 && i < units.length - 1);
        return n.toFixed(n < 10 ? 1 : 0) + ' ' + units[i];
    }
    function formatDate(iso) {
        if (!iso) return '—';
        var d = new Date(iso);
        if (isNaN(d.getTime())) return '—';
        return d.toLocaleString(undefined, {
            year: 'numeric', month: 'short', day: 'numeric',
            hour: '2-digit', minute: '2-digit', second: '2-digit'
        });
    }
    function formatRelative(iso) {
        if (!iso) return '';
        var d = new Date(iso);
        if (isNaN(d.getTime())) return '';
        var s = (Date.now() - d.getTime()) / 1000;
        if (s < 45) return 'just now';
        if (s < 90) return '1 min ago';
        if (s < 3600) return Math.round(s / 60) + ' min ago';
        if (s < 5400) return '1 hr ago';
        if (s < 86400) return Math.round(s / 3600) + ' hr ago';
        if (s < 172800) return 'yesterday';
        if (s < 604800) return Math.round(s / 86400) + ' days ago';
        return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }
    function debounce(fn, ms) {
        var timer;
        return function () {
            var args = arguments, self = this;
            clearTimeout(timer);
            timer = setTimeout(function () { fn.apply(self, args); }, ms);
        };
    }

    /* ---------- toast ---------- */
    var toastTimer;
    function toast(message, kind) {
        var el = document.getElementById('m4d-toast');
        if (!el) return;
        el.textContent = message;
        el.className = 'm4d-toast m4d-toast--show' + (kind ? ' m4d-toast--' + kind : '');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { el.className = 'm4d-toast'; }, 3200);
    }

    /* ---------- API ---------- */
    async function api(path, options) {
        var res = await fetch(API + path, Object.assign({
            headers: { 'Accept': 'application/json' }
        }, options || {}));
        if (!res.ok) {
            var body = '';
            try { body = await res.text(); } catch (e) { /* ignore */ }
            throw new Error('Request failed (' + res.status + ') ' + body);
        }
        if (res.status === 204) return null;
        var contentType = res.headers.get('content-type') || '';
        return contentType.indexOf('application/json') >= 0 ? res.json() : res.text();
    }

    /* ---------- realtime (SignalR with a polling fallback) ---------- */
    var handlers = {};
    function onHub(eventName, fn) {
        (handlers[eventName] = handlers[eventName] || []).push(fn);
    }
    function emit(eventName, payload) {
        (handlers[eventName] || []).forEach(function (fn) {
            try { fn(payload); }
            catch (e) { console.error('Hermex: handler error for ' + eventName, e); }
        });
    }
    var pollTimer = null;
    function startPolling() {
        if (pollTimer) return;
        pollTimer = setInterval(function () { emit('poll'); }, 5000);
    }
    function startHub() {
        var events = ['messageReceived', 'messageUpdated', 'messageDeleted',
            'inboxCleared', 'statsChanged', 'logAdded'];

        if (typeof signalR === 'undefined') {
            startPolling();
            return;
        }

        var connection = new signalR.HubConnectionBuilder()
            .withUrl(HUB)
            .withAutomaticReconnect()
            .build();

        events.forEach(function (ev) {
            connection.on(ev, function (payload) { emit(ev, payload); });
        });
        connection.onreconnecting(function () { setStatusPill('warn', 'Reconnecting…'); });
        connection.onreconnected(function () { refreshStatus(); });

        connection.start().catch(function () { startPolling(); });
    }

    /* ---------- server status pill ---------- */
    function setStatusPill(kind, label) {
        var el = document.getElementById('m4d-server-status');
        if (!el) return;
        el.className = 'm4d-status m4d-status--' + kind;
        var lbl = el.querySelector('.m4d-status__label');
        if (lbl) lbl.textContent = label;
    }
    async function refreshStatus() {
        try {
            var status = await api('/status');
            var srv = status.server;
            var kind = 'ok';
            var label = 'Listening : ' + (srv.listeningPort || srv.configuredPort);
            if (srv.status === 'PortConflict') { kind = 'error'; label = 'Port conflict'; }
            else if (srv.status === 'Faulted') { kind = 'error'; label = 'Server faulted'; }
            else if (srv.status !== 'Listening') { kind = 'warn'; label = srv.status; }
            setStatusPill(kind, label);
            emit('status', status);
            return status;
        } catch (e) {
            setStatusPill('error', 'Unavailable');
            return null;
        }
    }

    function ready(fn) {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', fn);
        } else {
            fn();
        }
    }

    ready(function () {
        initTheme();
        refreshStatus();
        startHub();
    });

    window.Hermex = {
        API: API,
        api: api,
        onHub: onHub,
        startHub: startHub,
        refreshStatus: refreshStatus,
        toast: toast,
        escapeHtml: escapeHtml,
        formatBytes: formatBytes,
        formatDate: formatDate,
        formatRelative: formatRelative,
        debounce: debounce,
        setTheme: setTheme,
        ready: ready
    };
})();
