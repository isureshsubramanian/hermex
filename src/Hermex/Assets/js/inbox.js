/* Hermex dashboard — Inbox page. */
(function () {
    'use strict';
    var M = window.Hermex;

    var ICON_CLIP = '<svg viewBox="0 0 24 24" width="11" height="11" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M21.44 11.05 12.25 20.24a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/></svg>';
    var ICON_DOWNLOAD = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5"/><path d="M12 15V3"/></svg>';
    var ICON_TRASH = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>';
    var ICON_FILE = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/></svg>';
    var ICON_RELAY = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/><path d="M22 2 15 22l-4-9-9-4z"/></svg>';
    var ICON_FOLDER = '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/></svg>';

    var state = { page: 1, search: '', unread: false, selectedId: null, mailboxId: null, totalPages: 1, detail: null, relayEnabled: false, sourceCache: {} };
    var el = {};

    function cache() {
        el.list = document.getElementById('m4d-msg-list');
        el.listEmpty = document.getElementById('m4d-list-empty');
        el.pager = document.getElementById('m4d-pager');
        el.detail = document.getElementById('m4d-detail');
        el.detailEmpty = document.getElementById('m4d-detail-empty');
        el.search = document.getElementById('m4d-search');
        el.unread = document.getElementById('m4d-unread-only');
        el.refresh = document.getElementById('m4d-refresh');
        el.clear = document.getElementById('m4d-clear');
        el.mailboxList = document.getElementById('m4d-mailbox-list');
        el.statTotal = document.getElementById('m4d-stat-total');
        el.statUnread = document.getElementById('m4d-stat-unread');
        el.statSize = document.getElementById('m4d-stat-size');
    }

    /* ---------- mailboxes ---------- */
    async function loadMailboxes() {
        try {
            renderMailboxes(await M.api('/mailboxes') || []);
        } catch (e) {
            /* non-fatal */
        }
    }

    function renderMailboxes(mailboxes) {
        if (!el.mailboxList) return;
        mailboxes.sort(function (a, b) {
            if (a.address === 'INBOX') return -1;
            if (b.address === 'INBOX') return 1;
            return a.address.localeCompare(b.address);
        });
        if (state.mailboxId == null) {
            var inbox = mailboxes.find(function (m) { return m.address === 'INBOX'; });
            if (inbox) state.mailboxId = inbox.id;
        }

        el.mailboxList.innerHTML = '';
        mailboxes.forEach(function (m) {
            var li = document.createElement('li');
            li.className = 'm4d-mailbox' + (m.id === state.mailboxId ? ' m4d-mailbox--active' : '');
            li.dataset.mailboxId = m.id;
            li.title = m.address + ' — ' + m.messageCount + ' message(s)';
            var name = m.address === 'INBOX' ? 'All mail' : m.address;
            var countClass = 'm4d-mailbox__count' + (m.unreadCount ? '' : ' m4d-mailbox__count--zero');
            li.innerHTML =
                '<span class="m4d-mailbox__icon">' + ICON_FOLDER + '</span>' +
                '<span class="m4d-mailbox__name">' + M.escapeHtml(name) + '</span>' +
                '<span class="' + countClass + '">' + (m.unreadCount || 0) + '</span>';
            li.addEventListener('click', function () { selectMailbox(m.id); });
            el.mailboxList.appendChild(li);
        });
    }

    function selectMailbox(id) {
        state.mailboxId = id;
        state.page = 1;
        el.mailboxList.querySelectorAll('.m4d-mailbox').forEach(function (row) {
            row.classList.toggle('m4d-mailbox--active', Number(row.dataset.mailboxId) === id);
        });
        loadList();
    }

    /* ---------- inbox list ---------- */
    async function loadList() {
        try {
            var query = '?page=' + state.page + '&pageSize=50';
            if (state.search) query += '&search=' + encodeURIComponent(state.search);
            if (state.unread) query += '&unread=true';
            if (state.mailboxId) query += '&mailbox=' + state.mailboxId;
            var result = await M.api('/messages' + query);
            renderList(result);
        } catch (e) {
            M.toast('Could not load messages.', 'error');
        }
    }

    function renderList(result) {
        var items = result.items || [];
        if (state.page > 1 && items.length === 0 && (result.totalCount || 0) > 0) {
            state.page = Math.max(1, result.totalPages || 1);
            loadList();
            return;
        }

        el.list.innerHTML = '';
        el.listEmpty.hidden = items.length > 0;
        items.forEach(function (m) { el.list.appendChild(renderRow(m)); });

        state.totalPages = result.totalPages || 1;
        renderPager(result);
    }

    function renderRow(m) {
        var li = document.createElement('li');
        li.className = 'm4d-msg' + (m.isRead ? '' : ' m4d-msg--unread') +
            (m.id === state.selectedId ? ' m4d-msg--selected' : '');
        li.dataset.id = m.id;
        li.setAttribute('role', 'option');

        var chips = '';
        if (m.hasHtml) chips += '<span class="m4d-chip m4d-chip--html">HTML</span>';
        if (m.hasAttachments) chips += '<span class="m4d-chip">' + ICON_CLIP + m.attachmentCount + '</span>';

        li.innerHTML =
            '<span class="m4d-msg__from">' + M.escapeHtml(m.from || m.fromAddress || '(unknown sender)') + '</span>' +
            '<span class="m4d-msg__time">' + M.escapeHtml(M.formatRelative(m.receivedAtUtc)) + '</span>' +
            '<span class="m4d-msg__subject">' + M.escapeHtml(m.subject || '(no subject)') + '</span>' +
            '<span class="m4d-msg__meta">' + chips +
                '<span class="m4d-msg__preview">' + M.escapeHtml(m.to || '') + '</span>' +
                '<span style="margin-left:auto">' + M.escapeHtml(M.formatBytes(m.rawSize)) + '</span>' +
            '</span>' +
            (m.isRead ? '' : '<span class="m4d-msg__unread-dot"></span>');

        li.addEventListener('click', function () { selectMessage(m.id); });
        return li;
    }

    function renderPager(result) {
        el.pager.innerHTML = '';
        if (state.totalPages <= 1) return;

        function pageButton(label, disabled, handler) {
            var b = document.createElement('button');
            b.type = 'button';
            b.textContent = label;
            b.disabled = disabled;
            if (!disabled) b.addEventListener('click', handler);
            return b;
        }
        el.pager.appendChild(pageButton('‹', state.page <= 1, function () { state.page--; loadList(); }));
        var label = document.createElement('span');
        label.textContent = 'Page ' + result.page + ' of ' + state.totalPages;
        el.pager.appendChild(label);
        el.pager.appendChild(pageButton('›', state.page >= state.totalPages, function () { state.page++; loadList(); }));
    }

    /* ---------- message detail ---------- */
    function selectMessage(id) {
        state.selectedId = id;
        document.querySelectorAll('.m4d-msg').forEach(function (row) {
            row.classList.toggle('m4d-msg--selected', Number(row.dataset.id) === id);
        });
        loadDetail(id);
    }

    async function loadDetail(id) {
        try {
            var detail = await M.api('/messages/' + id);
            state.detail = detail;
            renderDetail(detail);
            markRowRead(id);
        } catch (e) {
            M.toast('Could not open the message.', 'error');
        }
    }

    function markRowRead(id) {
        var row = document.querySelector('.m4d-msg[data-id="' + id + '"]');
        if (row && row.classList.contains('m4d-msg--unread')) {
            row.classList.remove('m4d-msg--unread');
            var dot = row.querySelector('.m4d-msg__unread-dot');
            if (dot) dot.remove();
        }
    }

    function detailRow(label, value) {
        return '<div class="m4d-detail__row"><span class="m4d-detail__label">' + label +
            '</span><span class="m4d-detail__value">' + M.escapeHtml(value) + '</span></div>';
    }

    function renderDetail(d) {
        el.detailEmpty.hidden = true;
        el.detail.hidden = false;

        var tabs = [];
        if (d.htmlBody != null) tabs.push({ id: 'html', label: 'HTML' });
        if (d.textBody != null) tabs.push({ id: 'text', label: 'Text' });
        tabs.push({ id: 'structure', label: 'Structure' });
        tabs.push({ id: 'headers', label: 'Headers' });
        if (d.attachments && d.attachments.length) {
            tabs.push({ id: 'attachments', label: 'Attachments', count: d.attachments.length });
        }
        tabs.push({ id: 'source', label: 'Source' });
        if (d.transcript) tabs.push({ id: 'session', label: 'Session' });
        var activeTab = tabs[0].id;

        var fromText = d.fromDisplay && d.fromDisplay !== d.fromAddress
            ? d.fromDisplay + ' <' + d.fromAddress + '>'
            : (d.fromAddress || '(unknown sender)');

        var html = '<div class="m4d-detail__head">';
        html += '<h1 class="m4d-detail__subject">' + M.escapeHtml(d.subject || '(no subject)') + '</h1>';
        html += detailRow('From', fromText);
        html += detailRow('To', d.toDisplay || d.envelopeTo || '—');
        if (d.ccDisplay) html += detailRow('Cc', d.ccDisplay);
        html += detailRow('Date', M.formatDate(d.receivedAtUtc));
        if (d.securedWithTls) html += detailRow('Security', 'Delivered over TLS');
        html += '<div class="m4d-detail__bar">';
        html += '<a class="m4d-btn" href="' + M.API + '/messages/' + d.id + '/raw">' + ICON_DOWNLOAD + 'Download .eml</a>';
        html += '<button type="button" class="m4d-btn" data-act="unread">Mark unread</button>';
        if (state.relayEnabled)
            html += '<button type="button" class="m4d-btn" data-act="relay">' + ICON_RELAY + 'Relay</button>';
        html += '<span class="m4d-detail__bar-spacer"></span>';
        html += '<button type="button" class="m4d-btn m4d-btn--danger" data-act="delete">' + ICON_TRASH + 'Delete</button>';
        html += '</div></div>';

        if (d.warnings && d.warnings.length) {
            html += '<div class="m4d-warnings"><strong>Parser notes</strong><ul>';
            d.warnings.forEach(function (w) { html += '<li>' + M.escapeHtml(w) + '</li>'; });
            html += '</ul></div>';
        }

        html += '<div class="m4d-tabs">';
        tabs.forEach(function (t) {
            html += '<button type="button" class="m4d-tab' + (t.id === activeTab ? ' m4d-tab--active' : '') +
                '" data-tab="' + t.id + '">' + t.label +
                (t.count ? '<span class="m4d-tab__count">' + t.count + '</span>' : '') + '</button>';
        });
        html += '</div>';
        html += '<div class="m4d-detail__body" id="m4d-detail-body"></div>';

        el.detail.innerHTML = html;

        el.detail.querySelector('[data-act="delete"]').addEventListener('click', function () { deleteMessage(d.id); });
        el.detail.querySelector('[data-act="unread"]').addEventListener('click', function () { markUnread(d.id); });
        var relayButton = el.detail.querySelector('[data-act="relay"]');
        if (relayButton) relayButton.addEventListener('click', function () { relayMessage(d.id); });
        el.detail.querySelectorAll('.m4d-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                el.detail.querySelectorAll('.m4d-tab').forEach(function (t) {
                    t.classList.toggle('m4d-tab--active', t === tab);
                });
                showTab(tab.dataset.tab, d);
            });
        });

        showTab(activeTab, d);
    }

    function showTab(tabId, d) {
        var body = document.getElementById('m4d-detail-body');
        if (!body) return;
        body.classList.toggle('m4d-detail__body--pad', tabId !== 'html');

        if (tabId === 'html') {
            body.innerHTML = '<iframe class="m4d-html-frame" sandbox="" title="HTML message body" src="' +
                M.API + '/messages/' + d.id + '/html"></iframe>';
        } else if (tabId === 'text') {
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(d.textBody || '') + '</pre>';
        } else if (tabId === 'headers') {
            body.innerHTML = renderHeaders(d.headers || []);
        } else if (tabId === 'attachments') {
            body.innerHTML = renderAttachments(d);
        } else if (tabId === 'structure') {
            body.innerHTML = renderStructure(d.structure);
        } else if (tabId === 'session') {
            body.innerHTML = renderSession(d.transcript);
        } else if (tabId === 'source') {
            renderSource(body, d.id);
        }
    }

    function renderStructure(node) {
        if (!node) return '<p class="m4d-empty__hint">No MIME structure available.</p>';
        return '<div class="m4d-tree">' + structureNode(node) + '</div>';
    }

    function structureNode(node) {
        var meta = [];
        if (node.fileName) meta.push(M.escapeHtml(node.fileName));
        if (node.encoding && node.encoding !== '7bit') meta.push(M.escapeHtml(node.encoding));
        if (node.charset) meta.push(M.escapeHtml(node.charset));
        if (node.size) meta.push(M.formatBytes(node.size));
        var children = (node.children || []).map(structureNode).join('');
        return '<div class="m4d-tree__node">' +
            '<div class="m4d-tree__row">' +
                '<span class="m4d-tree__role m4d-tree__role--' + M.escapeHtml(node.role || 'body') + '">' +
                    M.escapeHtml(node.role || 'body') + '</span>' +
                '<span class="m4d-tree__type">' + M.escapeHtml(node.mediaType || '') + '</span>' +
                (meta.length ? '<span class="m4d-tree__meta">' + meta.join(' &middot; ') + '</span>' : '') +
            '</div>' +
            (children ? '<div class="m4d-tree__children">' + children + '</div>' : '') +
            '</div>';
    }

    function renderSession(transcript) {
        if (!transcript) return '<p class="m4d-empty__hint">No session transcript was captured.</p>';
        var lines = transcript.split(/\r?\n/).map(function (line) {
            var cls = 'm4d-x-note';
            if (line.indexOf('S: ') === 0) cls = 'm4d-x-server';
            else if (line.indexOf('C: ') === 0) cls = 'm4d-x-client';
            return '<span class="' + cls + '">' + M.escapeHtml(line) + '</span>';
        });
        return '<pre class="m4d-pre m4d-transcript">' + lines.join('\n') + '</pre>';
    }

    function renderHeaders(headers) {
        if (!headers.length) return '<p class="m4d-empty__hint">No headers.</p>';
        var rows = headers.map(function (h) {
            return '<tr><td>' + M.escapeHtml(h.name) + '</td><td>' + M.escapeHtml(h.value) + '</td></tr>';
        }).join('');
        return '<table class="m4d-headers"><tbody>' + rows + '</tbody></table>';
    }

    function renderAttachments(d) {
        var items = (d.attachments || []).map(function (a) {
            var meta = (a.contentType || 'application/octet-stream') + ' · ' + M.formatBytes(a.size) +
                (a.isInline ? ' · inline' : '');
            return '<a class="m4d-attachment" href="' + M.API + '/messages/' + d.id + '/attachments/' + a.index + '">' +
                '<span class="m4d-attachment__icon">' + ICON_FILE + '</span>' +
                '<span class="m4d-attachment__info">' +
                '<span class="m4d-attachment__name">' + M.escapeHtml(a.fileName || ('attachment-' + a.index)) + '</span>' +
                '<span class="m4d-attachment__meta">' + M.escapeHtml(meta) + '</span>' +
                '</span>' + ICON_DOWNLOAD + '</a>';
        }).join('');
        return '<div class="m4d-attachments">' + items + '</div>';
    }

    async function renderSource(body, id) {
        if (state.sourceCache[id] != null) {
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(state.sourceCache[id]) + '</pre>';
            return;
        }
        body.innerHTML = '<p class="m4d-pre">Loading source…</p>';
        try {
            var raw = await M.api('/messages/' + id + '/raw');
            state.sourceCache[id] = raw;
            body.innerHTML = '<pre class="m4d-pre">' + M.escapeHtml(raw) + '</pre>';
        } catch (e) {
            body.innerHTML = '<p class="m4d-pre">Could not load the raw source.</p>';
        }
    }

    function showDetailEmpty() {
        state.detail = null;
        el.detail.hidden = true;
        el.detail.innerHTML = '';
        el.detailEmpty.hidden = false;
    }

    /* ---------- actions ---------- */
    async function deleteMessage(id) {
        if (!window.confirm('Delete this message permanently?')) return;
        try {
            await M.api('/messages/' + id, { method: 'DELETE' });
            if (state.selectedId === id) { state.selectedId = null; showDetailEmpty(); }
            M.toast('Message deleted.');
            loadList();
        } catch (e) {
            M.toast('Could not delete the message.', 'error');
        }
    }

    async function markUnread(id) {
        try {
            await M.api('/messages/' + id + '/read?isRead=false', { method: 'POST' });
            M.toast('Marked as unread.');
            loadList();
        } catch (e) {
            M.toast('Could not update the message.', 'error');
        }
    }

    async function relayMessage(id) {
        try {
            await M.api('/messages/' + id + '/relay', { method: 'POST' });
            M.toast('Message relayed to the upstream SMTP server.', 'success');
        } catch (e) {
            M.toast('Relay failed: ' + e.message, 'error');
        }
    }

    async function clearInbox() {
        if (!window.confirm('Delete every message in the inbox? This cannot be undone.')) return;
        try {
            var result = await M.api('/messages', { method: 'DELETE' });
            state.selectedId = null;
            showDetailEmpty();
            M.toast('Cleared ' + (result && result.cleared != null ? result.cleared : 0) + ' message(s).');
            state.page = 1;
            loadList();
        } catch (e) {
            M.toast('Could not clear the inbox.', 'error');
        }
    }

    /* ---------- stats ---------- */
    function applyStats(stats) {
        if (!stats) return;
        if (el.statTotal) el.statTotal.textContent = (stats.totalMessages || 0).toLocaleString();
        if (el.statUnread) el.statUnread.textContent = (stats.unreadMessages || 0).toLocaleString();
        if (el.statSize) el.statSize.textContent = M.formatBytes(stats.totalSizeBytes || 0);
    }

    function applyStatus(status) {
        if (!status) return;
        applyStats(status.stats);
        if (status.options) state.relayEnabled = !!status.options.relayEnabled;
    }

    /* ---------- wiring ---------- */
    M.ready(function () {
        cache();

        var debouncedSearch = M.debounce(function () {
            state.search = el.search.value.trim();
            state.page = 1;
            loadList();
        }, 280);
        el.search.addEventListener('input', debouncedSearch);
        el.unread.addEventListener('change', function () {
            state.unread = el.unread.checked;
            state.page = 1;
            loadList();
        });
        el.refresh.addEventListener('click', function () { loadList(); M.refreshStatus(); });
        el.clear.addEventListener('click', clearInbox);

        var debouncedReload = M.debounce(function () { loadList(); loadMailboxes(); }, 350);
        M.onHub('messageReceived', debouncedReload);
        M.onHub('messageDeleted', debouncedReload);
        M.onHub('messageUpdated', function (p) {
            if (p && !p.isRead) loadList();
            loadMailboxes();
        });
        M.onHub('inboxCleared', function () {
            state.selectedId = null;
            showDetailEmpty();
            loadList();
            loadMailboxes();
        });
        M.onHub('statsChanged', applyStats);
        M.onHub('status', applyStatus);
        M.onHub('poll', function () { loadList(); loadMailboxes(); });

        loadMailboxes().then(loadList);
        M.api('/status').then(applyStatus).catch(function () { });
    });
})();
