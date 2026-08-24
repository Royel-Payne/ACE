import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { escapeHtml } from './topbar.js';

// Keeps a DOM card per suitId; on each 'suits' notification it walks the (already sorted) state
// array and inserts only missing cards at their correct positions — O(list) scans, O(1) inserts.
// Rank numbers come from CSS counters, so inserting never renumbers siblings.

const cards = new Map(); // suitId -> element
let newBelowFold = 0;

export function init() {
  const list = document.getElementById('suit-list');

  list.addEventListener('click', e => {
    const card = e.target.closest('[data-suit]');
    if (card) actions.selectSuit(parseInt(card.dataset.suit, 10));
  });

  list.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); actions.moveSelection(1); }
    if (e.key === 'ArrowUp') { e.preventDefault(); actions.moveSelection(-1); }
  });

  const pill = document.getElementById('new-suits-pill');
  pill.addEventListener('click', () => {
    list.scrollTop = 0;
    newBelowFold = 0;
    pill.hidden = true;
  });

  list.addEventListener('scroll', () => {
    if (list.scrollTop <= 8 && newBelowFold > 0) {
      newBelowFold = 0;
      pill.hidden = true;
    }
  });

  subscribe('suits', sync);
  subscribe('suit-selected', renderSelection);
}

function cardHtml(suit) {
  const chips = [
    `<span class="chip chip--mono">${suit.totalEffectiveLegendaries} / ${suit.totalEffectiveEpics} / ${suit.totalEffectiveMajors}</span>`,
    ...suit.setCounts.map(sc => `<span class="chip">${escapeHtml(sc.name.replace(/ Set$/, ''))} ${sc.count}</span>`),
    suit.totalSetTinkers > 0
      ? `<span class="chip chip--tink">${suit.totalSetTinkers} tink${suit.totalSetTinkers === 1 ? '' : 's'}</span>`
      : '<span class="suit-card__notink">no tinks</span>',
  ];

  return '<div class="suit-card__top">' +
    `<span class="suit-card__pieces">${suit.count} piece${suit.count === 1 ? '' : 's'}${suit.isBaseSuit ? ' · locked base' : ''}</span>` +
    `<span class="suit-card__al">AL ${suit.totalBaseArmorLevel}</span>` +
    '</div>' +
    `<div class="suit-card__chips">${chips.join('')}</div>`;
}

function makeCard(suit) {
  const el = document.createElement('div');
  el.className = 'suit-card';
  el.dataset.suit = suit.suitId;
  el.setAttribute('role', 'option');
  el.innerHTML = cardHtml(suit);
  return el;
}

function sync() {
  const state = get();
  const list = document.getElementById('suit-list');
  const suits = state.search.suits;
  const wanted = new Set(suits.map(s => s.suitId));

  // Remove evicted cards
  for (const [suitId, el] of cards) {
    if (!wanted.has(suitId)) {
      el.remove();
      cards.delete(suitId);
    }
  }

  // Walk the sorted array; ensure DOM order matches by inserting missing cards
  const scrolled = list.scrollTop > 8;
  let cursor = list.firstElementChild;

  for (const suit of suits) {
    let el = cards.get(suit.suitId);

    if (el) {
      if (el !== cursor) list.insertBefore(el, cursor); // repair ordering drift (rare)
      cursor = el.nextElementSibling;
    } else {
      el = makeCard(suit);
      cards.set(suit.suitId, el);
      list.insertBefore(el, cursor);

      if (scrolled && el.offsetTop < list.scrollTop)
        newBelowFold++;
    }
  }

  if (newBelowFold > 0) {
    const pill = document.getElementById('new-suits-pill');
    pill.textContent = '↑ ' + newBelowFold + ' new suit' + (newBelowFold === 1 ? '' : 's');
    pill.hidden = false;
  }

  document.getElementById('suit-count').textContent =
    suits.length > 0 ? suits.length + (suits.length >= 200 ? ' (top 200)' : '') : '';

  renderSelection();
}

function renderSelection() {
  const selectedId = get().search.selectedSuitId;

  for (const [suitId, el] of cards) {
    const selected = suitId === selectedId;
    el.classList.toggle('selected', selected);
    el.setAttribute('aria-selected', selected);
  }
}

export function reset() {
  cards.clear();
  newBelowFold = 0;
  document.getElementById('suit-list').innerHTML = '';
  document.getElementById('new-suits-pill').hidden = true;
}
