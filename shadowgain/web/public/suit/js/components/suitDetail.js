import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { slotSortKey, slotLabel, isArmorSlot, effectiveCantripMatrix } from '../format.js';
import { escapeHtml } from './topbar.js';

let accessoriesOpen = true;

export function init() {
  const el = document.getElementById('suit-detail');

  el.addEventListener('click', e => {
    if (e.target.closest('[data-toggle-accessories]')) {
      accessoriesOpen = !accessoriesOpen;
      render();
    }
    if (e.target.closest('[data-goto-build]')) actions.setView('build');
  });

  // Deliberately NOT subscribed to 'suits': streaming inserts must not rebuild the detail pane
  subscribe(['suit-selected', 'search-status', 'cantrips'], render);
  render();
}

function pieceRow(piece) {
  const tinked = piece.isSetTinkeredVariant;
  const setChip = piece.effectiveSetName
    ? (tinked
      ? `<span class="chip chip--tink">${escapeHtml(piece.effectiveSetName.replace(/ Set$/, ''))} (T)</span>`
      : `<span class="chip">${escapeHtml(piece.effectiveSetName.replace(/ Set$/, ''))}</span>`)
    : '<span class="cell-none">—</span>';

  const info = piece.info ?? '';
  const donorTitle = tinked && piece.donor?.info ? escapeHtml(piece.donor.info) : '';

  const consumesLine = tinked && piece.donor
    ? `<span class="slot-row__consumes" title="${donorTitle}">consumes ${escapeHtml(piece.donor.name)} · ${escapeHtml(piece.donor.owner)}</span>`
    : '';

  return `<div class="slot-row ${tinked ? 'tinked' : ''}">` +
    `<span class="slot-row__slot">${escapeHtml(slotLabel(piece.slots))}</span>` +
    `<span class="slot-row__stack">` +
    `<span class="slot-row__name">${escapeHtml(piece.name)}</span>` +
    consumesLine +
    `<span class="slot-row__info" title="${escapeHtml(info)}">${escapeHtml(info)}</span>` +
    `</span>` +
    `<span class="slot-row__owner">${escapeHtml(piece.owner)}</span>` +
    setChip +
    `<span class="slot-row__al">${piece.calcedStartingArmorLevel > 0 ? piece.calcedStartingArmorLevel : ''}</span>` +
    '</div>';
}

function render() {
  const el = document.getElementById('suit-detail');
  const state = get();
  const suit = actions.selectedSuit();

  if (!suit) {
    const idle = state.search.status === 'idle';
    el.innerHTML = '<div class="empty-state">' +
      (idle
        ? '<span>No search yet.</span><button class="btn" data-goto-build>Configure a build</button>'
        : '<span>Waiting for suits…</span>') +
      '</div>';
    return;
  }

  const pieces = [...suit.pieces].sort((a, b) => slotSortKey(a.slots) - slotSortKey(b.slots));
  const armor = pieces.filter(p => isArmorSlot(p.slots));
  const accessories = pieces.filter(p => !isArmorSlot(p.slots));

  // The 7x7 matrix view: same layout as the picker, colored by the suit's best achieved tier
  const matrix = effectiveCantripMatrix(suit, state.cantrips);
  const byPosition = [...matrix.cells].sort((a, b) => a.row - b.row || a.column - b.column);

  const grid = byPosition.map(c =>
    `<div class="cgrid-cell cgrid-cell--mini ${c.level ? 'lvl-' + c.level : 'cgrid-cell--dim'}" ` +
    `style="grid-column: ${c.column + 1}; grid-row: ${c.row + 1};" title="${escapeHtml(c.name)}${c.level ? ' — ' + c.level : ''}">` +
    `<span class="dot ${c.level ? 'dot--' + c.level : 'dot--off'}"></span>` +
    `<span>${escapeHtml(c.name)}</span></div>`).join('');

  const extras = matrix.unmatched.length > 0
    ? '<div class="cantrip-cloud" style="margin-top: 6px;">' +
      matrix.unmatched.map(u =>
        `<span class="cloud-chip"><span class="dot dot--${u.level}"></span>${escapeHtml(u.family)}</span>`).join('') +
      '</div>'
    : '';

  el.innerHTML =
    '<div class="detail-head">' +
    `<span class="detail-head__title">Suit</span>` +
    `<span class="detail-head__sub">${suit.count} pieces · AL ${suit.totalBaseArmorLevel} · ` +
    `${suit.totalEffectiveLegendaries} leg / ${suit.totalEffectiveEpics} epic / ${suit.totalEffectiveMajors} major</span>` +
    '</div>' +
    '<div class="slot-rows">' +
    `<div class="label">ARMOR</div>` +
    armor.map(pieceRow).join('') +
    (accessories.length > 0
      ? `<button class="group-toggle ${accessoriesOpen ? 'open' : ''}" data-toggle-accessories>` +
        '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 6l6 6-6 6"></path></svg>' +
        '<span class="label" style="color: inherit;">CLOTHING &amp; JEWELRY</span>' +
        `<span class="count">${accessories.length} piece${accessories.length === 1 ? '' : 's'}</span></button>` +
        (accessoriesOpen ? accessories.map(pieceRow).join('') : '')
      : '') +
    '</div>' +
    '<div style="display: flex; flex-direction: column; gap: 8px; margin-top: 2px;">' +
    `<div class="label">EFFECTIVE CANTRIPS · ${matrix.covered}</div>` +
    `<div class="cgrid cgrid--mini">${grid}</div>` +
    extras +
    '</div>';
}
