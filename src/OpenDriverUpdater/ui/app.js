// Interfaz de OpenDriverUpdater.
// Se comunica con la parte de C# (MainForm.cs) por chrome.webview: postMessage hacia alla
// y el evento "message" para lo que llega.
(function () {
  'use strict';
  const { Spring, springEasing, rubberBand, VelocityTracker } = window.Motion;
  const $ = (sel, root = document) => root.querySelector(sel);
  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);

  const webview = window.chrome && window.chrome.webview;
  const send = (msg) => webview && webview.postMessage(msg);
  if (webview) webview.addEventListener('message', (e) => handle(e.data));
  window.addEventListener('error', (e) => send({ type: 'jsError', message: `${e.message} (${e.lineno}:${e.colno})` }));

  const state = {
    lang: 'en',
    rows: [],
    byId: new Map(),
    filter: 'all',
    query: '',
    selected: new Set(),
    scanning: true,
    installing: false,
    restorePoint: true,
    warnings: [],
    install: null,
    lastProgress: null,
  };

  // idioma

  function t(key, vars) {
    const dict = window.STRINGS[state.lang] || window.STRINGS.en;
    let s = dict[key] != null ? dict[key] : (window.STRINGS.en[key] != null ? window.STRINGS.en[key] : key);
    if (vars) for (const k in vars) s = s.split('{' + k + '}').join(vars[k]);
    return s;
  }
  const plural = (n, one, many) => t(n === 1 ? one : many, { n });
  const sourceName = (key) => (key ? t('src.' + key) : '');

  function applyStaticTexts() {
    document.documentElement.lang = state.lang;
    document.querySelectorAll('[data-t]').forEach((el) => { el.textContent = t(el.dataset.t); });
    document.querySelectorAll('[data-t-placeholder]').forEach((el) => { el.placeholder = t(el.dataset.tPlaceholder); });
    document.querySelectorAll('[data-t-label]').forEach((el) => { el.setAttribute('aria-label', t(el.dataset.tLabel)); });
    const btn = $('#lang-btn');
    btn.innerHTML = `<span class="flag">${window.FLAGS[state.lang]}</span>`;
    btn.setAttribute('aria-label', `${t('lang')}: ${window.LANGS.find((l) => l.code === state.lang).name}`);
  }

  function setLang(code, notify) {
    if (!window.STRINGS[code]) return;
    state.lang = code;
    applyStaticTexts();
    if (notify) send({ type: 'lang', lang: code });
    // volver a pintar lo que tiene texto generado
    if (state.scanning) { if (state.lastProgress) onScanProgress(state.lastProgress); }
    else if (state.rows.length) { updateCounts(); updateSubtitle(); renderList(false); }
    updateActionBar();
    if (sheet.open && sheet.redraw) sheet.redraw();
    requestAnimationFrame(() => placeThumb(false)); // los textos cambian de ancho
  }

  // curvas de muelle para las transiciones CSS
  (function () {
    const root = document.documentElement.style;
    const smooth = springEasing(0.32, 0.9);
    const bouncy = springEasing(0.36, 0.68);
    root.setProperty('--spring-ease', smooth.easing);
    root.setProperty('--spring-duration', Math.min(smooth.duration, 700) + 'ms');
    root.setProperty('--spring-bouncy-ease', bouncy.easing);
    root.setProperty('--spring-bouncy-duration', Math.min(bouncy.duration, 900) + 'ms');
  })();

  const ICON = {
    check: '<svg viewBox="0 0 20 20"><path d="m4.5 10.5 3.5 3.5 7.5-8"/></svg>',
    arrow: '<svg class="arrow" viewBox="0 0 20 20"><path d="M4 10h12m-4-4 4 4-4 4"/></svg>',
    chevron: '<svg class="chevron" viewBox="0 0 20 20"><path d="m8 5 5 5-5 5"/></svg>',
    x: '<svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="7.5"/><path d="m7.5 7.5 5 5m0-5-5 5"/></svg>',
    ok: '<svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="7.5"/><path d="m6.8 10.2 2.2 2.2 4.2-4.6"/></svg>',
    dash: '<svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="7.5"/><path d="M7 10h6"/></svg>',
    pending: '<svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="7.5" stroke-dasharray="2 3"/></svg>',
    sparkle: '<svg viewBox="0 0 48 48"><path d="M24 6v8m0 20v8M6 24h8m20 0h8M11.3 11.3l5.6 5.6m14.2 14.2 5.6 5.6m0-25.4-5.6 5.6M16.9 31.1l-5.6 5.6"/></svg>',
    search: '<svg viewBox="0 0 48 48"><circle cx="21" cy="21" r="12"/><path d="m30 30 9 9"/></svg>',
    done: '<svg viewBox="0 0 20 20"><path d="m4.5 10.5 3.5 3.5 7.5-8"/></svg>',
    warn: '<svg viewBox="0 0 20 20"><path d="M10 6.5v4.5m0 2.8v.01"/></svg>',
    tick: '<svg class="tick" viewBox="0 0 20 20"><path d="m4.5 10.5 3.5 3.5 7.5-8"/></svg>',
  };

  const KIND_ICONS = {
    display: '<rect x="3" y="4" width="14" height="9.5" rx="1.5"/><path d="M7.5 16.5h5M10 13.5v3"/>',
    net: '<path d="M2.8 8.2a10.2 10.2 0 0 1 14.4 0M5.3 10.8a6.6 6.6 0 0 1 9.4 0M7.8 13.4a3 3 0 0 1 4.4 0"/><path d="M10 16.2h.01"/>',
    audio: '<path d="M3.5 8v4h3l4 3.5v-11l-4 3.5h-3Z"/><path d="M13.5 7.5a3.5 3.5 0 0 1 0 5M15.8 5.4a6.5 6.5 0 0 1 0 9.2"/>',
    bluetooth: '<path d="m6 6.5 8 7-4 3.5V3l4 3.5-8 7"/>',
    input: '<rect x="6" y="2.5" width="8" height="15" rx="4"/><path d="M10 2.5v4.5"/>',
    storage: '<ellipse cx="10" cy="5.5" rx="6" ry="2.5"/><path d="M4 5.5v9c0 1.4 2.7 2.5 6 2.5s6-1.1 6-2.5v-9M4 10c0 1.4 2.7 2.5 6 2.5s6-1.1 6-2.5"/>',
    camera: '<rect x="2.5" y="6" width="11" height="8" rx="1.6"/><path d="m13.5 9 4-2.2v6.4l-4-2.2"/>',
    usb: '<path d="M10 2.5v12M10 17.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3ZM10 11l-4-2.5V6M10 9l4-2.5V5"/><path d="M5 5h2M13 3.5h2v2h-2z"/>',
    chip: '<rect x="5.5" y="5.5" width="9" height="9" rx="1.5"/><path d="M8 3v2.5M12 3v2.5M8 14.5V17M12 14.5V17M3 8h2.5M3 12h2.5M14.5 8H17M14.5 12H17"/>',
  };

  function kindOf(cls) {
    const c = (cls || '').toLowerCase();
    if (c === 'display' || c === 'monitor') return 'display';
    if (c === 'net' || c === 'netservice') return 'net';
    if (c === 'media' || c.includes('audio')) return 'audio';
    if (c === 'bluetooth') return 'bluetooth';
    if (c === 'hidclass' || c === 'keyboard' || c === 'mouse') return 'input';
    if (['diskdrive', 'scsiadapter', 'hdc', 'volume', 'cdrom'].includes(c)) return 'storage';
    if (c === 'camera' || c === 'image') return 'camera';
    if (c === 'usb' || c === 'usbdevice') return 'usb';
    return 'chip';
  }
  const glyph = (cls) => `<span class="glyph"><svg viewBox="0 0 20 20">${KIND_ICONS[kindOf(cls)]}</svg></span>`;

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  function formatSize(bytes) {
    if (!bytes) return '';
    const mb = bytes / (1024 * 1024);
    const dec = state.lang === 'en' ? '.' : ',';
    if (mb >= 1024) return (mb / 1024).toFixed(1).replace('.', dec) + ' GB';
    if (mb >= 1) return mb.toFixed(mb < 10 ? 1 : 0).replace('.', dec) + ' MB';
    return Math.max(1, Math.round(bytes / 1024)) + ' KB';
  }

  function statusText(r) {
    let s;
    if (r.status === 'missing') s = t(r.canUpdate ? 'status.missing' : 'status.missingNone');
    else s = t('status.' + r.status);
    if (r.canUpdate && r.match === 'generic') s += t('status.generic');
    return s;
  }

  function pillClass(r) {
    if (r.canUpdate) return { update: 'update', missing: 'missing', vendor: 'vendor' }[r.status] || 'update';
    if (r.status === 'ok') return 'ok';
    if (r.status === 'missing') return 'missing';
    return 'none';
  }

  const installedText = (r) => r.hasDriver ? r.installedVersion + (r.nvidiaVersion ? ` (${r.nvidiaVersion})` : '') : t('d.noDriver');

  // respuesta al pulsar: la clase se pone en el mismo pointerdown, sin esperar a nada

  let pressed = null;
  function pressStart(el) {
    if (!el || el.disabled) return;
    if (pressed && pressed !== el) pressed.classList.remove('is-pressed');
    pressed = el;
    el.classList.add('is-pressed');
  }
  function pressEnd() {
    if (pressed) pressed.classList.remove('is-pressed');
    pressed = null;
  }
  document.addEventListener('pointerdown', (e) => { if (e.button === 0) pressStart(e.target.closest('[data-press]')); }, true);
  document.addEventListener('pointerup', pressEnd, true);
  document.addEventListener('pointercancel', pressEnd, true);
  document.addEventListener('pointermove', (e) => {
    // si arrastras fuera del boton se cancela, como en macOS
    if (pressed && e.buttons && !pressed.contains(document.elementFromPoint(e.clientX, e.clientY))) pressEnd();
  }, true);
  window.addEventListener('blur', pressEnd);
  document.addEventListener('keydown', (e) => {
    if ((e.key === ' ' || e.key === 'Enter') && e.target.matches && e.target.matches('[data-press]') && !e.repeat) pressStart(e.target);
  });
  document.addEventListener('keyup', pressEnd);

  // selector de idioma

  const langMenu = $('#lang-menu');
  const langBtn = $('#lang-btn');
  const menuAnim = new Spring({
    response: 0.3, dampingFraction: 0.82, precision: 0.002,
    onUpdate: (v) => {
      langMenu.style.opacity = String(clamp(v, 0, 1));
      langMenu.style.transform = `scale(${0.92 + 0.08 * v})`;
    },
    onRest: (v) => { if (v === 0) langMenu.hidden = true; },
  });
  let menuOpen = false;

  function buildLangMenu() {
    langMenu.innerHTML = `<div class="lang-menu-title">${esc(t('lang'))}</div>` + window.LANGS.map((l) =>
      `<button class="lang-item" role="menuitemradio" aria-checked="${l.code === state.lang}" data-lang="${l.code}" data-press>
        <span class="flag">${window.FLAGS[l.code]}</span><span class="name">${esc(l.name)}</span>${ICON.tick}
      </button>`).join('');
  }
  function openLangMenu() {
    buildLangMenu();
    menuOpen = true;
    langMenu.hidden = false;
    langBtn.setAttribute('aria-expanded', 'true');
    menuAnim.to(1);
    const current = langMenu.querySelector('[aria-checked="true"]');
    current && current.focus({ preventScroll: true });
  }
  function closeLangMenu(focusBtn) {
    if (!menuOpen) return;
    menuOpen = false;
    langBtn.setAttribute('aria-expanded', 'false');
    menuAnim.to(0); // si estaba abriendose se da la vuelta desde donde este
    if (focusBtn) langBtn.focus({ preventScroll: true });
  }
  langBtn.addEventListener('pointerdown', (e) => { if (e.button === 0) { e.preventDefault(); menuOpen ? closeLangMenu() : openLangMenu(); } });
  langBtn.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown') { e.preventDefault(); openLangMenu(); } });
  langMenu.addEventListener('click', (e) => {
    const item = e.target.closest('[data-lang]');
    if (!item) return;
    setLang(item.dataset.lang, true);
    langMenu.querySelectorAll('[data-lang]').forEach((b) => b.setAttribute('aria-checked', String(b.dataset.lang === state.lang)));
    closeLangMenu(true);
  });
  langMenu.addEventListener('keydown', (e) => {
    const items = [...langMenu.querySelectorAll('[data-lang]')];
    const i = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      items[clamp(i + (e.key === 'ArrowDown' ? 1 : -1), 0, items.length - 1)].focus();
    }
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); closeLangMenu(true); }
  });
  document.addEventListener('pointerdown', (e) => { if (menuOpen && !e.target.closest('#lang')) closeLangMenu(); });

  // barra superior + rebote al llegar a los bordes de la lista

  const scroller = $('#scroller');
  const toolbar = $('#toolbar');
  scroller.addEventListener('scroll', () => toolbar.classList.toggle('is-scrolled', scroller.scrollTop > 2), { passive: true });

  const content = $('#scroll-content');
  const over = new Spring({
    response: 0.42, dampingFraction: 1, precision: 0.2,
    onUpdate: (y) => { content.style.transform = Math.abs(y) < 0.2 ? '' : `translateY(${y}px)`; },
  });
  let overRaw = 0, overTimer = 0;
  scroller.addEventListener('wheel', (e) => {
    if (e.ctrlKey) return;
    const dy = e.deltaY * (e.deltaMode === 1 ? 18 : e.deltaMode === 2 ? scroller.clientHeight : 1);
    const max = scroller.scrollHeight - scroller.clientHeight;
    const atTop = scroller.scrollTop <= 0;
    const atBottom = scroller.scrollTop >= max - 1;
    const pullTop = overRaw > 0 || (overRaw === 0 && atTop && dy < 0);
    const pullBottom = overRaw < 0 || (overRaw === 0 && atBottom && dy > 0);
    if (!pullTop && !pullBottom) return;
    e.preventDefault();
    overRaw = clamp(overRaw - dy, pullTop ? 0 : -800, pullTop ? 800 : 0);
    over.track(rubberBand(overRaw, scroller.clientHeight, 0.55) * 0.34);
    clearTimeout(overTimer);
    overTimer = setTimeout(() => { overRaw = 0; over.to(0); }, 120);
  }, { passive: false });

  // filtro segmentado (cambia al presionar, no al soltar)

  const segmented = $('#segmented');
  const thumb = $('#thumb');
  const thumbX = new Spring({ response: 0.34, dampingFraction: 0.78, precision: 0.1, onUpdate: (x) => { thumb.style.transform = `translateX(${x}px)`; } });
  const thumbW = new Spring({ response: 0.34, dampingFraction: 0.86, precision: 0.1, onUpdate: (w) => { thumb.style.width = `${w}px`; } });

  function placeThumb(animated) {
    const b = segmented.querySelector('[aria-selected="true"]');
    if (!b || !b.offsetWidth) return;
    if (animated) { thumbX.to(b.offsetLeft); thumbW.to(b.offsetWidth); }
    else { thumbX.jump(b.offsetLeft); thumbW.jump(b.offsetWidth); }
  }
  function setFilter(filter, animated = true) {
    if (state.filter === filter) return;
    state.filter = filter;
    segmented.querySelectorAll('button').forEach((b) => b.setAttribute('aria-selected', String(b.dataset.filter === filter)));
    placeThumb(animated);
    renderList(false);
    scroller.scrollTop = 0;
  }
  segmented.addEventListener('pointerdown', (e) => {
    const b = e.target.closest('button[data-filter]');
    if (b && e.button === 0) setFilter(b.dataset.filter);
  });
  segmented.addEventListener('click', (e) => {
    const b = e.target.closest('button[data-filter]');
    if (b) setFilter(b.dataset.filter);
  });
  segmented.addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
    const btns = [...segmented.querySelectorAll('button')];
    const i = btns.findIndex((b) => b.dataset.filter === state.filter);
    const next = btns[clamp(i + (e.key === 'ArrowRight' ? 1 : -1), 0, btns.length - 1)];
    next.focus();
    setFilter(next.dataset.filter);
  });
  new ResizeObserver(() => placeThumb(thumbX.isAnimating)).observe(segmented);

  const search = $('#search');
  search.addEventListener('input', () => { state.query = search.value.trim().toLowerCase(); renderList(false); });

  // lista

  const list = $('#list');

  function matchesFilter(r) {
    if (state.filter === 'updates') return r.canUpdate;
    if (state.filter === 'ok') return r.status === 'ok';
    if (state.filter === 'none') return !r.canUpdate && r.status !== 'ok';
    return true;
  }
  function matchesQuery(r) {
    if (!state.query) return true;
    return [r.name, r.deviceClass, r.provider, r.installedVersion, r.latestVersion, statusText(r), sourceName(r.sourceKey)]
      .some((v) => v && String(v).toLowerCase().includes(state.query));
  }

  function rowHtml(r) {
    const selected = state.selected.has(r.id);
    const check = r.canUpdate
      ? `<button class="check" role="checkbox" aria-checked="${selected}" aria-label="${esc(r.name)}" data-check="${r.id}" data-press>${ICON.check}</button>`
      : '<span class="check-spacer"></span>';
    const installed = installedText(r);
    // algunos paquetes no traen numero de version: entonces se enseña la fecha
    const latest = r.latestVersion || r.latestDate;
    const versions = r.canUpdate && latest
      ? `<span class="v v-installed">${esc(installed)}</span>${ICON.arrow}<span class="v v-latest">${esc(latest)}</span>`
      : `<span class="v v-installed">${esc(installed)}</span>`;
    const sub = [r.deviceClass, r.provider].filter(Boolean).map(esc).join(' · ');
    const subVersions = r.canUpdate && latest ? ` · ${esc(installed)} → ${esc(latest)}` : ` · ${esc(installed)}`;
    return `<div class="row${selected ? ' is-selected' : ''}" role="button" tabindex="0" data-id="${r.id}" data-press>
      ${check}
      ${glyph(r.deviceClass)}
      <span class="row-main">
        <span class="row-name">${esc(r.name)}</span>
        <span class="row-sub">${sub || '&nbsp;'}<span class="sub-versions">${subVersions}</span></span>
      </span>
      <span class="versions">${versions}</span>
      <span class="pill ${pillClass(r)}" title="${esc(statusText(r))}">${esc(t('pill.' + r.status))}</span>
      ${ICON.chevron}
    </div>`;
  }

  function emptyHtml(icon, title, text) {
    return `<div class="empty">${icon}<h3>${esc(title)}</h3><p>${esc(text)}</p></div>`;
  }

  function renderList(entering) {
    const rows = state.rows.filter((r) => matchesFilter(r) && matchesQuery(r));
    let html = '';

    if (!rows.length) {
      if (state.query) html = emptyHtml(ICON.search, t('empty.search.title'), t('empty.search.text', { q: search.value }));
      else if (state.filter === 'updates') html = emptyHtml(ICON.sparkle, t('empty.good.title'), t('empty.good.text'));
      else html = emptyHtml(ICON.search, t('empty.none.title'), t('empty.none.text'));
    } else if (state.filter === 'all') {
      const groups = [
        ['section.updates', rows.filter((r) => r.canUpdate)],
        ['section.ok', rows.filter((r) => r.status === 'ok')],
        ['section.none', rows.filter((r) => !r.canUpdate && r.status !== 'ok')],
      ];
      for (const [title, items] of groups) {
        if (!items.length) continue;
        html += `<section class="section"><div class="section-header"><span>${esc(t(title))}</span><span>${items.length}</span></div><div class="group">${items.map(rowHtml).join('')}</div></section>`;
      }
    } else {
      html = `<section class="section"><div class="group">${rows.map(rowHtml).join('')}</div></section>`;
    }

    list.innerHTML = html;
    if (entering) {
      list.classList.add('is-entering');
      list.querySelectorAll('.row').forEach((el, i) => { el.style.animationDelay = `${Math.min(i, 18) * 16}ms`; });
      setTimeout(() => list.classList.remove('is-entering'), 1200);
    }
  }

  function counts() {
    const c = {
      all: state.rows.length,
      updates: state.rows.filter((r) => r.canUpdate).length,
      ok: state.rows.filter((r) => r.status === 'ok').length,
    };
    c.none = c.all - c.updates - c.ok;
    return c;
  }
  function updateCounts() {
    const c = counts();
    document.querySelectorAll('[data-count]').forEach((el) => { el.textContent = c[el.dataset.count] || ''; });
    placeThumb(false);
  }
  function updateSubtitle() {
    const c = counts();
    $('#subtitle').textContent = plural(c.all, 'sum.one', 'sum.many') + ' · ' + (c.updates ? t('sum.updates', { n: c.updates }) : t('sum.good'));
  }

  // seleccion: la casilla cambia en el pointerdown

  function toggleSelected(id, force) {
    const r = state.byId.get(id);
    if (!r || !r.canUpdate || state.installing) return;
    const on = force !== undefined ? force : !state.selected.has(id);
    if (on) state.selected.add(id); else state.selected.delete(id);
    list.querySelectorAll(`[data-id="${id}"]`).forEach((row) => {
      row.classList.toggle('is-selected', on);
      const c = row.querySelector('.check');
      if (c) c.setAttribute('aria-checked', String(on));
    });
    updateActionBar();
  }

  list.addEventListener('pointerdown', (e) => {
    const c = e.target.closest('[data-check]');
    if (c && e.button === 0) {
      e.preventDefault();
      toggleSelected(+c.dataset.check);
    }
  });
  list.addEventListener('click', (e) => {
    if (e.target.closest('[data-check]')) return;
    const row = e.target.closest('.row');
    if (row) openDetails(+row.dataset.id);
  });
  list.addEventListener('keydown', (e) => {
    const row = e.target.closest('.row');
    if (!row) return;
    if (e.key === ' ' && e.target === row) { e.preventDefault(); toggleSelected(+row.dataset.id); }
    else if (e.key === 'Enter' && e.target === row) openDetails(+row.dataset.id);
    else if (e.key === ' ' && e.target.matches('[data-check]')) { e.preventDefault(); toggleSelected(+e.target.dataset.check); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const rows = [...list.querySelectorAll('.row')];
      const next = rows[clamp(rows.indexOf(row) + (e.key === 'ArrowDown' ? 1 : -1), 0, rows.length - 1)];
      next && next.focus();
    }
  });

  // barra de accion de abajo

  const bar = $('#action-bar');
  const barY = new Spring({ value: 140, response: 0.4, dampingFraction: 0.8, precision: 0.2, onUpdate: (y) => { bar.style.transform = `translate(-50%, ${y}px)`; } });
  barY.jump(140);

  function updateActionBar() {
    const rows = [...state.selected].map((id) => state.byId.get(id)).filter(Boolean);
    const visible = rows.length > 0 && !state.scanning && !state.installing;
    bar.setAttribute('aria-hidden', String(!visible));
    barY.to(visible ? 0 : 140);
    if (!rows.length) return;
    const size = rows.reduce((a, r) => a + (r.sizeBytes || 0), 0);
    $('#action-count').textContent = plural(rows.length, 'bar.one', 'bar.many');
    const sources = [...new Set(rows.map((r) => sourceName(r.sourceKey)))].join(', ');
    $('#action-meta').textContent = [size ? t('bar.approx', { size: formatSize(size) }) : '', sources].filter(Boolean).join(' · ');
    $('#btn-update').textContent = rows.length === 1 ? t('btn.update') : t('btn.updateN', { n: rows.length });
  }
  $('#btn-clear').addEventListener('click', () => [...state.selected].forEach((id) => toggleSelected(id, false)));
  $('#btn-update').addEventListener('click', () => openConfirm());

  // hoja inferior (detalles, confirmar, progreso, resultado)

  const sheetEl = $('#sheet');
  const sheetBody = $('#sheet-body');
  const scrim = $('#scrim');
  const sheet = { open: false, dismissable: true, onClose: null, redraw: null };

  const closedY = () => sheetEl.offsetHeight + 40;
  const sheetY = new Spring({
    value: 0, response: 0.44, dampingFraction: 0.84, precision: 0.2,
    onUpdate: (y) => {
      sheetEl.style.transform = `translate(-50%, ${y}px)`;
      scrim.style.opacity = String(1 - clamp(y / closedY(), 0, 1));
    },
    onRest: () => {
      if (!sheet.open) { sheetEl.classList.remove('is-visible'); sheetBody.replaceChildren(); }
    },
  });
  const sheetH = new Spring({
    response: 0.38, dampingFraction: 0.92, precision: 0.3,
    onUpdate: (h) => { sheetBody.style.height = `${h}px`; },
    onRest: () => { sheetBody.style.height = ''; },
  });

  // al cambiar el contenido de una hoja abierta, la altura se anima con muelle
  function setSheetContent(node, animate) {
    const from = sheetBody.offsetHeight;
    sheetBody.replaceChildren(node);
    sheetBody.style.height = '';
    if (!animate) return;
    const to = sheetBody.offsetHeight;
    if (!sheetH.isAnimating) sheetH.value = from;
    sheetBody.style.height = `${sheetH.value}px`;
    sheetH.to(to);
    node.animate([{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }], { duration: 240, easing: 'cubic-bezier(0.2, 0.9, 0.3, 1)' });
  }

  function openSheet(node, opts = {}) {
    sheet.dismissable = opts.dismissable !== false;
    sheet.onClose = opts.onClose || null;
    sheet.redraw = opts.redraw || null;
    if (sheet.open) { setSheetContent(node, true); return; }
    setSheetContent(node, false);
    sheet.open = true;
    sheetEl.classList.add('is-visible');
    sheetEl.setAttribute('aria-hidden', 'false');
    scrim.classList.add('is-active');
    if (!sheetY.isAnimating) sheetY.jump(closedY()); // si se estaba cerrando, sigue desde ahi
    sheetY.to(0);
    requestAnimationFrame(() => {
      const f = sheetEl.querySelector('[data-autofocus]') || sheetEl.querySelector('button');
      f && f.focus({ preventScroll: true });
    });
  }

  function closeSheet(velocity) {
    if (!sheet.open) return;
    sheet.open = false;
    sheet.redraw = null;
    sheetEl.setAttribute('aria-hidden', 'true');
    scrim.classList.remove('is-active');
    sheetY.to(closedY(), velocity);
    const cb = sheet.onClose;
    sheet.onClose = null;
    cb && cb();
  }

  // si no se puede cerrar (instalando) hace un pequeño rebote para que se note
  const nudgeSheet = () => sheetY.to(0, -520);

  scrim.addEventListener('pointerdown', () => (sheet.dismissable ? closeSheet() : nudgeSheet()));

  // arrastrar la hoja desde la barrita de arriba
  const vt = new VelocityTracker();
  let drag = null;
  sheetEl.addEventListener('pointerdown', (e) => {
    if (!sheet.open || e.button !== 0) return;
    if (!e.target.closest('.sheet-grabber, .sheet-head') || e.target.closest('button, a, input')) return;
    drag = { y0: e.clientY, from: sheetY.value };
    vt.reset();
    vt.add(e.clientY);
    sheetEl.setPointerCapture(e.pointerId);
  });
  sheetEl.addEventListener('pointermove', (e) => {
    if (!drag) return;
    let y = drag.from + (e.clientY - drag.y0);
    if (y < 0 || !sheet.dismissable) y = rubberBand(y, 160);
    vt.add(e.clientY);
    sheetY.track(y);
  });
  const endDrag = () => {
    if (!drag) return;
    drag = null;
    const v = vt.velocity;
    if (sheet.dismissable && (sheetY.value > closedY() * 0.3 || v > 900)) closeSheet(Math.max(v, 0));
    else sheetY.to(0, v);
  };
  sheetEl.addEventListener('pointerup', endDrag);
  sheetEl.addEventListener('pointercancel', endDrag);

  function el(html) {
    const tpl = document.createElement('template');
    tpl.innerHTML = html.trim();
    return tpl.content.firstElementChild;
  }

  function onAction(node, handlers) {
    node.addEventListener('click', (e) => {
      const a = e.target.closest('[data-action]');
      if (a && handlers[a.dataset.action]) handlers[a.dataset.action](a);
    });
  }

  // detalles de un dispositivo

  function detailsNode(id) {
    const r = state.byId.get(id);
    const others = (r.others || []).map((o) =>
      `<div><span class="mv">${esc(o.version || '—')}</span><span class="ms">${esc(sourceName(o.sourceKey))} · ${esc(o.title)}</span></div>`).join('');
    const selected = state.selected.has(id);
    const latestInfo = [r.latestDate, sourceName(r.sourceKey)].filter(Boolean).join(' · ') || t('d.notFound');

    const node = el(`<div class="sheet-content">
      <div class="sheet-head">
        ${glyph(r.deviceClass)}
        <div style="min-width:0;flex:1">
          <h2 class="sheet-title">${esc(r.name)}</h2>
          <p class="sheet-subtitle"><span class="pill ${pillClass(r)}">${esc(statusText(r))}</span></p>
        </div>
      </div>
      <div class="compare">
        <div class="compare-card">
          <div class="compare-label">${esc(t('d.installed'))}</div>
          <div class="compare-version">${esc(installedText(r))}</div>
          <div class="compare-date">${esc(r.installedDate || '')}</div>
        </div>
        <div class="compare-card${r.canUpdate ? ' is-latest' : ''}">
          <div class="compare-label">${esc(t('d.latest'))}</div>
          <div class="compare-version">${esc(r.latestVersion || r.latestDate || '—')}</div>
          <div class="compare-date">${esc(latestInfo)}</div>
        </div>
      </div>
      <dl class="meta">
        ${r.latestTitle ? `<dt>${esc(t('d.package'))}</dt><dd>${esc(r.latestTitle)}</dd>` : ''}
        ${r.match ? `<dt>${esc(t('d.match'))}</dt><dd>${esc(t(r.match === 'exact' ? 'd.exact' : 'd.generic'))}</dd>` : ''}
        <dt>${esc(t('d.type'))}</dt><dd>${esc(r.deviceClass || '—')}</dd>
        <dt>${esc(t('d.provider'))}</dt><dd>${esc(r.provider || '—')}</dd>
        ${r.infName ? `<dt>${esc(t('d.inf'))}</dt><dd>${esc(r.infName)}</dd>` : ''}
        ${r.sizeBytes ? `<dt>${esc(t('d.size'))}</dt><dd>${formatSize(r.sizeBytes)}</dd>` : ''}
      </dl>
      ${others ? `<div class="subhead">${esc(t('d.others'))}</div><div class="mini-list">${others}</div>` : ''}
      ${(r.hardwareIds || []).length ? `<div class="subhead">${esc(t('d.hwids'))}</div><div class="mono">${r.hardwareIds.map(esc).join('<br>')}</div>` : ''}
      <div class="sheet-actions">
        ${r.infoUrl ? `<button class="btn btn-gray" data-action="url" data-press>${esc(t('d.page'))}</button>` : ''}
        ${r.canUpdate
          ? `<button class="btn ${selected ? 'btn-gray' : 'btn-primary'}" data-action="toggle" data-press data-autofocus>${esc(t(selected ? 'd.unselect' : 'd.select'))}</button>`
          : `<button class="btn btn-gray" data-action="close" data-press data-autofocus>${esc(t('d.close'))}</button>`}
      </div>
    </div>`);

    onAction(node, {
      url: () => send({ type: 'openUrl', url: r.infoUrl }),
      toggle: () => { toggleSelected(id); closeSheet(); },
      close: () => closeSheet(),
    });
    return node;
  }

  function openDetails(id) {
    if (!state.byId.has(id)) return;
    openSheet(detailsNode(id), { redraw: () => setSheetContent(detailsNode(id), false) });
  }

  // confirmar actualizacion

  function confirmNode(rows) {
    const size = rows.reduce((a, r) => a + (r.sizeBytes || 0), 0);
    const items = rows.slice(0, 8).map((r) =>
      `<div><span class="ms" style="flex:1">${esc(r.name)}</span><span class="mv">${esc(r.hasDriver ? r.installedVersion : t('c.noDriver'))} → ${esc(r.latestVersion || r.latestDate)}</span></div>`).join('')
      + (rows.length > 8 ? `<div><span class="ms">${esc(t('c.more', { n: rows.length - 8 }))}</span></div>` : '');

    const node = el(`<div class="sheet-content">
      <div class="sheet-head"><div>
        <h2 class="sheet-title">${esc(rows.length === 1 ? t('c.one') : t('c.many', { n: rows.length }))}</h2>
        <p class="sheet-subtitle">${esc(t('c.text', { size: size ? t('c.size', { size: formatSize(size) }) : '' }))}</p>
      </div></div>
      <div class="mini-list">${items}</div>
      ${rows.some((r) => r.sourceKey === 'nvidia') ? `<p class="sheet-subtitle" style="margin-top:12px">${esc(t('c.nvidia'))}</p>` : ''}
      ${rows.some((r) => r.match === 'generic') ? `<p class="sheet-subtitle" style="margin-top:12px">${esc(t('c.generic'))}</p>` : ''}
      <div class="switch-row">
        <div>${esc(t('c.restore'))}<small>${esc(t('c.restoreHint'))}</small></div>
        <button class="switch" role="switch" aria-checked="${state.restorePoint}" aria-label="${esc(t('c.restore'))}" data-press></button>
      </div>
      <div class="sheet-actions">
        <button class="btn btn-gray btn-large" data-action="cancel" data-press>${esc(t('c.cancel'))}</button>
        <button class="btn btn-primary btn-large" data-action="go" data-press data-autofocus>${esc(t('c.go'))}</button>
      </div>
    </div>`);

    const sw = node.querySelector('.switch');
    const flip = () => { state.restorePoint = !state.restorePoint; sw.setAttribute('aria-checked', String(state.restorePoint)); };
    sw.addEventListener('pointerdown', (e) => { if (e.button === 0) { e.preventDefault(); flip(); } });
    sw.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
    onAction(node, { cancel: () => closeSheet(), go: () => startInstall(rows) });
    return node;
  }

  function openConfirm() {
    const rows = [...state.selected].map((id) => state.byId.get(id)).filter((r) => r && r.canUpdate);
    if (rows.length) openSheet(confirmNode(rows), { redraw: () => setSheetContent(confirmNode(rows), false) });
  }

  // instalacion

  function startInstall(rows) {
    state.installing = true;
    state.install = { rows, results: new Map(), total: rows.length };
    updateActionBar();
    setToolbarEnabled(false);

    const node = el(`<div class="sheet-content">
      <div class="sheet-head"><div style="flex:1;min-width:0">
        <h2 class="sheet-title">${esc(t('i.title'))}</h2>
        <p class="sheet-subtitle" id="install-message" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${esc(t('i.preparing'))}</p>
      </div></div>
      <div class="overall"><div class="progress"><div class="progress-fill" id="install-fill"></div></div></div>
      <div class="task-list">${rows.map((r) => `<div class="task" data-task="${r.id}" data-state="pending">
          <span class="task-icon">${ICON.pending}</span>
          <span style="min-width:0"><div class="task-name">${esc(r.name)}</div><div class="task-msg">${esc(r.latestVersion)} · ${esc(sourceName(r.sourceKey))}</div></span>
        </div>`).join('')}</div>
    </div>`);
    openSheet(node, { dismissable: false });

    const fill = node.querySelector('#install-fill');
    state.install.fill = new Spring({ response: 0.5, dampingFraction: 1, precision: 0.0005, onUpdate: (v) => { fill.style.transform = `scaleX(${v})`; } });
    send({ type: 'install', ids: rows.map((r) => r.id), restorePoint: state.restorePoint });
  }

  function setTask(id, st, message) {
    const task = sheetBody.querySelector(`[data-task="${id}"]`);
    if (!task) return;
    task.dataset.state = st;
    task.querySelector('.task-icon').innerHTML =
      st === 'working' ? '<span class="spinner"></span>' : st === 'ok' ? ICON.ok : st === 'fail' ? ICON.x : st === 'skip' ? ICON.dash : ICON.pending;
    if (message) task.querySelector('.task-msg').textContent = message;
    if (st === 'working') {
      // ojo: scrollIntoView moveria tambien la ventana entera, asi que se desplaza solo la hoja
      const box = task.closest('.sheet-content');
      const top = task.offsetTop - box.offsetTop;
      if (top < box.scrollTop || top + task.offsetHeight > box.scrollTop + box.clientHeight)
        box.scrollTo({ top: top - box.clientHeight / 2, behavior: 'smooth' });
    }
  }

  function showInstallResult(msg) {
    state.installing = false;
    const results = [...state.install.results.values()];
    const ok = msg.okCount, failed = msg.failCount;
    const good = failed === 0 && ok > 0;
    const lines = results.map((r) => {
      const color = r.ok ? 'green' : r.status === 'notApplied' ? 'orange' : 'red';
      const mark = r.ok ? '✓' : r.status === 'notApplied' ? '–' : '✕';
      return `<div><span class="mv" style="color:var(--${color})">${mark}</span><span class="ms" style="white-space:normal">${esc(r.name)} — ${esc(r.message)}</span></div>`;
    }).join('');

    const title = ok === 0 ? t('r.none') : failed === 0 ? (ok === 1 ? t('r.one') : t('r.many', { n: ok })) : t('r.partial', { ok, total: ok + failed });
    const sub = ok === 0 ? t('r.subNone') : msg.reboot ? t('r.subReboot') : t('r.subOptional');

    const node = el(`<div class="sheet-content">
      <div class="result-hero">
        <div class="badge ${good ? 'ok' : 'warn'}">${good ? ICON.done : ICON.warn}</div>
        <h2 class="sheet-title">${esc(title)}</h2>
        <p class="sheet-subtitle">${esc(sub)}</p>
      </div>
      <div class="subhead">${esc(t('r.detail'))}</div>
      <div class="mini-list">${lines}</div>
      <div class="sheet-actions">
        ${ok > 0
          ? `<button class="btn btn-gray btn-large" data-action="later" data-press>${esc(t('r.later'))}</button><button class="btn btn-primary btn-large" data-action="restart" data-press data-autofocus>${esc(t('r.restart'))}</button>`
          : `<button class="btn btn-primary btn-large" data-action="later" data-press data-autofocus>${esc(t('r.close'))}</button>`}
      </div>
    </div>`);
    onAction(node, {
      restart: (b) => { b.disabled = true; send({ type: 'restart' }); },
      later: () => closeSheet(),
    });
    state.selected.clear();
    openSheet(node, { onClose: () => send({ type: 'scan' }) });
    setToolbarEnabled(true);
  }

  // pantalla de analisis

  const scanEl = $('#scan');
  const scanFill = $('#scan-fill');
  const scanShow = new Spring({
    value: 1, response: 0.4, dampingFraction: 1, precision: 0.002,
    onUpdate: (v) => { scanEl.style.opacity = String(v); scanEl.style.transform = `scale(${0.985 + 0.015 * v})`; },
    onRest: (v) => { if (v === 0) scanEl.hidden = true; },
  });
  const scanProgress = new Spring({ response: 0.6, dampingFraction: 1, precision: 0.0005, onUpdate: (v) => { scanFill.style.transform = `scaleX(${v})`; } });

  function setStep(phase) {
    const order = ['devices', 'sources', 'compare'];
    const idx = phase === 'done' ? 3 : order.indexOf(phase);
    document.querySelectorAll('#steps li').forEach((li) => {
      const i = order.indexOf(li.dataset.step);
      li.classList.toggle('is-done', i < idx);
      li.classList.toggle('is-active', i === idx);
    });
  }

  function showScan() {
    state.scanning = true;
    state.lastProgress = null;
    updateActionBar();
    setToolbarEnabled(false);
    $('#scan-title').textContent = t('scan.title');
    $('#scan-message').textContent = t('scan.devices');
    $('#btn-retry').hidden = true;
    $('#sources-count').textContent = '';
    setStep('devices');
    scanProgress.jump(0);
    scanProgress.to(0.06);
    scanEl.hidden = false;
    scanShow.to(1);
  }

  function onScanProgress(p) {
    state.lastProgress = p;
    if (scanEl.hidden) showScan();
    const msg = $('#scan-message');
    setStep(p.phase);
    if (p.phase === 'devices') { msg.textContent = t('scan.devices'); scanProgress.to(0.06); }
    if (p.phase === 'sources') {
      msg.textContent = p.device ? t('scan.checking', { name: p.device }) : t('scan.sources');
      msg.title = msg.textContent;
      scanProgress.to(0.1 + 0.82 * (p.total > 0 ? p.done / p.total : 0));
      $('#sources-count').textContent = p.total > 0 ? `${p.done}/${p.total}` : '';
    }
    if (p.phase === 'compare') { msg.textContent = t('scan.compare'); scanProgress.to(0.96); }
  }

  function onScanResult(msg) {
    state.rows = msg.rows;
    state.byId = new Map(msg.rows.map((r) => [r.id, r]));
    state.selected = new Set(msg.rows.filter((r) => r.recommended).map((r) => r.id));
    state.warnings = msg.warnings || [];
    state.scanning = false;

    setStep('done');
    scanProgress.to(1);
    $('#controls').hidden = false;
    updateCounts();
    updateSubtitle();

    $('#banner').hidden = state.warnings.length === 0;
    $('#banner-text').textContent = state.warnings.join(' · ');

    if (state.filter !== 'all' && state.filter !== 'updates') setFilter('all', false);
    renderList(true);
    send({ type: 'rendered', count: list.querySelectorAll('.row').length });
    requestAnimationFrame(() => placeThumb(false));
    setToolbarEnabled(true);
    setTimeout(() => { scanShow.to(0); updateActionBar(); }, 260);
  }

  function onScanError(message) {
    state.scanning = false;
    if (scanEl.hidden) { scanEl.hidden = false; scanShow.to(1); }
    $('#scan-title').textContent = t('scan.error');
    $('#scan-message').textContent = message;
    $('#scan-message').title = message;
    $('#btn-retry').hidden = false;
    setToolbarEnabled(true);
  }

  function setToolbarEnabled(on) {
    $('#btn-rescan').disabled = !on || state.installing;
    $('#btn-export').disabled = !on || state.installing || state.rows.length === 0;
  }

  // aviso flotante

  const toast = $('#toast');
  const toastY = new Spring({ value: -80, response: 0.38, dampingFraction: 0.78, precision: 0.2, onUpdate: (y) => { toast.style.transform = `translate(-50%, ${y}px)`; } });
  toastY.jump(-80);
  let toastTimer = 0;
  function showToast(text) {
    toast.textContent = text;
    toastY.to(0);
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastY.to(-80), 3200);
  }

  $('#btn-rescan').addEventListener('click', () => send({ type: 'scan' }));
  $('#btn-retry').addEventListener('click', () => send({ type: 'scan' }));
  $('#btn-export').addEventListener('click', () => send({ type: 'export' }));
  $('#banner-close').addEventListener('click', () => { $('#banner').hidden = true; });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      if (menuOpen) closeLangMenu(true);
      else if (sheet.open) { sheet.dismissable ? closeSheet() : nudgeSheet(); }
      else if (document.activeElement === search && search.value) { search.value = ''; state.query = ''; renderList(false); }
    }
    const k = e.key.toLowerCase();
    if ((e.ctrlKey || e.metaKey) && k === 'f' && !state.scanning) { e.preventDefault(); search.focus(); search.select(); }
    if ((e.ctrlKey || e.metaKey) && k === 'r') { e.preventDefault(); if (!state.scanning && !state.installing) send({ type: 'scan' }); }
  });
  document.addEventListener('contextmenu', (e) => { if (!e.target.closest('input, .mono')) e.preventDefault(); });

  // mensajes que llegan de C#

  function handle(msg) {
    switch (msg.type) {
      case 'init': setLang(msg.lang, false); break;
      case 'scanStarted': showScan(); break;
      case 'scanProgress': onScanProgress(msg); break;
      case 'scanResult': onScanResult(msg); break;
      case 'scanError': onScanError(msg.message); break;
      case 'installStatus': { const m = $('#install-message'); if (m) m.textContent = msg.message; break; }
      case 'installProgress': {
        const m = $('#install-message');
        if (m) m.textContent = msg.message;
        setTask(msg.id, 'working', msg.message);
        if (state.install && state.install.fill) state.install.fill.to((msg.index + 0.4) / msg.total);
        break;
      }
      case 'installItem': {
        const r = state.byId.get(msg.id);
        if (state.install) state.install.results.set(msg.id, { name: r ? r.name : '', ok: msg.ok, status: msg.status, message: msg.message });
        setTask(msg.id, msg.ok ? 'ok' : msg.status === 'notApplied' ? 'skip' : 'fail', msg.message);
        if (state.install && state.install.fill) state.install.fill.to(state.install.results.size / state.install.total);
        break;
      }
      case 'installDone': showInstallResult(msg); break;
      case 'exported': showToast(t('toast.exported', { file: msg.fileName })); break;
      case 'error': showToast(msg.message); break;
    }
  }

  applyStaticTexts();
  $('#scan-message').textContent = t('scan.devices');
  scanEl.hidden = false;
  setStep('devices');
  scanProgress.jump(0.04);
  window.ODU = { handle, state, setLang }; // para probar desde la consola
  send({ type: 'ready' });
})();
