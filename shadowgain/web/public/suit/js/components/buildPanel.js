import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { escapeHtml } from './topbar.js';

// The selects and toggle are PERMANENT DOM nodes: the structure is built once (rebuilt only if
// the set list itself changes) and renders just patch values in place. Rebuilding these controls
// on state changes destroys an open dropdown and makes clicks feel dropped.

let structureMemo = null;

export function init() {
  const el = document.getElementById('build-panel');

  el.addEventListener('change', e => {
    if (e.target.matches('[data-set="primary"]')) actions.setBuild({ primarySetId: parseInt(e.target.value, 10) });
    if (e.target.matches('[data-set="secondary"]')) actions.setBuild({ secondarySetId: parseInt(e.target.value, 10) });
  });

  el.addEventListener('click', e => {
    if (e.target.closest('[data-toggle="transfers"]'))
      actions.setBuild({ allowTransfers: !get().build.allowTransfers });
  });

  subscribe(['inventory', 'build', 'search-status'], render);
  render();
}

function options(sets) {
  const base = [
    { id: 0, name: 'No Armor Set' },
    { id: 255, name: 'Any Armor Set' },
    ...sets.map(s => ({ id: s.id, name: s.name })),
  ];

  return base.map(o => `<option value="${o.id}">${escapeHtml(o.name)}</option>`).join('');
}

function ensureStructure(sets) {
  const el = document.getElementById('build-panel');
  const memo = JSON.stringify(sets.map(s => s.id));

  if (structureMemo === memo && el.firstChild)
    return el;

  structureMemo = memo;

  el.innerHTML =
    '<div class="card">' +
    '<div class="label">BUILD TARGET</div>' +
    '<div class="field"><span>Primary set</span>' +
    `<select class="select" data-set="primary">${options(sets)}</select></div>` +
    '<div class="field"><span>Secondary set</span>' +
    `<select class="select" data-set="secondary">${options(sets)}</select></div>` +
    '<div class="toggle-row" data-toggle="transfers">' +
    '<span class="toggle"></span>' +
    '<span class="toggle-row__text">' +
    '<span class="toggle-row__title">Allow set transfers</span>' +
    '<span class="toggle-row__sub">Server mod: move a loot attribute set from a donor piece onto same-coverage armor. The donor is destroyed. Only used when a specific set is selected above.</span>' +
    '</span></div>' +
    '<div class="toggle-row__sub" data-last style="padding-top: 4px;"></div>' +
    '</div>';

  return el;
}

function render() {
  const state = get();
  const el = ensureStructure(state.inventory?.armorSets ?? []);

  const primary = el.querySelector('[data-set="primary"]');
  const secondary = el.querySelector('[data-set="secondary"]');

  if (primary.value !== String(state.build.primarySetId)) primary.value = String(state.build.primarySetId);
  if (secondary.value !== String(state.build.secondarySetId)) secondary.value = String(state.build.secondarySetId);

  el.querySelector('[data-toggle="transfers"] .toggle').classList.toggle('on', state.build.allowTransfers);

  const last = state.search.lastSummary;
  const lastEl = el.querySelector('[data-last]');
  const text = last ? `Last search: ${last.suits} suits in ${last.seconds} s` : '';
  if (lastEl.textContent !== text) lastEl.textContent = text;
}
