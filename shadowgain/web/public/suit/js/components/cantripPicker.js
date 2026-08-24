import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { LEVELS, levelName } from '../format.js';
import { escapeHtml } from './topbar.js';

let popoverOpen = false;

export function init() {
  const panel = document.getElementById('cantrip-panel');

  panel.addEventListener('click', e => {
    const remove = e.target.closest('[data-remove]');
    if (remove) { e.stopPropagation(); actions.setCantrip(remove.dataset.remove, null); return; }
    if (e.target.closest('[data-open-popover]')) openPopover();
  });

  document.addEventListener('click', e => {
    if (!popoverOpen) return;
    // A click that re-rendered the popover detaches its own target; that's an inside click.
    if (!e.target.isConnected) return;
    const root = document.getElementById('popover-root');
    if (!root.contains(e.target) && !e.target.closest('#cantrip-panel')) closePopover();
  });

  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && popoverOpen) closePopover();
  });

  subscribe(['build', 'cantrips'], render);
  render();
}

function selectionRows(state) {
  const families = new Map((state.cantrips?.families ?? []).map(f => [f.key, f]));
  const rank = { legendary: 4, epic: 3, major: 2, minor: 1 };

  const rows = [...state.build.cantrips]
    .map(([key, level]) => ({ key, level, family: families.get(key) }))
    .filter(r => r.family)
    .sort((a, b) => rank[b.level] - rank[a.level] ||
      (a.family.column * 10 + a.family.row) - (b.family.column * 10 + b.family.row));

  return rows.map(r =>
    `<div class="cantrip-row" data-open-popover>` +
    `<span class="dot dot--${r.level}"></span>` +
    `<span class="cantrip-row__name">${escapeHtml(r.family.name)}</span>` +
    `<span class="cantrip-row__level">${levelName(r.level)}</span>` +
    `<button class="cantrip-row__remove" data-remove="${r.key}" title="Remove">✕</button>` +
    `</div>`).join('');
}

function render() {
  const state = get();

  // Memoized: 'build' notifications also fire for set/toggle changes this panel doesn't show;
  // rebuilding here on those would add DOM churn right next to the controls being clicked.
  const el = document.getElementById('cantrip-panel');
  const memo = JSON.stringify([[...state.build.cantrips], state.build.activePreset, state.build.presetEdited, !!state.cantrips]);
  if (el.dataset.memo === memo) return;
  el.dataset.memo = memo;

  const preset = state.build.activePreset
    ? 'Preset: ' + state.build.activePreset + (state.build.presetEdited ? ' (edited)' : '')
    : '';

  el.innerHTML =
    '<div class="cantrip-head">' +
    '<span class="label">CANTRIPS</span>' +
    `<span class="preset-link" data-open-popover>${escapeHtml(preset) || 'Presets…'}</span>` +
    '</div>' +
    `<div class="cantrip-list">${selectionRows(state)}` +
    '<div class="cantrip-add" data-open-popover>' +
    '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14"></path><path d="M5 12h14"></path></svg>' +
    '<span>Add cantrip…</span></div></div>' +
    '<div class="legend">' +
    '<span><span class="dot dot--legendary"></span>Lgnd</span>' +
    '<span><span class="dot dot--epic"></span>Epic</span>' +
    '<span><span class="dot dot--major"></span>Major</span>' +
    '<span><span class="dot dot--minor"></span>Minor</span>' +
    '</div>';

  if (popoverOpen) renderPopover();
}

function openPopover() {
  popoverOpen = true;
  renderPopover();
}

function closePopover() {
  popoverOpen = false;
  document.getElementById('popover-root').innerHTML = '';
}

function renderPopover() {
  const state = get();
  const root = document.getElementById('popover-root');
  const presets = Object.keys(state.cantrips?.presets ?? {});

  const cells = [];
  const byPosition = [...(state.cantrips?.families ?? [])].sort((a, b) => a.row - b.row || a.column - b.column);

  for (const family of byPosition) {
    const level = state.build.cantrips.get(family.key);
    cells.push(
      `<button class="cgrid-cell ${level ? 'lvl-' + level : ''}" data-family="${family.key}" ` +
      `style="grid-column: ${family.column + 1}; grid-row: ${family.row + 1};" ` +
      `title="${escapeHtml(family.name)} — click to cycle, Shift-click to reverse">` +
      `<span class="dot ${level ? 'dot--' + level : 'dot--off'}"></span>` +
      `<span>${escapeHtml(family.name)}</span></button>`);
  }

  root.innerHTML =
    '<div class="popover cantrip-pop" role="dialog" aria-label="Cantrips">' +
    '<div class="cantrip-pop__presets"><span class="label">PRESETS</span>' +
    presets.map(p =>
      `<button class="chip preset-chip ${state.build.activePreset === p && !state.build.presetEdited ? 'active' : ''}" data-preset="${escapeHtml(p)}">${escapeHtml(p)}</button>`).join('') +
    '<button class="chip preset-chip" data-preset="">Clear</button>' +
    '</div>' +
    `<div class="cgrid">${cells.join('')}</div>` +
    '<div class="legend" style="margin-top: 0; border-top: none; padding-top: 0;">' +
    '<span><span class="dot dot--legendary"></span>Legendary</span>' +
    '<span><span class="dot dot--epic"></span>Epic</span>' +
    '<span><span class="dot dot--major"></span>Major</span>' +
    '<span><span class="dot dot--minor"></span>Minor</span>' +
    '<span style="margin-left: auto;">Click cycles up · Shift-click cycles down</span>' +
    '</div></div>';

  const pop = root.firstElementChild;
  const anchor = document.getElementById('cantrip-panel').getBoundingClientRect();
  pop.style.top = Math.max(12, anchor.top) + 'px';
  pop.style.right = (window.innerWidth - anchor.left + 12) + 'px';

  pop.addEventListener('click', e => {
    const presetBtn = e.target.closest('[data-preset]');
    if (presetBtn) { actions.applyPreset(presetBtn.dataset.preset); return; }

    const cell = e.target.closest('[data-family]');
    if (!cell) return;

    const key = cell.dataset.family;
    const current = get().build.cantrips.get(key) ?? null;
    const index = current ? LEVELS.indexOf(current) : -1;

    let next;
    if (e.shiftKey) {
      // reverse: off <- minor <- major <- epic <- legendary <- off
      next = index === -1 ? 'legendary' : (index === 0 ? null : LEVELS[index - 1]);
    } else {
      // forward: off -> minor -> major -> epic -> legendary -> off
      next = index === -1 ? 'minor' : (index === LEVELS.length - 1 ? null : LEVELS[index + 1]);
    }

    actions.setCantrip(key, next);
  });
}
