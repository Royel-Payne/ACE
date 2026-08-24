import { subscribe } from '../state.js';
import * as actions from '../actions.js';
import { SET_BONUSES, planText } from '../format.js';
import { escapeHtml } from './topbar.js';
import { toast } from './toast.js';

const ARROW = '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14"></path><path d="M13 6l6 6-6 6"></path></svg>';
const COPY = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="11" height="11" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg>';

export function init() {
  document.getElementById('tinker-plan').addEventListener('click', e => {
    if (e.target.closest('[data-copy-plan]')) {
      const suit = actions.selectedSuit();
      const text = suit ? planText(suit) : '';
      if (text) navigator.clipboard.writeText(text).then(() => toast('Tinkering plan copied'));
    }
  });

  // Deliberately NOT subscribed to 'suits': streaming inserts must not rebuild these cards
  subscribe('suit-selected', render);
  render();
}

function setBonusCard(suit) {
  if (!suit || suit.setCounts.length === 0) return '';

  const rows = suit.setCounts.map((sc, i) => {
    const filled = Math.min(5, sc.count);
    const pips = Array.from({ length: 5 }, (_, p) =>
      `<span class="pip ${p < filled ? (i === 0 && filled >= 5 ? 'fill fill--accent' : 'fill') : ''}"></span>`).join('');
    const bonus = SET_BONUSES[sc.setId];

    return '<div class="setbonus">' +
      '<div class="setbonus__head">' +
      `<span class="setbonus__name">${escapeHtml(sc.name)}</span>` +
      `<span class="setbonus__pips">${pips}</span>` +
      `<span class="setbonus__n">${Math.min(5, sc.count)}/5</span>` +
      '</div>' +
      (bonus ? `<span class="setbonus__desc">${escapeHtml(bonus)}</span>` : '') +
      '</div>';
  }).join('');

  return `<div class="card"><div class="label">SET BONUSES</div>${rows}</div>`;
}

function planSteps(suit) {
  const tinked = suit?.pieces.filter(p => p.isSetTinkeredVariant) ?? [];

  if (tinked.length === 0)
    return '<div class="plan-empty">No set tinkering required for this suit.</div>';

  return tinked.map((piece, i) =>
    '<div class="plan-step">' +
    `<div class="plan-step__num">${i + 1}</div>` +
    '<div class="plan-step__body">' +
    '<div class="plan-step__title">' +
    `<span>${escapeHtml(piece.name)}</span>` +
    `<span class="plan-step__sets">${escapeHtml((piece.originalSetName ?? '—').replace(/ Set$/, ''))}</span>` +
    ARROW +
    `<span class="plan-step__to">${escapeHtml((piece.effectiveSetName ?? '—').replace(/ Set$/, ''))}</span>` +
    '</div>' +
    `<span class="plan-step__text">${(piece.instructions ?? []).map(escapeHtml).join(' ')}</span>` +
    (piece.donor?.info
      ? `<span class="plan-step__text" style="color: var(--text-5);">Donor: ${escapeHtml(piece.donor.info)}</span>`
      : '') +
    '</div></div>').join('');
}

function render() {
  const suit = actions.selectedSuit();
  const tinkCount = suit?.totalSetTinkers ?? 0;

  document.getElementById('set-bonuses').innerHTML = setBonusCard(suit);

  document.getElementById('tinker-plan').innerHTML =
    '<div class="plan-head">' +
    '<span class="label">TINKERING PLAN</span>' +
    (tinkCount > 0 ? `<span class="n">${tinkCount} transfer${tinkCount === 1 ? '' : 's'}</span>` : '') +
    '</div>' +
    (suit ? planSteps(suit) : '<div class="plan-empty">Select a suit to see its plan.</div>') +
    (tinkCount > 0 ? `<button class="copy-btn" data-copy-plan>${COPY}<span>Copy tinkering plan</span></button>` : '');
}
