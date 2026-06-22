// ==================== API Client ====================
class ApiClient {
    constructor() {
        this.pendingRequests = new Map();
        this.requestId = 0;
        this.eventHandlers = new Map();
        window.chrome.webview.addEventListener('message', (e) => {
            const data = JSON.parse(e.data);
            if (data.type === 'event') {
                this.handleEvent(data.name, data.data);
            } else if (data.requestId && this.pendingRequests.has(data.requestId)) {
                const { resolve, reject } = this.pendingRequests.get(data.requestId);
                this.pendingRequests.delete(data.requestId);
                data.result?.error ? reject(data.result.error) : resolve(data.result);
            }
        });
    }
    async call(action, data = null) {
        return new Promise((resolve, reject) => {
            const id = ++this.requestId;
            this.pendingRequests.set(id, { resolve, reject });
            window.chrome.webview.postMessage(JSON.stringify({ requestId: id, action, data }));
            setTimeout(() => {
                if (this.pendingRequests.has(id)) {
                    this.pendingRequests.delete(id);
                    reject(new Error('Request timeout'));
                }
            }, 30000);
        });
    }
    pickFolder() {
        window.chrome.webview.hostObjects.host.ShowFolderPicker();
        return new Promise(resolve => { this.once('folderSelected', resolve); });
    }
    handleEvent(name, data) {
        (this.eventHandlers.get(name) || []).forEach(fn => fn(data));
        window.dispatchEvent(new CustomEvent(`app:${name}`, { detail: data }));
    }
    on(eventName, callback) {
        if (!this.eventHandlers.has(eventName)) this.eventHandlers.set(eventName, []);
        this.eventHandlers.get(eventName).push(callback);
    }
    off(eventName, callback) {
        const handlers = this.eventHandlers.get(eventName);
        if (handlers) this.eventHandlers.set(eventName, handlers.filter(h => h !== callback));
    }
    once(eventName, callback) {
        const handler = (e) => { callback(e.detail); window.removeEventListener(`app:${eventName}`, handler); };
        window.addEventListener(`app:${eventName}`, handler, { once: true });
    }
}
const api = new ApiClient();

// ==================== State ====================
class AppState {
    constructor() {
        this.data = {};
        this.listeners = new Map();
    }
    set(key, value) {
        this.data[key] = value;
        (this.listeners.get(key) || []).forEach(fn => fn(value));
    }
    get(key) { return this.data[key]; }
    onChange(key, callback) {
        if (!this.listeners.has(key)) this.listeners.set(key, []);
        this.listeners.get(key).push(callback);
    }
    async refreshSources() { const s = await api.call('getSources'); this.set('sources', s); }
    async refreshTargets() { const t = await api.call('getTargets'); this.set('targets', t); }
    async refreshSchedules() { const s = await api.call('getSchedules'); this.set('schedules', s); }
    async refreshSettings() { const s = await api.call('getSettings'); this.set('settings', s); }
    async refreshDashboard() { const d = await api.call('getDashboard'); this.set('dashboard', d); }
    async refreshAll() {
        try { await Promise.all([
            this.refreshSources(), this.refreshTargets(),
            this.refreshSchedules(), this.refreshSettings(),
            this.refreshDashboard()
        ]); } catch(e) {}
    }
}
const state = new AppState();

// ==================== Router ====================
class Router {
    constructor() {
        this.currentRoute = null;
        this.pageIds = ['dashboard','sources','schedule','targets','logs','settings'];
    }
    navigate(route) {
        if (this.currentRoute === route) return;
        const prev = this.currentRoute;
        document.querySelectorAll('.nav-item').forEach(el => {
            el.classList.toggle('active', el.dataset.route === route);
        });
        this.pageIds.forEach(id => {
            const el = document.getElementById(`page-${id}`);
            if (el) el.style.display = id === route ? 'block' : 'none';
        });
        this.currentRoute = route;
        const page = document.getElementById(`page-${route}`);
        if (page) page.dispatchEvent(new CustomEvent('page:show'));
    }
}
const router = new Router();

// ==================== Calendar Widget ====================
class CalendarWidget {
    constructor() {
        this._container = null;
        this.currentMonth = new Date().getMonth();
        this.currentYear = new Date().getFullYear();
        this.backupDates = new Set();
        this.scheduledDates = new Set();
    }
    setBackupDates(dates) { this.backupDates = new Set(dates.map(d => this.fmt(d))); }
    setScheduledDates(dates) { this.scheduledDates = new Set(dates.map(d => this.fmt(d))); }
    fmt(date) {
        const d = new Date(date);
        return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
    }
    render(containerId) {
        this._container = document.getElementById(containerId);
        if (!this._container) return;
        this._container.innerHTML = `
            <div class="calendar-w">
                <div class="cal-h">
                    <button class="btn btn-ghost btn-sm" data-cal="prev">&#9664;</button>
                    <span class="cal-t" id="calTitle"></span>
                    <button class="btn btn-ghost btn-sm" data-cal="next">&#9654;</button>
                </div>
                <div class="cal-g" id="calGrid"></div>
                <div class="cal-l">
                    <span><span class="d d-ok"></span> ${t('Выполнен')}</span>
                    <span><span class="d d-warn"></span> ${t('Запланирован')}</span>
                </div>
            </div>`;
        this.draw();
        this._container.querySelector('[data-cal="prev"]').onclick = () => {
            this.currentMonth--; if (this.currentMonth < 0) { this.currentMonth = 11; this.currentYear--; } this.draw();
        };
        this._container.querySelector('[data-cal="next"]').onclick = () => {
            this.currentMonth++; if (this.currentMonth > 11) { this.currentMonth = 0; this.currentYear++; } this.draw();
        };
    }
    draw() {
        if (!this._container) return;
        const months = _lang === 'en' ? months_en : months_ru;
        const dayNames = _lang === 'en' ? daysShort_en : daysShort_ru;
        this._container.querySelector('#calTitle').textContent = `${months[this.currentMonth]} ${this.currentYear}`;
        const firstDay = new Date(this.currentYear, this.currentMonth, 1).getDay();
        const daysInMonth = new Date(this.currentYear, this.currentMonth + 1, 0).getDate();
        const daysInPrev = new Date(this.currentYear, this.currentMonth, 0).getDate();
        const startOffset = firstDay === 0 ? 6 : firstDay - 1;
        const today = this.fmt(new Date());
        let html = dayNames.map(d => `<div class="cal-dn">${d}</div>`).join('');
        for (let i = startOffset - 1; i >= 0; i--) html += `<div class="cal-d cal-o">${daysInPrev - i}</div>`;
        for (let day = 1; day <= daysInMonth; day++) {
            const ds = `${this.currentYear}-${String(this.currentMonth+1).padStart(2,'0')}-${String(day).padStart(2,'0')}`;
            let cls = 'cal-d';
            if (ds === today) cls += ' cal-td';
            if (this.backupDates.has(ds)) cls += ' cal-ok';
            if (this.scheduledDates.has(ds)) cls += ' cal-sk';
            html += `<div class="${cls}">${day}</div>`;
        }
        const rem = 42 - (startOffset + daysInMonth);
        for (let day = 1; day <= rem; day++) html += `<div class="cal-d cal-o">${day}</div>`;
        this._container.querySelector('#calGrid').innerHTML = html;
    }
}
const calendarWidget = new CalendarWidget();

// ==================== Helpers ====================
function showToast(title, message, type) {
    const c = document.getElementById('toast-container');
    const t = document.createElement('div');
    t.className = 'toast';
    t.innerHTML = `<div class="toast-title">${title}</div><div class="toast-message">${message}</div>`;
    c.appendChild(t);
    setTimeout(() => t.remove(), 4000);
}

function formatSize(bytes) {
    if (!bytes || bytes === 0) return '0 B';
    const sizes = ['B','KB','MB','GB','TB'];
    let o = 0; let s = bytes;
    while (s >= 1024 && o < sizes.length - 1) { o++; s /= 1024; }
    return `${s.toFixed(1)} ${sizes[o]}`;
}

function esc(s) { return (s||'').replace(/"/g,'&quot;').replace(/'/g,'&#39;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }

function showModal(html) {
    const ov = document.createElement('div'); ov.className = 'modal-overlay';
    ov.innerHTML = `<div class="modal">${html}</div>`;
    document.body.appendChild(ov);
    ov.addEventListener('click', (e) => { if (e.target === ov) ov.remove(); });
    const cancel = ov.querySelector('#cancelBtn');
    if (cancel) cancel.onclick = () => ov.remove();
    return ov;
}

function spinner() { return '<div class="spinner"></div>'; }

const ICONS = {
    dashboard: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><rect x="3" y="3" width="7" height="9" rx="1"/><rect x="14" y="3" width="7" height="5" rx="1"/><rect x="14" y="12" width="7" height="9" rx="1"/><rect x="3" y="16" width="7" height="5" rx="1"/></svg>`,
    sources: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>`,
    schedule: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>`,
    targets: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><rect x="2" y="3" width="20" height="14" rx="2" ry="2"/><line x1="8" y1="21" x2="16" y2="21"/><line x1="12" y1="17" x2="12" y2="21"/></svg>`,
    logs: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="16" y1="13" x2="8" y2="13"/><line x1="16" y1="17" x2="8" y2="17"/></svg>`,
    settings: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>`,
    play: `<svg viewBox="0 0 24 24" fill="currentColor" stroke="none" style="width:14px;height:14px"><polygon points="5 3 19 12 5 21 5 3"/></svg>`,
    refresh: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/></svg>`,
    add: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>`,
    edit: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></svg>`,
    trash: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>`,
    check: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><polyline points="20 6 9 17 4 12"/></svg>`,
    archive: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><polyline points="21 8 21 21 3 21 3 8"/><rect x="1" y="3" width="22" height="5" rx="2" ry="2"/><line x1="10" y1="12" x2="14" y2="12"/></svg>`,
    search: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>`,
    download: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>`,
    upload: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>`,
    folder: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/></svg>`,
    bell: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>`,
    server: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><rect x="2" y="2" width="20" height="8" rx="2" ry="2"/><rect x="2" y="14" width="20" height="8" rx="2" ry="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>`,
    calendar: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><rect x="3" y="4" width="18" height="18" rx="2" ry="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/></svg>`,
    activity: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/></svg>`,
    wifi: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M5 12.55a11 11 0 0 1 14.08 0"/><path d="M1.42 9a16 16 0 0 1 21.16 0"/><path d="M8.53 16.11a6 6 0 0 1 6.95 0"/><line x1="12" y1="20" x2="12.01" y2="20"/></svg>`,
    globe: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>`,
    clock: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>`,
    shield: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>`,
    hardDrive: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:14px;height:14px"><line x1="22" y1="12" x2="2" y2="12"/><path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/><line x1="6" y1="16" x2="6.01" y2="16"/><line x1="10" y1="16" x2="10.01" y2="16"/></svg>`,
    sun: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>`,
    moon: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="width:18px;height:18px"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>`
};

function icon(name) { return ICONS[name] || ''; }

function btn(label, cls, extra) {
    return `<button class="btn ${cls}" ${extra||''}>${label}</button>`;
}

function badge(text, cls) {
    return `<span class="badge badge-${cls}">${text}</span>`;
}

// ==================== i18n & Theme ====================
let _lang = localStorage.getItem('backupapp_lang') || 'ru';
let _theme = localStorage.getItem('backupapp_theme') || 'dark';

const _en = {
  'Дашборд':'Dashboard','Запустить бэкап':'Start Backup','Обновить':'Refresh',
  'Календарь бэкапов':'Backup Calendar','Очередь ожидания':'Pending Queue',
  'Последние бэкапы':'Recent Backups','Последние события':'Recent Events',
  'Всего бэкапов':'Total Backups','Успешных':'Successful','Следующий бэкап':'Next Backup',
  'Активных источников':'Active Sources','ПК онлайн / всего':'PCs Online / Total',
  'Нет событий':'No Events','История бэкапов пуста':'No Backup History',
  'Нет ожидающих передач':'No Pending Transfers',
  'Успешно':'Success','Ошибка':'Error','В процессе':'In Progress','В очереди':'Queued',
  'Попыток:':'Attempts:','Источник':'Source','Размер':'Size','Статус':'Status','Время':'Time',
  'мин':'min','Только что':'Just now','На':'To','Целевой ПК':'Target PC','В очереди:':'Queued:',
  'Нет активных источников':'No Active Sources','Добавьте источники в настройках':'Add sources in settings',
  'Бэкап запущен':'Backup Started',
  'Источники бэкапа':'Backup Sources','Добавить источник':'Add Source',
  'Активен':'Active','Отключён':'Disabled',
  'Оценка':'Estimate','Оценить размер':'Estimate Size',
  'Проверить SHA256':'Check SHA256','Редактировать':'Edit','Удалить':'Delete',
  'Нет источников. Нажмите "Добавить источник"':'No Sources. Click "Add Source"',
  'Редактировать источник':'Edit Source','Добавить источник':'Add Source',
  'Название':'Name','Путь к папке':'Folder Path','Обзор':'Browse',
  'Маски исключений':'Exclusion Patterns','Сжатие (0-9)':'Compression (0-9)',
  'Пароль AES-256':'AES-256 Password','Оставьте пустым если не нужно':'Leave empty if not needed',
  'Включено':'Enabled','Отмена':'Cancel','Сохранить':'Save','Добавить':'Add',
  'Файлов:':'Files:','Объём:':'Size:','Нет локальных бэкапов':'No local backups',
  'Заполните название и путь':'Fill in name and path',
  'Расписание бэкапов':'Backup Schedule','Календарь':'Calendar',
  'Сначала добавьте источники':'Add sources first',
  'Настройте расписание для каждого источника.':'Configure schedule for each source.',
  'Не настроено':'Not configured','Не задано':'Not set','Умный':'Smart','Настроить':'Configure',
  'Ошибка загрузки':'Load Error',
  'Расписание:':'Schedule:','Тип':'Type','Интервал (часы)':'Interval (hours)',
  'Время запуска':'Start Time','Дни недели':'Days of Week',
  'Умный расчёт':'Smart Calculation','Пропускать без изменений':'Skip if No Changes',
  'Анализировать':'Analyze','Анализ':'Analysis','Рекомендуемый интервал:':'Recommended interval:',
  'Путь не указан':'Path not specified','ч':'h',
  'Целевые ПК':'Target PCs','Добавить ПК':'Add PC',
  'Пинг':'Ping','Проверить доступность':'Check reachability',
  'Нет целевых ПК':'No Target PCs',
  'Редактировать ПК':'Edit PC','Добавить целевой ПК':'Add Target PC',
  'IP или сетевое имя':'IP or Hostname','Сетевой путь':'Network Path',
  'Другие учётные данные':'Different Credentials','Пользователь':'Username','Пароль':'Password',
  'Политика хранения':'Retention Policy','Хранить N копий':'Keep N copies',
  'Хранить N дней':'Keep N days','Заполните название и IP':'Fill in name and IP',
  'Проверка':'Check','ПК доступен':'PC is reachable','ПК недоступен':'PC is unreachable',
  'Удалить источник?':'Delete source?','Удалить целевой ПК?':'Delete target PC?',
  'Хранить:':'Keep:','копий /':'copies /','дн.':'days',
  'Журнал событий':'Event Log','Все уровни':'All Levels','Поиск...':'Search...',
  'Автообновление каждые 5 сек':'Auto-refresh every 5s','Нет записей':'No entries',
  'Экспорт':'Export','Импорт':'Import','Сохранено:':'Saved:',
  'Настройки':'Settings','Общие':'General','Язык':'Language','Тема':'Theme',
  'Русский':'Russian','Английский':'English','Тёмная':'Dark','Светлая':'Light',
  'Запуск при старте Windows':'Launch on Windows startup',
  'Сворачивать в трей':'Minimize to tray','Горячая клавиша':'Hotkey',
  'Уведомления':'Notifications','Toast-уведомления':'Toast Notifications',
  'Email':'Email','Включить':'Enable','SMTP сервер':'SMTP Server','Порт':'Port',
  'Сеть':'Network','Таймаут (сек)':'Timeout (sec)','Макс. параллельных передач':'Max parallel transfers',
  'Кол-во повторов':'Retry count','Задержка (мс)':'Delay (ms)',
  'Локальное хранение':'Local Storage','Сохранять локально':'Save locally','Путь':'Path',
  'Сохранить настройки':'Save Settings',
  'Настройки':'Settings','Сохранены':'Saved',
  'Источники':'Sources','Расписание':'Schedule','Целевые ПК':'Target PCs','Журнал':'Logs',
  'Бэкап сейчас':'Backup Now',
  'Нет источников':'No Sources','Добавьте источники':'Add sources','Бэкап':'Backup','Запущен':'Started',
  'Ошибка бэкапа':'Backup Error','Неизвестная ошибка':'Unknown Error','См. логи':'See logs',
  'Бэкап завершён':'Backup Complete',
  'Переключить тему':'Toggle theme','Тема':'Theme',
  'Выполнен':'Completed','Запланирован':'Scheduled',
  'Интервал':'Interval','Ежедневно':'Daily','Еженедельно':'Weekly','Ежемесячно':'Monthly','По изменению':'On Change',
  'Время:':'Time:','Интервал:':'Interval:','Дни:':'Days:','Без изменений':'No Changes',
  'Ошибка загрузки':'Load Error','Сжатие':'Compression','Исключений':'Exclusions','Например: Документы':'E.g. Documents',
};

function t(key, ...args) {
  let s = _lang === 'en' && _en[key] ? _en[key] : key;
  args.forEach((a, i) => s = s.replace(`{${i}}`, a));
  return s;
}

const months_ru = ['Январь','Февраль','Март','Апрель','Май','Июнь','Июль','Август','Сентябрь','Октябрь','Ноябрь','Декабрь'];
const months_en = ['January','February','March','April','May','June','July','August','September','October','November','December'];
const daysShort_ru = ['Пн','Вт','Ср','Чт','Пт','Сб','Вс'];
const daysShort_en = ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'];
const daysFull_ru = ['Вс','Пн','Вт','Ср','Чт','Пт','Сб'];
const daysFull_en = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];

function applyTheme(theme) {
  _theme = theme || 'dark';
  document.documentElement.setAttribute('data-theme', _theme === 'light' ? 'light' : 'dark');
  localStorage.setItem('backupapp_theme', _theme);
}

function setLang(lang) {
  _lang = lang || 'ru';
  localStorage.setItem('backupapp_lang', _lang);
  document.querySelector('html').setAttribute('lang', _lang);
}

const scheduleLabels = {
  ru: { Daily:'Ежедневно', Weekly:'Еженедельно', Monthly:'Ежемесячно', Interval:'Интервал', OnChange:'По изменению', Smart:'Умный расчёт' },
  en: { Daily:'Daily', Weekly:'Weekly', Monthly:'Monthly', Interval:'Interval', OnChange:'On Change', Smart:'Smart' }
};

function computeScheduleDates(schedule, maxDays) {
    if (maxDays === undefined) maxDays = 60;
    const now = new Date();
    const dates = [];
    const typeNames = ['Daily','Weekly','Monthly','Interval','OnChange','Smart'];
    const type = typeNames[schedule.type] || schedule.type;
    const setTime = (d, tod) => {
        if (!tod) return d;
        const p = tod.split(':');
        const r = new Date(d);
        r.setHours(+p[0], +p[1]||0, 0, 0);
        return r;
    };
    if (type === 'OnChange') return [];
    if (type === 'Interval' && schedule.intervalHours) {
        const ms = schedule.intervalHours * 3600000;
        let d = new Date(now);
        for (let i = 0; i < 60; i++) { d = new Date(d.getTime() + ms); if (d > now) dates.push(d); }
        return dates;
    }
    for (let day = 0; day <= maxDays; day++) {
        const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() + day);
        let candidate = null;
        if (type === 'Daily' && schedule.timeOfDay) candidate = setTime(d, schedule.timeOfDay);
        else if (type === 'Weekly' && schedule.timeOfDay) {
            if ((schedule.activeDays||[1,2,3,4,5]).includes(d.getDay())) candidate = setTime(d, schedule.timeOfDay);
        }
        else if (type === 'Monthly' && schedule.timeOfDay) { if (d.getDate() === 1) candidate = setTime(d, schedule.timeOfDay); }
        else if (type === 'Smart' && schedule.timeOfDay) candidate = setTime(d, schedule.timeOfDay);
        if (candidate && candidate > now) dates.push(candidate);
    }
    return dates;
}

// ==================== Components ====================
// Each component: { init(containerId) -> renders once, attaches events, returns this }
// Pages emit 'page:show' event when navigated to — components refresh data then.

// ----- Dashboard -----
const dashboard = {
    init(containerId) {
        const c = document.getElementById(containerId);
        c.innerHTML = `
            <div class="flex-between mb-16">
                <h1>${t('Дашборд')}</h1>
                <div class="flex flex-gap">
                    ${btn(icon('play')+' '+t('Запустить бэкап'),'btn-success','id="dashBackupBtn"')}
                    <button class="btn btn-ghost" id="dashThemeBtn" title="${t('Переключить тему')}">${_theme==='light'?icon('moon'):icon('sun')}</button>
                    ${btn(icon('refresh')+' '+t('Обновить'),'btn-ghost','id="dashRefreshBtn"')}
                </div>
            </div>
            <div class="stats-grid" id="dashStats"></div>
            <div id="dashProgress" class="mb-16" style="display:none"></div>
            <div class="grid-2 mb-16">
                <div class="card"><h2>${icon('calendar')} ${t('Календарь бэкапов')}</h2><div id="dashCal"></div></div>
                <div class="card"><h2>${icon('clock')} ${t('Очередь ожидания')}</h2><div id="dashQueue"></div></div>
            </div>
            <div class="grid-2">
                <div class="card"><h2>${icon('archive')} ${t('Последние бэкапы')}</h2><div id="dashRecentBackups"></div></div>
                <div class="card"><h2>${icon('activity')} ${t('Последние события')}</h2><div id="dashRecentLogs"></div></div>
            </div>`;
        document.getElementById('dashBackupBtn').onclick = () => this.run();
        document.getElementById('dashThemeBtn').onclick = () => this.toggleTheme();
        document.getElementById('dashRefreshBtn').onclick = () => this.refresh();
        c.addEventListener('page:show', () => this.refresh());
        calendarWidget.render('dashCal');
    },
    async refresh() {
        // Hide backup progress if still visible
        const pEl = document.getElementById('dashProgress');
        if (pEl) { pEl.style.display = 'none'; pEl.innerHTML = ''; }
        // Update theme toggle icon to match current theme
        const thBtn = document.getElementById('dashThemeBtn');
        if (thBtn) thBtn.innerHTML = _theme === 'light' ? icon('moon') : icon('sun');
        try { await Promise.all([state.refreshDashboard(), state.refreshSchedules()]); } catch(e) {}
        const data = state.get('dashboard') || {};
        const sr = data.successRate || 0;
        document.getElementById('dashStats').innerHTML = `
            <div class="stat-card ${sr >= 90 ? 'status-ok' : sr >= 50 ? 'status-warn' : 'status-error'}">
                <div style="font-size:24px;margin-bottom:6px">${icon('archive')}</div>
                <div class="stat-value">${data.totalBackups || 0}</div><div class="stat-label">${t('Всего бэкапов')}</div></div>
            <div class="stat-card ${sr >= 90 ? 'status-ok' : 'status-warn'}">
                <div style="font-size:24px;margin-bottom:6px">${icon('shield')}</div>
                <div class="stat-value">${sr.toFixed(0)}%</div><div class="stat-label">${t('Успешных')}</div></div>
            <div class="stat-card">
                <div style="font-size:24px;margin-bottom:6px">${icon('clock')}</div>
                <div class="stat-value">${data.nextBackup ? new Date(data.nextBackup).toLocaleString(_lang) : '--'}</div>
                <div class="stat-label">${t('Следующий бэкап')}</div></div>
            <div class="stat-card">
                <div style="font-size:24px;margin-bottom:6px">${icon('folder')}</div>
                <div class="stat-value">${data.activeSources || 0}</div><div class="stat-label">${t('Активных источников')}</div></div>
            <div class="stat-card ${(data.offlineTargets||0)===0?'status-ok':'status-warn'}">
                <div style="font-size:24px;margin-bottom:6px">${icon('server')}</div>
                <div class="stat-value">${data.activeTargets||0} / ${(data.activeTargets||0)+(data.offlineTargets||0)}</div>
                <div class="stat-label">${t('ПК онлайн / всего')}</div></div>`;
        const logs = (data.recentLogs||[]).slice(0,8).map(l =>
            `<div class="log-entry"><span class="time">${new Date(l.timestamp).toLocaleTimeString(_lang)}</span><span class="level level-${(l.level||'info').toLowerCase()}">${l.level}</span><span class="message">${esc(l.message)}</span></div>`
        ).join('') || `<div class="empty-state"><p>${t('Нет событий')}</p></div>`;
        document.getElementById('dashRecentLogs').innerHTML = `<div class="log-container">${logs}</div>`;
            const bkups = (data.recentBackups||[]);
        const sources = state.get('sources') || [];
        const targets = state.get('targets') || [];
        if (bkups.length) {
            document.getElementById('dashRecentBackups').innerHTML = `<div class="backup-list">${bkups.slice(0,6).map(b => {
                const src = sources.find(s => s.id === b.sourceId);
                const statusNames = ['Pending','InProgress','Completed','Failed','PartiallyFailed'];
                const st = statusNames[b.status] || b.status || 'Completed';
                const ok = st === 'Completed';
                const failed = st === 'Failed' || st === 'PartiallyFailed';
                const dur = b.completedAt && b.startedAt ? Math.round((new Date(b.completedAt) - new Date(b.startedAt)) / 1000) : 0;
                const durStr = dur ? (dur >= 60 ? `${Math.floor(dur/60)}м ${dur%60}с` : `${dur}с`) : '';
                return `<div class="backup-item">
                    <div class="backup-icon ${ok?'icon-ok':failed?'icon-err':'icon-warn'}">${ok?icon('check'):failed?icon('trash'):icon('clock')}</div>
                    <div class="backup-info">
                        <div class="backup-name">${esc(src?.name||b.archivePath.split('\\').pop()||t('Источник'))}</div>
                        <div class="backup-meta">${formatSize(b.archiveSize||0)}${durStr?' &middot; '+durStr:''} &middot; ${new Date(b.completedAt||b.startedAt).toLocaleString(_lang)}</div>
                    </div>
                    <div class="backup-status">${badge(ok?t('Успешно'):failed?t('Ошибка'):t('В процессе'),ok?'success':failed?'error':'warning')}</div>
                </div>`;
            }).join('')}</div>`;
        } else {
            document.getElementById('dashRecentBackups').innerHTML = `<div class="empty-state"><p>${t('История бэкапов пуста')}</p></div>`;
        }
        const schedules = state.get('schedules') || [];
        const allDates = schedules.flatMap(s => computeScheduleDates(s));
        if (allDates.length) { calendarWidget.setScheduledDates(allDates); }
        if (data.backupDates) { calendarWidget.setBackupDates(data.backupDates); }
        calendarWidget.draw();
        try {
            const p = await api.call('getPendingTransfers');
            const q = document.getElementById('dashQueue');
            if (!p||!p.length) { q.innerHTML = `<div class="empty-state"><p>${t('Нет ожидающих передач')}</p></div>`; }
            else {
                q.innerHTML = `<div class="queue-list">${p.map(x => {
                    const src = sources.find(s => s.id === x.sourceId);
                    const tgt = targets.find(t => t.id === x.targetId);
                    const queued = new Date(x.queuedAt);
                    const elapsed = Math.round((Date.now() - queued) / 60000);
                    const elapsedStr = elapsed < 1 ? t('Только что') : elapsed < 60 ? `${elapsed}${t('мин')}` : `${Math.floor(elapsed/60)}${t('ч')}`;
                    return `<div class="queue-item">
                        <div class="queue-icon">${icon('upload')}</div>
                        <div class="queue-info">
                            <div class="queue-name">${esc(src?.name||x.archiveName)}</div>
                            <div class="queue-meta">${t('На')} ${esc(tgt?.name||t('Целевой ПК'))} &middot; ${t('В очереди:')} ${elapsedStr}</div>
                            ${x.lastError?`<div class="queue-error">${icon('trash')} ${esc(x.lastError)}</div>`:''}
                        </div>
                        <div class="queue-status">${badge(t('Попыток:')+' '+x.retryCount,'warning')}</div>
                    </div>`;
                }).join('')}</div>`;
            }
        } catch(e) {}
    },
    async run() {
        const sources = state.get('sources') || [];
        const enabled = sources.filter(s => s.isEnabled);
        if (!enabled.length) { showToast(t('Нет активных источников'),t('Добавьте источники в настройках')); return; }
        for (const s of enabled) {
            showBackupProgress({ sourceName: s.name, percentage: 0, phase: t('Запуск...') });
            try { await api.call('startBackup', { sourceId: s.id }); showToast(t('Бэкап запущен'), s.name); } catch(e) { showToast(t('Ошибка'), e.message); }
        }
    },
    toggleTheme() {
        const newTheme = _theme === 'light' ? 'dark' : 'light';
        applyTheme(newTheme);
        const btn = document.getElementById('dashThemeBtn');
        if (btn) btn.innerHTML = newTheme === 'light' ? icon('moon') : icon('sun');
        showToast(t('Тема'), newTheme === 'light' ? t('Светлая') : t('Тёмная'));
    }
};

// ----- SourceManager -----
const sourceManager = {
    init(containerId) {
        const c = document.getElementById(containerId);
        c.innerHTML = `
            <div class="flex-between mb-16"><h1>${t('Источники бэкапа')}</h1><button class="btn btn-primary" onclick="App.sourceManager.showDialog(null)">${icon('add')} ${t('Добавить источник')}</button></div>
            <div id="sourceList" class="item-list"></div>`;
        c.addEventListener('page:show', () => this.refresh());
        state.onChange('sources', () => this.renderList());
    },
    async refresh() { await state.refreshSources(); },
    renderList() {
        const sources = state.get('sources') || [];
        const container = document.getElementById('sourceList');
        if (!sources.length) { container.innerHTML = `<div class="empty-state"><div class="icon">${icon('folder')}</div><p>${t('Нет источников. Нажмите "Добавить источник"')}</p></div>`; return; }
        container.innerHTML = sources.map(s => `<div class="item-card">
            <div class="item-info">
                <div class="item-title">${esc(s.name)} ${badge(s.isEnabled?t('Активен'):t('Отключён'),s.isEnabled?'success':'warning')}</div>
                <div class="item-subtitle">${esc(s.path)} &mdash; ${t('Сжатие')}: ${s.compressionLevel||6} &mdash; ${t('Исключений')}: ${(s.excludePatterns||[]).length}</div>
            </div>
            <div class="item-actions">
                    <button class="btn btn-ghost btn-sm" onclick="App.sourceManager.estimate('${s.id}')" title="${t('Оценить размер')}">${icon('hardDrive')} ${t('Оценка')}</button>
                <button class="btn btn-ghost btn-sm" onclick="App.sourceManager.verify('${s.id}')" title="${t('Проверить SHA256')}">${icon('shield')} SHA256</button>
                <button class="btn btn-ghost btn-sm" onclick="App.sourceManager.edit('${s.id}')" title="${t('Редактировать')}">${icon('edit')}</button>
                <button class="btn btn-error btn-sm" onclick="App.sourceManager.remove('${s.id}')" title="${t('Удалить')}">${icon('trash')}</button>
            </div></div>`).join('');
    },
    async estimate(id) {
        const s = (state.get('sources')||[]).find(x=>x.id===id);
        if (!s||!s.path) return;
        try { const r = await api.call('estimateSize',{path:s.path,patterns:s.excludePatterns||[]}); if(r) showToast(t('Оценка'), `${t('Файлов:')} ${r.fileCount}\n${t('Объём:')} ${r.totalSizeFormatted}`); } catch(e) { showToast(t('Ошибка'), e.message); }
    },
    async verify(id) {
        const s = (state.get('sources')||[]).find(x=>x.id===id); if(!s) return;
        const cfg = state.get('settings'); const lp = cfg?.localStorage?.path||'C:\\Backups\\Local';
        try {
            const files = await api.call('findLocalBackups',{path:lp,pattern:`Backup_${s.name}_*.zip`});
            if(!files||!files.length){showToast('SHA256',t('Нет локальных бэкапов'));return;}
            const r = await api.call('verifyIntegrity',{path:files[files.length-1]});
            if(r?.hash) showToast('SHA256', r.hash);
        } catch(e) { showToast(t('Ошибка'), e.message); }
    },
    showDialog(source) {
        const isEdit = !!source;
        const ov = showModal(`
            <h2>${isEdit?t('Редактировать источник'):t('Добавить источник')}</h2>
            <div class="form-group"><label>${t('Название')}</label><input id="srcName" value="${isEdit?esc(source.name):''}" placeholder="${t('Например: Документы')}"></div>
            <div class="form-group"><label>${t('Путь к папке')}</label><div class="flex flex-gap"><input id="srcPath" value="${isEdit?esc(source.path):''}" placeholder="C:\\Users\\..." style="flex:1"><button class="btn btn-ghost" id="srcBrowseBtn">${icon('folder')} ${t('Обзор')}</button></div></div>
            <div class="form-group"><label>${t('Маски исключений')}</label><textarea id="srcExcludes" placeholder="*.tmp&#10;node_modules/">${isEdit?(source.excludePatterns||[]).join('\n'):''}</textarea></div>
            <div class="form-group"><label>${t('Сжатие (0-9)')}</label><input id="srcCompression" type="number" min="0" max="9" value="${isEdit?source.compressionLevel||6:6}"></div>
            <div class="form-group"><label>${t('Пароль AES-256')}</label><input id="srcPassword" type="password" value="${isEdit?(source.password||''):''}" placeholder="${t('Оставьте пустым если не нужно')}"></div>
            <div class="form-group"><label><input type="checkbox" id="srcEnabled" ${isEdit&&!source.isEnabled?'':'checked'}> ${t('Включено')}</label></div>
            <div class="modal-actions">${btn(t('Отмена'),'btn-ghost','id="cancelBtn"')}${btn(isEdit?t('Сохранить'):t('Добавить'),'btn-primary','id="saveBtn"')}</div>`);
        const q = (sel) => ov.querySelector(sel);
        q('#srcBrowseBtn').onclick=async()=>{ try { const r=await api.pickFolder(); if(r?.path) q('#srcPath').value=r.path; } catch(e) { showToast(t('Ошибка'), e.message); } };
        q('#saveBtn').onclick=async()=>{
            try {
                const name=q('#srcName').value.trim(), path=q('#srcPath').value.trim();
                const excludes=q('#srcExcludes').value.split('\n').map(s=>s.trim()).filter(Boolean);
                const compression=parseInt(q('#srcCompression').value)||6;
                const password=q('#srcPassword').value, enabled=q('#srcEnabled').checked;
                if(!name||!path){showToast(t('Ошибка'),t('Заполните название и путь'));return;}
                const data={name,path,excludePatterns:excludes,compressionLevel:compression,isEnabled:enabled,password:password||null};
                if(isEdit) data.id=source.id;
                await api.call(isEdit?'updateSource':'addSource',data);
                ov.remove(); await this.refresh();
            } catch(e) { showToast(t('Ошибка'), e.message); }
        };
    },
    edit(id) { const s = (state.get('sources')||[]).find(x=>x.id===id); if(s) this.showDialog(s); },
    async remove(id) { if(!confirm(t('Удалить источник?'))) return; await api.call('removeSource',{id}); await this.refresh(); }
};

// ----- ScheduleEditor -----
const scheduleEditor = {
    init(containerId) {
        const c = document.getElementById(containerId);
        c.innerHTML = `<div class="flex-between mb-16"><h1>${t('Расписание бэкапов')}</h1></div>
            <div class="grid-2"><div id="scheduleContent"></div><div class="card"><h2>${icon('calendar')} ${t('Календарь')}</h2><div id="schCal"></div></div></div>`;
        c.addEventListener('page:show',()=>{
            calendarWidget.render('schCal');
            this.refresh();
        });
    },
    async refresh() {
        try {
            await Promise.all([state.refreshSources(),state.refreshSchedules()]);
        } catch(e) {}
        this.renderContent();
    },
    renderContent() {
        try {
            const sources=state.get('sources')||[], schedules=state.get('schedules')||[];
            const c=document.getElementById('scheduleContent');
            if (!c) return;
            if (!sources.length) { c.innerHTML=`<div class="empty-state"><div class="icon">${icon('clock')}</div><p>${t('Сначала добавьте источники')}</p></div>`; return; }
            const typeNames=['Daily','Weekly','Monthly','Interval','OnChange','Smart'];
            const labels = scheduleLabels[_lang] || scheduleLabels.ru;
            c.innerHTML=`<div class="card"><p class="mb-16" style="color:var(--text-secondary)">${t('Настройте расписание для каждого источника.')}</p>${sources.map(src=>{
                const sch=schedules.find(s=>s.sourceId===src.id);
                const typeStr = sch ? (typeNames[sch.type]||sch.type) : null;
                const typeLabel = typeStr ? (labels[typeStr]||t('Не задано')) : t('Не настроено');
                const days = sch?.activeDays?.length ? sch.activeDays.map(d=>(_lang==='en'?daysFull_en:daysFull_ru)[d]).join(', ') : '';
                const info = [];
                if (sch) {
                    if (sch.timeOfDay) info.push(t('Время:')+' '+sch.timeOfDay.substring(0,5));
                    if (sch.intervalHours) info.push(t('Интервал:')+' '+sch.intervalHours+(_lang==='en'?'h':'ч'));
                    if (days) info.push(t('Дни:')+' '+days);
                    if (sch.skipIfNoChanges) info.push(t('Без изменений'));
                } else {
                    info.push(t('Время:')+' --');
                }
                info.push(t('Сжатие')+': '+(src.compressionLevel||6));
                return `<div class="item-card">
                    <div class="item-info">
                        <div class="item-title">${esc(src.name)} ${badge(typeLabel,sch?'info':'warning')}</div>
                        <div class="item-subtitle">${info.join(' | ')}</div>
                    </div>
                    <div class="item-actions">${sch?.useSmartCalculation?badge(t('Умный'),'info'):''}
                    ${btn(t('Настроить'),'btn-ghost btn-sm',`onclick="App.scheduleEditor.edit('${src.id}')"`)}</div>
                </div>`;
            }).join('')}</div>`;
            const backupDates = schedules.flatMap(s => computeScheduleDates(s));
            if (backupDates.length) { calendarWidget.setScheduledDates(backupDates); calendarWidget.draw(); }
        } catch(e) {
            const c=document.getElementById('scheduleContent');
            if (c) c.innerHTML=`<div class="empty-state"><p>${t('Ошибка загрузки')}</p></div>`;
        }
    },
    async edit(sourceId) {
        const sources=state.get('sources')||[], schedules=state.get('schedules')||[];
        const source=sources.find(s=>s.id===sourceId), current=schedules.find(s=>s.sourceId===sourceId);
        const typeNames=['Daily','Weekly','Monthly','Interval','OnChange','Smart'];
        const currentType = current ? (typeNames[current.type]||current.type||'Smart') : 'Smart';
        const now=current?.timeOfDay?current.timeOfDay.substring(0,5):'02:00';
        const activeDays=current?.activeDays||[1,2,3,4,5];
        const dayNames=_lang==='en'?daysFull_en:daysFull_ru;
        const dayCheckboxes=dayNames.map((n,i)=>`<label class="day-pill"><input type="checkbox" class="sch-day" value="${i}" ${activeDays.includes(i)?'checked':''}> ${n}</label>`).join(' ');
        const labels = scheduleLabels[_lang] || scheduleLabels.ru;
        const ov=showModal(`
            <h2>${t('Расписание:')} ${esc(source?.name||t('Источник'))}</h2>
            <div class="form-group"><label>${t('Тип')}</label><select id="schType">
                <option value="Interval" ${currentType==='Interval'?'selected':''}>${labels.Interval}</option>
                <option value="Daily" ${currentType==='Daily'?'selected':''}>${labels.Daily}</option>
                <option value="Weekly" ${currentType==='Weekly'?'selected':''}>${labels.Weekly}</option>
                <option value="Smart" ${currentType==='Smart'?'selected':''}>${labels.Smart}</option>
                <option value="OnChange" ${currentType==='OnChange'?'selected':''}>${labels.OnChange}</option></select></div>
            <div id="schIntervalFields" class="form-group"><label>${t('Интервал (часы)')}</label><input id="schInterval" type="number" min="1" value="${current?.intervalHours||4}"></div>
            <div class="form-group"><label>${t('Время запуска')}</label><input id="schTime" type="time" value="${now}"></div>
            <div class="form-group"><label>${t('Дни недели')}</label><div class="day-pills">${dayCheckboxes}</div></div>
            <div class="form-group"><label><input type="checkbox" id="schSmart" ${current?.useSmartCalculation?'checked':''}> ${t('Умный расчёт')}</label></div>
            <div class="form-group"><label><input type="checkbox" id="schSkip" ${current?.skipIfNoChanges!==false?'checked':''}> ${t('Пропускать без изменений')}</label></div>
            ${btn(icon('activity')+' '+t('Анализировать'),'btn-ghost','id="analyzeBtn"')}
            <div class="modal-actions">${btn(t('Отмена'),'btn-ghost','id="cancelBtn"')}${btn(t('Сохранить'),'btn-primary','id="saveBtn"')}</div>`);
        const q=(sel)=>ov.querySelector(sel);
        q('#schType').onchange=()=>{
            const v=q('#schType').value;
            q('#schIntervalFields').style.display=v==='Interval'?'block':'none';
        }; q('#schType').dispatchEvent(new Event('change'));
        q('#analyzeBtn').onclick=async()=>{
            if(!source?.path){showToast(t('Ошибка'),t('Путь не указан'));return;}
            try{const r=await api.call('calculateSchedule',{path:source.path});if(r){const typeNames=['Daily','Weekly','Monthly','Interval','OnChange','Smart'];q('#schInterval').value=r.intervalHours||4;q('#schType').value=typeNames[r.type]||r.type||'Smart';q('#schType').dispatchEvent(new Event('change'));showToast(t('Анализ'),`${t('Рекомендуемый интервал:')} ${r.intervalHours||4} ${t('ч')}`);}}catch(e){showToast(t('Ошибка'),e.message);}
        };
        q('#saveBtn').onclick=async()=>{
            try{
                const type=q('#schType').value, intervalHours=parseInt(q('#schInterval').value)||4;
                const timeOfDay=q('#schTime').value+':00', smart=q('#schSmart').checked;
                const skip=q('#schSkip').checked;
                const days=[...ov.querySelectorAll('.sch-day:checked')].map(cb=>parseInt(cb.value));
                await api.call('saveSchedule',{sourceId,type,intervalHours:type==='Interval'?intervalHours:null,timeOfDay,useSmartCalculation:smart,skipIfNoChanges:skip,activeDays:days.length?days:[1,2,3,4,5]});
                ov.remove(); await this.refresh();
            } catch(e) { showToast(t('Ошибка'), e.message); }
        };
    }
};

// ----- TargetManager -----
const targetManager = {
    init(containerId) {
        const c=document.getElementById(containerId);
        c.innerHTML=`<div class="flex-between mb-16"><h1>${t('Целевые ПК')}</h1><button class="btn btn-primary" onclick="App.targetManager.showDialog(null)">${icon('add')} ${t('Добавить ПК')}</button></div><div id="targetList" class="item-list"></div>`;
        c.addEventListener('page:show',()=>this.refresh());
        state.onChange('targets',()=>this.renderList());
    },
    async refresh(){await state.refreshTargets();},
    renderList(){
        const targets=state.get('targets')||[], c=document.getElementById('targetList');
        if(!targets.length){c.innerHTML=`<div class="empty-state"><div class="icon">${icon('server')}</div><p>${t('Нет целевых ПК')}</p></div>`;return;}
        c.innerHTML=targets.map(p=>`<div class="item-card">
            <div class="item-info"><div class="item-title">${esc(p.name)} ${badge(p.isEnabled?t('Активен'):t('Отключён'),p.isEnabled?'success':'warning')}</div>
                <div class="item-subtitle">${esc(p.hostOrIp)} &mdash; ${esc(p.networkPath)} &mdash; ${t('Хранить:')} ${p.retention?.keepLastN||'--'} ${t('копий /')} ${p.retention?.keepDays||'--'} ${t('дн.')}</div></div>
            <div class="item-actions">
                <button class="btn btn-ghost btn-sm" onclick="App.targetManager.check('${p.id}')" title="${t('Проверить доступность')}">${icon('wifi')} ${t('Пинг')}</button>
                <button class="btn btn-ghost btn-sm" onclick="App.targetManager.edit('${p.id}')" title="${t('Редактировать')}">${icon('edit')}</button>
                <button class="btn btn-error btn-sm" onclick="App.targetManager.remove('${p.id}')" title="${t('Удалить')}">${icon('trash')}</button>
            </div></div>`).join('');
    },
    async check(id){try{const r=await api.call('checkTarget',{id});showToast(t('Проверка'),r?t('ПК доступен'):t('ПК недоступен'));}catch(e){showToast(t('Ошибка'),e.message);}},
    showDialog(target){this.dialog(target);},
    edit(id){const target=(state.get('targets')||[]).find(x=>x.id===id);if(target)this.dialog(target);},
    async dialog(target){
        const isEdit=!!target;
        const ov=showModal(`
            <h2>${isEdit?t('Редактировать ПК'):t('Добавить целевой ПК')}</h2>
            <div class="form-group"><label>${t('Название')}</label><input id="tgtName" value="${isEdit?esc(target.name):''}" placeholder="${t('Сервер-1')}"></div>
            <div class="form-group"><label>${t('IP или сетевое имя')}</label><input id="tgtHost" value="${isEdit?esc(target.hostOrIp):''}" placeholder="192.168.1.10"></div>
            <div class="form-group"><label>${t('Сетевой путь')}</label><input id="tgtPath" value="${isEdit?esc(target.networkPath):''}" placeholder="\\\\192.168.1.10\\Backups"></div>
            <div class="form-group"><label><input type="checkbox" id="tgtCreds" ${isEdit&&target.useCustomCredentials?'checked':''}> ${icon('shield')} ${t('Другие учётные данные')}</label></div>
            <div id="tgtCredFields" class="${isEdit&&target.useCustomCredentials?'':'hidden'}">
                <div class="form-group"><label>${t('Пользователь')}</label><input id="tgtUser" value="${isEdit?esc(target.username||''):''}"></div>
                <div class="form-group"><label>${t('Пароль')}</label><input id="tgtPass" type="password"></div></div>
            <hr style="border-color:var(--border)"><h3>${t('Политика хранения')}</h3>
            <div class="form-group"><label>${t('Хранить N копий')}</label><input id="tgtKeepN" type="number" min="1" value="${isEdit&&target.retention?.keepLastN?target.retention.keepLastN:'10'}"></div>
            <div class="form-group"><label>${t('Хранить N дней')}</label><input id="tgtKeepDays" type="number" min="1" value="${isEdit&&target.retention?.keepDays?target.retention.keepDays:'30'}"></div>
            <div class="modal-actions">${btn(t('Отмена'),'btn-ghost','id="cancelBtn"')}${btn(isEdit?t('Сохранить'):t('Добавить'),'btn-primary','id="saveBtn"')}</div>`);
        const q=(sel)=>ov.querySelector(sel);
        q('#tgtCreds').onchange=()=>q('#tgtCredFields').classList.toggle('hidden');
        q('#saveBtn').onclick=async()=>{
            try{
                const name=q('#tgtName').value.trim(), host=q('#tgtHost').value.trim();
                const netPath=q('#tgtPath').value.trim(), creds=q('#tgtCreds').checked;
                const user=q('#tgtUser').value.trim(), pass=q('#tgtPass').value;
                const keepN=parseInt(q('#tgtKeepN').value)||null, keepDays=parseInt(q('#tgtKeepDays').value)||null;
                if(!name||!host){showToast(t('Ошибка'),t('Заполните название и IP'));return;}
                const data={name,hostOrIp:host,networkPath:netPath,useCustomCredentials:creds,username:creds?user:null,passwordKey:creds?pass:null,retention:{keepLastN:keepN,keepDays},isEnabled:true};
                if(isEdit) data.id=target.id;
                await api.call(isEdit?'updateTarget':'addTarget',data); ov.remove(); await this.refresh();
            } catch(e) { showToast(t('Ошибка'), e.message); }
        };
    },
    async remove(id){
        const ov=showModal(`
            <h2>${icon('trash')} ${t('Удалить целевой ПК')}</h2>
            <p>${t('Вы уверены, что хотите удалить этот целевой ПК?')}</p>
            <div class="modal-actions">${btn(t('Отмена'),'btn-ghost','id="cancelBtn"')}${btn(t('Удалить'),'btn-error','id="confirmBtn"')}</div>`);
        ov.querySelector('#confirmBtn').onclick=async()=>{
            ov.remove(); await api.call('removeTarget',{id}); await this.refresh();
        };
    }
};

// ----- LogViewer -----
const logViewer = {
    _timer: null,
    init(containerId) {
        const c=document.getElementById(containerId);
        c.innerHTML=`<div class="flex-between mb-16"><h1>${t('Журнал событий')}</h1><div class="flex flex-gap">
            ${btn('CSV','btn-ghost','id="exportCsvBtn"')}${btn('JSON','btn-ghost','id="exportJsonBtn"')}
            ${btn(icon('refresh'),'btn-ghost','id="logRefreshBtn"')}</div></div>
            <div class="filter-bar">
                <select id="logLevelFilter"><option value="">${t('Все уровни')}</option><option value="Info">Info</option><option value="Warning">Warning</option><option value="Error">Error</option></select>
                <input id="logSearch" placeholder="${t('Поиск...')}" style="flex:1;max-width:300px"></div>
            <div class="log-container" id="logEntries"></div>
            <div style="text-align:right;margin-top:6px;font-size:11px;color:var(--text-muted)">${t('Автообновление каждые 5 сек')}</div>`;
        document.getElementById('logLevelFilter').onchange=()=>this.load();
        document.getElementById('logSearch').oninput=()=>this.load();
        document.getElementById('logRefreshBtn').onclick=()=>this.load();
        document.getElementById('exportCsvBtn').onclick=()=>this.export('csv');
        document.getElementById('exportJsonBtn').onclick=()=>this.export('json');
        c.addEventListener('page:show',()=>{this.load();this.startTimer();});
        c.addEventListener('page:hide',()=>this.stopTimer());
    },
    startTimer(){this.stopTimer();this._timer=setInterval(()=>this.load(),5000);},
    stopTimer(){if(this._timer){clearInterval(this._timer);this._timer=null;}},
    async load(){
        try{
            const lvl=document.getElementById('logLevelFilter').value, search=document.getElementById('logSearch').value;
            const logs=await api.call('getLogs',{count:200,level:lvl||null,search:search||null});
            const c=document.getElementById('logEntries');
            if(!logs||!logs.length){c.innerHTML=`<div style="padding:12px;color:var(--text-muted);text-align:center">${t('Нет записей')}</div>`;return;}
            c.innerHTML=logs.map(l=>{
                const levelName = ['Info','Warning','Error'][l.level] || l.level || 'Info';
                return `<div class="log-entry"><span class="time">${new Date(l.timestamp).toLocaleString(_lang)}</span><span class="level level-${levelName.toLowerCase()}">${levelName}</span><span class="message">${esc(l.message)}</span></div>`;
            }).join('');
        }catch(e){}
    },
    async export(fmt){try{const r=await api.call('exportLogs',{format:fmt});if(r?.path)showToast(t('Экспорт'),t('Сохранено:')+' '+r.path);}catch(e){showToast(t('Ошибка'),e.message);}}
};

// ----- SettingsView -----
const settingsView = {
    init(containerId) {
        const c=document.getElementById(containerId);
        c.innerHTML=`<div class="flex-between mb-16"><h1>${t('Настройки')}</h1><div class="flex flex-gap">${btn(icon('download')+' '+t('Экспорт'),'btn-ghost','id="exportCfgBtn"')}${btn(icon('upload')+' '+t('Импорт'),'btn-ghost','id="importCfgBtn"')}</div></div>
            <div class="grid-2">
                <div class="card"><h2>${icon('globe')} ${t('Общие')}</h2>
                    <div class="form-group"><label>${t('Язык')}</label><select id="cfgLang"><option value="ru">${t('Русский')}</option><option value="en">${t('Английский')}</option></select></div>
                    <div class="form-group"><label>${t('Тема')}</label><select id="cfgTheme"><option value="dark">${t('Тёмная')}</option><option value="light">${t('Светлая')}</option></select></div>
                    <div class="form-group"><label><input type="checkbox" id="cfgStartWin"> ${t('Запуск при старте Windows')}</label></div>
                    <div class="form-group"><label><input type="checkbox" id="cfgMinTray"> ${t('Сворачивать в трей')}</label></div>
                    <div class="form-group"><label>${t('Горячая клавиша')}</label><input id="cfgHotkey" value="Ctrl+Shift+B"></div></div>
                <div class="card"><h2>${icon('bell')} ${t('Уведомления')}</h2>
                    <div class="form-group"><label><input type="checkbox" id="cfgToast" checked> ${t('Toast-уведомления')}</label></div>
                    <hr style="border-color:var(--border)"><h3>${icon('globe')} ${t('Email')}</h3>
                    <div class="form-group"><label><input type="checkbox" id="cfgEmailEnabled"> ${t('Включить')}</label></div>
                    <div id="emailFields" class="hidden">
                        <div class="form-group"><label>${t('SMTP сервер')}</label><input id="cfgSmtpHost"></div>
                        <div class="form-group"><label>${t('Порт')}</label><input id="cfgSmtpPort" value="587"></div>
                        <div class="form-group"><label>${t('Пользователь')}</label><input id="cfgSmtpUser"></div>
                        <div class="form-group"><label>${t('Пароль')}</label><input id="cfgSmtpPass" type="password"></div></div></div>
                <div class="card"><h2>${icon('wifi')} ${t('Сеть')}</h2>
                    <div class="form-group"><label>${t('Таймаут (сек)')}</label><input id="cfgTimeout" value="30"></div>
                    <div class="form-group"><label>${t('Макс. параллельных передач')}</label><input id="cfgMaxParallel" type="number" min="1" max="10" value="3"></div>
                    <div class="form-group"><label>${t('Кол-во повторов')}</label><input id="cfgRetryCount" type="number" min="0" value="3"></div>
                    <div class="form-group"><label>${t('Задержка (мс)')}</label><input id="cfgRetryDelay" type="number" value="5000"></div></div>
                <div class="card"><h2>${icon('hardDrive')} ${t('Локальное хранение')}</h2>
                    <div class="form-group"><label><input type="checkbox" id="cfgLocalEnabled" checked> ${t('Сохранять локально')}</label></div>
                    <div class="form-group"><label>${t('Путь')}</label><input id="cfgLocalPath" value="C:\\Backups\\Local"></div>
                    <div class="form-group"><label>${t('Хранить N копий')}</label><input id="cfgLocalKeepN" type="number" value="5"></div>
                    <div class="form-group"><label>${t('Хранить N дней')}</label><input id="cfgLocalKeepDays" type="number" value="30"></div></div></div>
            <div class="mt-16 flex flex-gap">${btn(icon('check')+' '+t('Сохранить настройки'),'btn-primary','id="saveSettingsBtn"')}</div>`;
        document.getElementById('cfgEmailEnabled').onchange=()=>document.getElementById('emailFields').classList.toggle('hidden');
        document.getElementById('saveSettingsBtn').onclick=()=>this.save();
        document.getElementById('exportCfgBtn').onclick=async()=>{try{const r=await api.call('exportConfig');if(r?.path)showToast(t('Экспорт'),t('Сохранено:')+' '+r.path);}catch(e){showToast(t('Ошибка'),e.message);}};
        document.getElementById('importCfgBtn').onclick=async()=>{try{const r=await api.call('importConfig');if(r){await state.refreshAll();this.loadValues();}}catch(e){showToast(t('Ошибка'),e.message);}};

        // Preview theme on change (no reload needed)
        document.getElementById('cfgTheme').onchange=()=>applyTheme(document.getElementById('cfgTheme').value);

        c.addEventListener('page:show',()=>this.loadValues());
    },
    loadValues(){
        const s=state.get('settings');
        if(!s) return;
        const set=(id,val)=>{const el=document.getElementById(id);if(el)el.value=val;};
        const check=(id,val)=>{const el=document.getElementById(id);if(el)el.checked=!!val;};
        set('cfgLang',s.app?.language||'ru'); set('cfgTheme',s.app?.theme||'dark');
        check('cfgStartWin',s.app?.startWithWindows); check('cfgMinTray',s.app?.minimizeToTray!==false);
        set('cfgHotkey',s.app?.hotkey||'Ctrl+Shift+B');
        check('cfgToast',s.notifications?.toast!==false);
        check('cfgEmailEnabled',s.notifications?.email?.enabled);
        document.getElementById('emailFields').classList.toggle('hidden',!s.notifications?.email?.enabled);
        set('cfgSmtpHost',s.notifications?.email?.smtpHost||''); set('cfgSmtpPort',s.notifications?.email?.smtpPort||587);
        set('cfgSmtpUser',s.notifications?.email?.username||''); set('cfgSmtpPass',s.notifications?.email?.passwordKey||'');
        set('cfgTimeout',s.network?.timeoutSeconds||30); set('cfgMaxParallel',s.network?.maxParallelTransfers||3);
        set('cfgRetryCount',s.network?.retryCount||3); set('cfgRetryDelay',s.network?.retryDelayMs||5000);
        check('cfgLocalEnabled',s.localStorage?.enabled!==false);
        set('cfgLocalPath',s.localStorage?.path||'C:\\Backups\\Local');
        set('cfgLocalKeepN',s.localStorage?.retention?.keepLastN||5);
        set('cfgLocalKeepDays',s.localStorage?.retention?.keepDays||30);
    },
    async save(){
        const val=(id)=>document.getElementById(id)?.value||'';
        const chk=(id)=>!!document.getElementById(id)?.checked;
        const num=(id,def)=>parseInt(document.getElementById(id)?.value)||def;
        const settings = state.get('settings')||{};
        const lang=val('cfgLang'), theme=val('cfgTheme');
        settings.app={language:lang,theme:theme,startWithWindows:chk('cfgStartWin'),minimizeToTray:chk('cfgMinTray'),hotkey:val('cfgHotkey')};
        settings.notifications={toast:chk('cfgToast'),email:{enabled:chk('cfgEmailEnabled'),smtpHost:val('cfgSmtpHost'),smtpPort:num('cfgSmtpPort',587),username:val('cfgSmtpUser'),passwordKey:val('cfgSmtpPass'),useSsl:true}};
        settings.network={timeoutSeconds:num('cfgTimeout',30),maxParallelTransfers:num('cfgMaxParallel',3),retryCount:num('cfgRetryCount',3),retryDelayMs:num('cfgRetryDelay',5000)};
        settings.localStorage={enabled:chk('cfgLocalEnabled'),path:val('cfgLocalPath'),retention:{keepLastN:num('cfgLocalKeepN',5)||null,keepDays:num('cfgLocalKeepDays',30)||null}};
        try{
            await api.call('saveSettings',settings);
            await state.refreshSettings();
            applyTheme(theme);
            showToast(t('Настройки'),t('Сохранены'));
            if(lang!==_lang){ setLang(lang); setTimeout(()=>location.reload(), 500); }
        }catch(e){showToast(t('Ошибка'),e.message);}
    }
};

const App = { api, state, router, dashboard, sourceManager, scheduleEditor, targetManager, logViewer, settingsView };

function showBackupProgress(data) {
    const el = document.getElementById('dashProgress');
    if (!el) return;
    el.style.display = 'block';
    el.innerHTML = `<div class="card backup-progress-card">
        <div class="flex-between" style="margin-bottom:8px">
            <div class="progress-title">${icon('archive')} ${esc(data.sourceName||'')}</div>
            <div class="progress-pct">${data.percentage||0}%</div>
        </div>
        <div class="progress-bar"><div class="progress-fill" style="width:${data.percentage||0}%"></div></div>
        <div class="flex-between" style="margin-top:8px">
            <span class="progress-phase">${esc(data.phase||t('Запуск...'))}</span>
            <span class="progress-file">${esc(data.currentFile||'')}</span>
        </div>
    </div>`;
}

// ==================== App Init ====================
(function(){
    function buildLayout(){
        document.getElementById('app').innerHTML=`
        <nav class="sidebar">
            <div class="sidebar-header"><div class="logo">Backup App</div><div class="version">v1.0.0</div></div>
            <div class="nav-items">
                <a class="nav-item active" data-route="dashboard"><span class="nav-icon">${icon('dashboard')}</span><span>${t('Дашборд')}</span></a>
                <a class="nav-item" data-route="sources"><span class="nav-icon">${icon('sources')}</span><span>${t('Источники')}</span></a>
                <a class="nav-item" data-route="schedule"><span class="nav-icon">${icon('schedule')}</span><span>${t('Расписание')}</span></a>
                <a class="nav-item" data-route="targets"><span class="nav-icon">${icon('targets')}</span><span>${t('Целевые ПК')}</span></a>
                <a class="nav-item" data-route="logs"><span class="nav-icon">${icon('logs')}</span><span>${t('Журнал')}</span></a>
                <a class="nav-item" data-route="settings"><span class="nav-icon">${icon('settings')}</span><span>${t('Настройки')}</span></a>
            </div>
            <div class="sidebar-footer">${btn(icon('play')+' '+t('Бэкап сейчас'),'btn-primary btn-block','id="quickBackupBtn"')}</div>
        </nav>
        <main class="main-content">
            <div class="page" id="page-dashboard" style="display:block"></div>
            <div class="page" id="page-sources" style="display:none"></div>
            <div class="page" id="page-schedule" style="display:none"></div>
            <div class="page" id="page-targets" style="display:none"></div>
            <div class="page" id="page-logs" style="display:none"></div>
            <div class="page" id="page-settings" style="display:none"></div>
        </main>`;

        // Inject CSS
        if(!document.getElementById('appCss')){
            const link=document.createElement('link'); link.id='appCss'; link.rel='stylesheet'; link.href='css/main.css';
            document.head.appendChild(link);
        }
        // Toast container
        const tc=document.createElement('div'); tc.id='toast-container';
        document.body.appendChild(tc);
    }

    function init(){
        applyTheme(_theme);
        buildLayout();

        // Init all components
        dashboard.init('page-dashboard');
        sourceManager.init('page-sources');
        scheduleEditor.init('page-schedule');
        targetManager.init('page-targets');
        logViewer.init('page-logs');
        settingsView.init('page-settings');

        // Navigation
        document.querySelectorAll('.nav-item').forEach(el=>{
            el.addEventListener('click',()=>router.navigate(el.dataset.route));
        });
        document.getElementById('quickBackupBtn').addEventListener('click',async()=>{
            const sources=state.get('sources')||[], enabled=sources.filter(s=>s.isEnabled);
            if(!enabled.length){showToast(t('Нет источников'),t('Добавьте источники'));return;}
            for(const s of enabled){try{await api.call('startBackup',{sourceId:s.id});showToast(t('Бэкап'),t('Запущен')+' '+s.name);}catch(e){showToast(t('Ошибка'),e.message);}}
        });

        // Listen for backup completion / error events from C#
        api.on('backupCompleted', (data) => {
            const sources = state.get('sources') || [];
            const src = sources.find(s => s.id === data?.sourceId);
            const statusNames = ['Pending','InProgress','Completed','Failed','PartiallyFailed'];
            const status = statusNames[data?.status] || data?.status || 'Completed';
            if (status === 'Failed' || status === 'PartiallyFailed') {
                showToast(t('Ошибка бэкапа'), src ? `${src.name}: ${data.errorMessage || t('Неизвестная ошибка')}` : t('См. логи'));
            } else {
                showToast(t('Бэкап завершён'), src ? `${t('Источник')} ${src.name}` : t('Успешно'));
            }
            state.refreshAll();
        });
        api.on('backupError', (data) => {
            showToast(t('Ошибка бэкапа'), data?.error || t('Неизвестная ошибка'));
        });
        api.on('backupStarted', (data) => showBackupProgress(data || { sourceName: '', percentage: 0, phase: t('Запуск...') }));
        api.on('backupProgress', (data) => {
            if (data) showBackupProgress(data);
        });

        // Refresh all then navigate to dashboard
        state.refreshAll().then(()=>{
            // Apply backend language/theme if they differ from localStorage
            const s=state.get('settings');
            if(s?.app){
                if(s.app.theme && s.app.theme!==_theme) applyTheme(s.app.theme);
                if(s.app.language && s.app.language!==_lang){ setLang(s.app.language); location.reload(); return; }
            }
            router.navigate('dashboard');
        });
    }

    window.App = App;
    if(document.readyState==='loading') document.addEventListener('DOMContentLoaded',init);
    else init();
})();
