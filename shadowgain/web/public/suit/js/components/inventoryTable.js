import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { escapeHtml } from './topbar.js';

const LOCK = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="11" width="14" height="9" rx="2"></rect><path d="M8 11V8a4 4 0 0 1 8 0v3"></path></svg>';
const EYEOFF = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3l18 18"></path><path d="M10.6 10.6a2.4 2.4 0 0 0 3.3 3.3"></path><path d="M9 5.2A9.8 9.8 0 0 1 12 5c5 0 8.5 4 9.5 7-0.4 1.2-1.3 2.7-2.6 4M6.2 6.4C4 7.9 2.6 10.2 2.5 12c1 3 4.5 7 9.5 7 1.1 0 2.1-0.2 3-0.5"></path></svg>';

const COLUMNS = [
  ['name', 'ITEM'],
  ['owner', 'OWNER'],
  ['slot', 'SLOT'],
  ['set', 'SET'],
  ['al', 'AL'],
  ['details', 'DETAILS'],
];

let flatItems = [];   // cached flattened + haystacked items
let renderTimer = 0;

export function init() {
  const header = document.getElementById('inv-header');
  header.innerHTML = '<div class="inv-grid">' +
    '<span></span>' +
    COLUMNS.map(([key, label]) =>
      `<button class="hcell ${key === 'al' ? 'num' : ''} ${key === 'spells' ? 'pad' : ''}" data-sort="${key}">${label}<span data-glyph="${key}"></span></button>`)
      .join('') +
    '</div>';

  header.addEventListener('click', e => {
    const btn = e.target.closest('[data-sort]');
    if (btn) actions.setSort(btn.dataset.sort);
  });

  const body = document.getElementById('inv-body');
  body.addEventListener('click', e => {
    const btn = e.target.closest('[data-flag]');
    if (!btn) return;
    const row = btn.closest('[data-id]');
    actions.toggleFlag(parseInt(row.dataset.id, 10), btn.dataset.flag);
  });

  const textInput = document.getElementById('text-filter');
  textInput.addEventListener('input', () => {
    clearTimeout(renderTimer);
    renderTimer = setTimeout(() => actions.setTextFilter(textInput.value.trim().toLowerCase()), 120);
  });

  document.addEventListener('keydown', e => {
    if (e.ctrlKey && e.key.toLowerCase() === 'k') { e.preventDefault(); textInput.focus(); textInput.select(); }
    if (e.key === 'Escape' && document.activeElement === textInput) { textInput.value = ''; actions.setTextFilter(''); textInput.blur(); }
  });

  subscribe('inventory', rebuildFlat);
  subscribe(['inventory', 'filters'], render);
  subscribe('flags', patchFlaggedRows);
}

function rebuildFlat() {
  const state = get();
  flatItems = [];

  for (const server of state.inventory?.servers ?? []) {
    for (const character of server.characters) {
      const key = actions.charKey(server.name, character.name);
      for (const item of character.items) {
        flatItems.push({
          item,
          charKey: key,
          haystack: (item.owner + '|' + item.equippableSlots + '|' + (item.info ?? item.name)).toLowerCase(),
        });
      }
    }
  }
}

function typeOf(item) {
  // Mirrors the sidebar's item-type buckets; everything else is "other"
  if (/Armor$|HeadWear|HandWear|FootWear/.test(item.equippableSlots) && (item.objectClass === 'Armor' || item.objectClass === 'Clothing')) return 'armor';
  if (/ChestWear|UpperLegWear|AbdomenWear/.test(item.equippableSlots) && item.objectClass === 'Clothing') return 'underwear';
  if (item.objectClass === 'Jewelry') return 'jewelry';
  return 'other';
}

function visibleItems() {
  const state = get();
  const { filters, sort } = state;
  const rows = [];

  for (const entry of flatItems) {
    if (filters.characters.size > 0 && !filters.characters.has(entry.charKey)) continue;

    const type = typeOf(entry.item);
    if (type !== 'other' && !filters.itemTypes.has(type)) continue;
    if (type === 'other') continue; // weapons/salvage/etc. aren't search input; keep the grid focused

    if (filters.text && !entry.haystack.includes(filters.text)) continue;
    rows.push(entry.item);
  }

  const dir = sort.dir;
  const value = {
    name: i => i.name.toLowerCase(),
    owner: i => i.owner.toLowerCase(),
    slot: i => i.equippableSlots,
    set: i => i.itemSetName ?? '￿',
    al: i => -(i.calcedStartingArmorLevel > 0 ? i.calcedStartingArmorLevel : -1),
    details: i => detailText(i).toLowerCase(),
  }[sort.col] ?? (i => i.name.toLowerCase());

  rows.sort((a, b) => {
    const va = value(a), vb = value(b);
    return (va < vb ? -1 : va > vb ? 1 : 0) * dir;
  });

  return rows;
}

// The identity name players recognize: material-prefixed ("Gold Amulet"), falling back to the raw name
function fullName(item) {
  return item.material ? item.material + ' ' + item.name : item.name;
}

// Everything identifying beyond the name: the ItemInfo string with its leading "Material Name" stripped
function detailText(item) {
  const info = item.info ?? '';
  const prefix = fullName(item);
  return info.startsWith(prefix) ? info.slice(prefix.length).replace(/^,\s*/, '') : info;
}

function rowHtml(item) {
  const setCell = item.itemSetName
    ? `<span><span class="chip">${escapeHtml(item.itemSetName.replace(/ Set$/, ''))}</span></span>`
    : '<span class="cell-none">—</span>';

  const al = item.calcedStartingArmorLevel > 0 ? item.calcedStartingArmorLevel : '<span class="cell-none">—</span>';
  const name = fullName(item);
  const details = detailText(item);
  const infoTitle = escapeHtml(item.info ?? name);

  return `<div class="inv-grid inv-row ${item.excluded ? 'excluded' : ''}" data-id="${item.itemKey}">` +
    `<span class="row-flags">` +
    `<button class="flag-btn ${item.locked ? 'on' : ''}" data-flag="locked" title="Lock into every suit">${LOCK}</button>` +
    `<button class="flag-btn ${item.excluded ? 'on' : ''}" data-flag="excluded" title="Exclude from searches">${EYEOFF}</button>` +
    `</span>` +
    `<span class="cell-name" title="${infoTitle}">${escapeHtml(name)}</span>` +
    `<span class="cell-muted">${escapeHtml(item.owner)}</span>` +
    `<span class="cell-muted">${escapeHtml(shortSlot(item))}</span>` +
    setCell +
    `<span class="cell-al">${al}</span>` +
    `<span class="cell-spells" title="${infoTitle}">${escapeHtml(details)}</span>` +
    `</div>`;
}

function shortSlot(item) {
  const map = {
    HeadWear: 'Head', ChestArmor: 'Chest', AbdomenArmor: 'Abdomen', UpperArmArmor: 'Upper Arms',
    LowerArmArmor: 'Lower Arms', HandWear: 'Hands', UpperLegArmor: 'Upper Legs',
    LowerLegArmor: 'Lower Legs', FootWear: 'Feet', NeckWear: 'Necklace', TrinketOne: 'Trinket',
    ChestWear: 'Shirt', UpperLegWear: 'Pants',
  };

  const parts = item.equippableSlots.split(',').map(s => s.trim());
  if (parts.length === 1) return map[parts[0]] ?? parts[0];
  if (parts.includes('WristWearLeft')) return 'Wrist';
  if (parts.includes('FingerWearLeft')) return 'Finger';
  if (parts.includes('FootWear')) return 'Feet';
  const first = map[parts[0]] ?? parts[0];
  return first + ' ×' + parts.length;
}

function render() {
  const state = get();

  // Browser build: until inventory files are loaded, the table area is the drop target prompt
  if (flatItems.length === 0) {
    document.getElementById('inv-body').innerHTML =
      '<div class="empty-state" style="height: 100%;">' +
      '<span style="font-size: 15px;">No inventory loaded</span>' +
      '<button class="btn btn--primary" data-load-inventory>Load your Mag-Tools folder</button>' +
      '<span>…or drag it anywhere onto this page. Files are parsed locally — nothing is uploaded.</span>' +
      '</div>';
    document.getElementById('inv-statusbar').innerHTML =
      '<span>Waiting for inventory files (Documents\\Decal Plugins\\Mag-Tools)</span>';
    return;
  }

  const rows = visibleItems();

  for (const glyph of document.querySelectorAll('[data-glyph]'))
    glyph.textContent = glyph.dataset.glyph === state.sort.col ? (state.sort.dir === 1 ? '▲' : '▼') : '';

  document.getElementById('inv-body').innerHTML = rows.map(rowHtml).join('');

  const total = flatItems.length;
  const setPieces = flatItems.filter(e => e.item.itemSetId >= 13 && e.item.itemSetId <= 30).length;
  const statusbar = document.getElementById('inv-statusbar');

  statusbar.innerHTML =
    `<span>${total.toLocaleString()} items · showing <span class="lit">${rows.length.toLocaleString()}</span></span>` +
    `<span>·</span><span><span class="lit">${setPieces}</span> set pieces</span>` +
    (state.filters.text ? '<span>· text filter is display-only (not sent to search)</span>' : '') +
    (state.build.allowTransfers ? '<span class="accent right">set transfers enabled</span>' : '');
}

function patchFlaggedRows() {
  // Cheap: re-render only rows whose flags changed would need per-row tracking; the flags
  // toggle re-renders the affected row via full render (rows count is modest with content-visibility)
  render();
}
