import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { escapeHtml } from './topbar.js';

const CHECK = '<svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="#1c1206" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round"><path d="M5 13l4 4 10-10"></path></svg>';

const ITEM_TYPES = [
  ['armor', 'Body armor & clothing'],
  ['underwear', 'Shirts & pants'],
  ['jewelry', 'Jewelry'],
];

export function init() {
  const el = document.getElementById('sidebar');

  el.addEventListener('click', e => {
    const row = e.target.closest('[data-char], [data-type], [data-server]');
    if (!row) return;
    if (row.dataset.char) actions.toggleCharacter(row.dataset.char);
    else if (row.dataset.server) actions.toggleServer(row.dataset.server);
    else actions.toggleItemType(row.dataset.type);
  });

  el.addEventListener('change', e => {
    if (e.target.matches('[data-al]')) {
      const min = parseInt(el.querySelector('[data-al="min"]').value, 10);
      const max = parseInt(el.querySelector('[data-al="max"]').value, 10);
      actions.setALRange(Number.isFinite(min) ? min : 0, Number.isFinite(max) ? max : 9999);
    }
  });

  subscribe(['inventory', 'filters'], render);
  render();
}

function checkboxHtml(on, partial = false) {
  if (partial && !on)
    return '<span class="checkbox partial"><span class="checkbox__dash"></span></span>';
  return `<span class="checkbox ${on ? 'on' : ''}">${CHECK}</span>`;
}

function render() {
  const state = get();
  const el = document.getElementById('sidebar');
  const parts = [];

  const servers = state.inventory?.servers ?? [];
  for (const server of servers) {
    const keys = server.characters.map(c => actions.charKey(server.name, c.name));
    const onCount = keys.filter(k => state.filters.characters.has(k)).length;
    const allOn = onCount === keys.length && keys.length > 0;
    const totalItems = server.characters.reduce((n, c) => n + c.items.length, 0);

    parts.push('<div class="side-section">');
    parts.push(
      `<div class="side-row side-row--server" data-server="${escapeHtml(server.name)}" title="Track all characters on ${escapeHtml(server.name)}">` +
      checkboxHtml(allOn, onCount > 0) +
      `<span class="label" style="padding: 0;">${escapeHtml(server.name.toUpperCase())}</span>` +
      `<span class="side-row__count">${totalItems}</span></div>`);

    for (const character of server.characters) {
      const key = actions.charKey(server.name, character.name);
      const on = state.filters.characters.has(key);
      parts.push(
        `<div class="side-row ${on ? 'checked' : ''}" data-char="${escapeHtml(key)}">` +
        checkboxHtml(on) +
        `<span>${escapeHtml(character.name)}</span>` +
        `<span class="side-row__count">${character.items.length}</span></div>`);
    }

    parts.push('</div>');
  }

  parts.push('<div class="side-section"><div class="label">ITEM TYPES</div>');
  for (const [type, name] of ITEM_TYPES) {
    const on = state.filters.itemTypes.has(type);
    parts.push(
      `<div class="side-row ${on ? 'checked' : ''}" data-type="${type}">` +
      checkboxHtml(on) + `<span>${name}</span></div>`);
  }
  parts.push('</div>');

  parts.push(
    '<div class="side-section" style="gap: 8px;"><div class="label">BASE ARMOR LEVEL</div>' +
    '<div class="al-range">' +
    `<input data-al="min" type="text" inputmode="numeric" value="${state.filters.minCoreAL}">` +
    '<span style="color: var(--text-4);">–</span>' +
    `<input data-al="max" type="text" inputmode="numeric" value="${state.filters.maxCoreAL}">` +
    '</div></div>');

  // Don't clobber the AL inputs while the user is typing in one
  if (el.contains(document.activeElement) && document.activeElement.matches('[data-al]'))
    return;

  el.innerHTML = parts.join('');
}
