import { get, subscribe } from '../state.js';
import * as actions from '../actions.js';
import { elapsedText, suitText } from '../format.js';
import { toast } from './toast.js';

const ICONS = {
  reload: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-2.6-6.3"></path><path d="M21 3v6h-6"></path></svg>',
  play: '<svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor" stroke="none"><path d="M8 5v14l11-7z"></path></svg>',
  stop: '<svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor" stroke="none"><rect x="6" y="6" width="12" height="12" rx="1"></rect></svg>',
  copy: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="11" height="11" rx="2"></rect><path d="M5 15V5a2 2 0 0 1 2-2h10"></path></svg>',
};

export function init() {
  const seg = document.getElementById('view-seg');
  seg.addEventListener('click', e => {
    const btn = e.target.closest('[data-nav]');
    if (btn) actions.setView(btn.dataset.nav);
  });

  document.getElementById('topbar-actions').addEventListener('click', e => {
    const btn = e.target.closest('[data-action]');
    if (!btn) return;

    switch (btn.dataset.action) {
      case 'reload': actions.reloadInventory(); break;
      case 'calculate': actions.startSearch(); break;
      case 'stop': actions.stopSearch(); break;
      case 'copy-suit': {
        const suit = actions.selectedSuit();
        if (suit) navigator.clipboard.writeText(suitText(suit)).then(() => toast('Suit copied'));
        break;
      }
      case 'export': {
        const suit = actions.selectedSuit();
        if (!suit) break;
        const blob = new Blob([suitText(suit)], { type: 'text/plain' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'suit.txt';
        a.click();
        URL.revokeObjectURL(a.href);
        break;
      }
    }
  });

  subscribe(['view', 'inventory', 'search-status'], render);
  render();
}

function render() {
  const state = get();
  const { view, search } = state;

  for (const btn of document.querySelectorAll('#view-seg .seg__btn'))
    btn.classList.toggle('active', btn.dataset.nav === view);

  const serverChip = document.getElementById('server-chip');
  const servers = state.inventory?.servers ?? [];
  if (servers.length > 0) {
    serverChip.hidden = false;
    serverChip.innerHTML = '<span class="status-dot status-dot--ok"></span>' +
      escapeHtml(servers.map(s => s.name).join(' · '));
  } else {
    serverChip.hidden = true;
  }

  document.getElementById('search-box').hidden = view !== 'build';

  const status = document.getElementById('topbar-status');
  if (view === 'results' && search.status !== 'idle') {
    const dotClass = search.status === 'running' || search.status === 'stopping' ? 'status-dot--running'
      : search.status === 'completed' ? 'status-dot--ok'
      : search.status === 'error' ? 'status-dot--err' : '';
    const word = { running: 'Searching', stopping: 'Stopping', completed: 'Search complete', aborted: 'Stopped', error: 'Error' }[search.status] ?? '';
    status.innerHTML = `<span class="status-dot ${dotClass}"></span><span>${word} · ${search.suits.length} suits · ${elapsedText(search.elapsed)}</span>`;
  } else {
    status.innerHTML = '';
  }

  // Memoized: the status line above ticks every second, but the buttons only change on
  // view/status transitions — rebuilding them each tick resets hover state and wastes work.
  const actionsEl = document.getElementById('topbar-actions');
  const actionsKey = view + '|' + search.status;

  if (actionsEl.dataset.key !== actionsKey) {
    actionsEl.dataset.key = actionsKey;

    if (view === 'build') {
      actionsEl.innerHTML =
        `<button class="btn" data-load-inventory>${ICONS.reload}<span>Load inventory…</span></button>` +
        `<button class="btn btn--primary" data-action="calculate">${ICONS.play}<span>Calculate suits</span></button>`;
    } else if (search.status === 'running' || search.status === 'stopping') {
      actionsEl.innerHTML =
        `<button class="btn btn--primary" data-action="stop" ${search.status === 'stopping' ? 'disabled' : ''}>${ICONS.stop}<span>Stop</span></button>`;
    } else {
      actionsEl.innerHTML =
        `<button class="btn" data-action="export"><span>Export</span></button>` +
        `<button class="btn btn--primary" data-action="copy-suit">${ICONS.copy}<span>Copy suit</span></button>`;
    }
  }
}

export function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
