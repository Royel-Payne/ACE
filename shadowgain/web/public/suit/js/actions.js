// All state mutations live here. Components call actions; renders react to notifications.

import { get, patch, notify } from './state.js';
import { api, openEvents, closeEvents } from './api.js';
import { save } from './persist.js';
import { compareSuits } from './format.js';
import { toast } from './components/toast.js';

export function charKey(server, character) {
  return server + '||' + character;
}

// ---- View ----

export function setView(view) {
  patch({ view }, 'view');
  location.hash = view;
}

// ---- Data loading ----

export async function loadAll() {
  try {
    const [inventory, cantrips] = await Promise.all([api.inventory(), api.cantrips()]);
    reconcile(inventory);
    patch({ inventory, cantrips }, 'inventory', 'cantrips');
  } catch (err) {
    toast('Failed to load inventory: ' + err.message, true);
  }

  // Resume view of an existing search after a page reload
  try {
    const status = await api.status();
    if (status) {
      const state = get();
      state.search.status = status.state === 'Running' || status.state === 'Stopping' ? 'running' : status.state.toLowerCase();
      state.search.startedAt = Date.now() - status.elapsedSeconds * 1000;
      openSearchEvents();
      notify('search-status', 'suits');
    }
  } catch {
    // No search yet — fine
  }
}

// Browser build: parse dropped/picked Mag-Tools XML files (entries: [{path, file}])
export async function loadInventoryFiles(entries) {
  try {
    const inventory = await api.loadFiles(entries);
    reconcile(inventory);
    patch({ inventory }, 'inventory');
    const count = inventory.servers.reduce((n, s) => n + s.characters.reduce((m, c) => m + c.items.length, 0), 0);
    toast('Loaded ' + count.toLocaleString() + ' items from ' + entries.length + ' file' + (entries.length === 1 ? '' : 's'));
  } catch (err) {
    toast(err.message, true);
  }
}

export async function reloadInventory() {
  try {
    const inventory = await api.reloadInventory();
    reconcile(inventory);
    patch({ inventory }, 'inventory');
    toast('Inventory reloaded');
  } catch (err) {
    toast(err.message, true);
  }
}

function reconcile(inventory) {
  // Drop persisted character selections that no longer exist. An empty/failed load must NOT
  // wipe the user's saved selection — skip reconciling until real data arrives.
  if (!inventory?.servers?.length) return;

  const state = get();
  const known = new Set();
  for (const server of inventory.servers)
    for (const character of server.characters)
      known.add(charKey(server.name, character.name));

  state.filters.characters = new Set([...state.filters.characters].filter(k => known.has(k)));
}

// ---- Filters / sort ----

export function toggleCharacter(key) {
  const { filters } = get();
  filters.characters.has(key) ? filters.characters.delete(key) : filters.characters.add(key);
  notify('filters');
  save(get());
}

// Server header checkbox: all tracked -> untrack all; otherwise track every character on it
export function toggleServer(serverName) {
  const state = get();
  const server = state.inventory?.servers.find(s => s.name === serverName);
  if (!server) return;

  const keys = server.characters.map(c => charKey(serverName, c.name));
  const allOn = keys.every(k => state.filters.characters.has(k));

  for (const key of keys)
    allOn ? state.filters.characters.delete(key) : state.filters.characters.add(key);

  notify('filters');
  save(state);
}

export function toggleItemType(type) {
  const { filters } = get();
  filters.itemTypes.has(type) ? filters.itemTypes.delete(type) : filters.itemTypes.add(type);
  notify('filters');
  save(get());
}

export function setALRange(min, max) {
  const { filters } = get();
  filters.minCoreAL = Number.isFinite(min) ? min : 0;
  filters.maxCoreAL = Number.isFinite(max) ? max : 9999;
  notify('filters');
  save(get());
}

export function setTextFilter(text) {
  get().filters.text = text;
  notify('filters');
}

export function setSort(col) {
  const { sort } = get();
  if (sort.col === col) sort.dir = -sort.dir;
  else { sort.col = col; sort.dir = 1; }
  notify('filters');
}

// ---- Item flags ----

export function toggleFlag(itemKey, flag) {
  const state = get();
  let target = null;

  for (const server of state.inventory?.servers ?? [])
    for (const character of server.characters)
      for (const item of character.items)
        if (item.itemKey === itemKey) target = item;

  if (!target) return;

  const patchBody = {};
  if (flag === 'locked') {
    patchBody.locked = !target.locked;
    target.locked = !target.locked;
    if (target.locked && target.excluded) { target.excluded = false; patchBody.excluded = false; }
  } else {
    patchBody.excluded = !target.excluded;
    target.excluded = !target.excluded;
    if (target.excluded && target.locked) { target.locked = false; patchBody.locked = false; }
  }

  notify('item-flag:' + itemKey, 'flags');
  api.setFlags(itemKey, patchBody).catch(err => toast(err.message, true));
}

// ---- Build config ----

export function setBuild(partial) {
  Object.assign(get().build, partial);
  notify('build');
  save(get());
}

export function setCantrip(familyKey, level) {
  const { build } = get();
  if (level) build.cantrips.set(familyKey, level);
  else build.cantrips.delete(familyKey);
  build.presetEdited = !!build.activePreset;
  notify('build');
  save(get());
}

export function applyPreset(name) {
  const state = get();
  const preset = state.cantrips?.presets?.[name];
  state.build.cantrips = new Map();

  if (preset)
    for (const familyKey of preset)
      state.build.cantrips.set(familyKey, 'legendary');

  state.build.activePreset = preset ? name : '';
  state.build.presetEdited = false;
  notify('build');
  save(get());
}

// ---- Search lifecycle ----

let elapsedTimer = 0;
let suitBuffer = [];
let flushScheduled = false;

export async function startSearch() {
  const state = get();

  if (state.search.status === 'running') return;
  if (state.filters.characters.size === 0) { toast('Select at least one character first', true); return; }
  if (state.build.cantrips.size === 0) {
    toast('Pick the cantrips to hunt for (or load a preset) — the search optimizes for your selection.', true);
    return;
  }

  const request = {
    characters: [...state.filters.characters].map(key => {
      const [server, character] = key.split('||');
      return { server, character };
    }),
    primaryArmorSetId: state.build.primarySetId,
    secondaryArmorSetId: state.build.secondarySetId,
    allowSetTransfers: state.build.allowTransfers,
    cantrips: [...state.build.cantrips].map(([familyKey, level]) => ({ familyKey, level })),
    filters: {
      removeEquipped: false,
      removeUnequipped: false,
      minBaseArmorLevel: state.filters.minCoreAL,
      maxBaseArmorLevel: state.filters.maxCoreAL,
      minExtremityArmorLevel: 0,
      maxExtremityArmorLevel: 9999,
      includeBodyArmorClothing: state.filters.itemTypes.has('armor'),
      includeShirtsPants: state.filters.itemTypes.has('underwear'),
      includeJewelry: state.filters.itemTypes.has('jewelry'),
      jewelryNecklace: true,
      jewelryTrinket: true,
      jewelryBracelet: true,
      jewelryRing: true,
      minLegendaries: 0,
      maxLegendaries: 99,
      minEpics: 0,
      maxEpics: 99,
    },
  };

  try {
    const result = await api.startSearch(request);
    Object.assign(state.search, {
      status: 'running',
      searchId: result.searchId,
      startedAt: Date.now(),
      elapsed: 0,
      suits: [],
      selectedSuitId: null,
    });
    openSearchEvents();
    setView('results');
    notify('suits', 'search-status', 'suit-selected');

    clearInterval(elapsedTimer);
    elapsedTimer = setInterval(() => {
      const search = get().search;
      if (search.status !== 'running' && search.status !== 'stopping') { clearInterval(elapsedTimer); return; }
      search.elapsed = (Date.now() - search.startedAt) / 1000;
      notify('search-status');
    }, 1000);
  } catch (err) {
    toast(err.message, true);
  }
}

export async function stopSearch() {
  try {
    get().search.status = 'stopping';
    notify('search-status');
    await api.stopSearch();
  } catch (err) {
    toast(err.message, true);
  }
}

export function openSearchEvents() {
  openEvents({
    snapshot(data) {
      const state = get();
      const suits = (data.suits ?? []).slice().sort(compareSuits);
      state.search.suits = suits;
      if (data.status) {
        state.search.status = mapState(data.status.state);
        state.search.startedAt = Date.now() - data.status.elapsedSeconds * 1000;
        state.search.elapsed = data.status.elapsedSeconds;
      }
      if (!state.search.selectedSuitId && suits.length > 0)
        state.search.selectedSuitId = suits[0].suitId;
      notify('suits', 'search-status', 'suit-selected');
    },

    suit(dto) {
      suitBuffer.push(dto);
      if (!flushScheduled) {
        flushScheduled = true;
        requestAnimationFrame(flushSuits);
      }
    },

    'suit-evicted'(data) {
      const search = get().search;
      const index = search.suits.findIndex(s => s.suitId === data.suitId);
      if (index >= 0) {
        search.suits.splice(index, 1);

        if (search.selectedSuitId === data.suitId) {
          search.selectedSuitId = search.suits[0]?.suitId ?? null;
          notify('suits', 'suit-selected');
        } else
          notify('suits');
      }
    },

    progress(status) {
      const search = get().search;
      search.elapsed = status.elapsedSeconds;
      notify('search-status');
    },

    warning(data) {
      toast(data.message, true);
    },

    completed(status) {
      const state = get();
      state.search.status = mapState(status.state);
      state.search.elapsed = status.elapsedSeconds;
      state.search.lastSummary = { suits: status.suitsFound, seconds: Math.round(status.elapsedSeconds) };
      save(state);
      notify('search-status');
    },

    error() {
      const search = get().search;
      if (search.status === 'running' || search.status === 'stopping') {
        // EventSource auto-reconnects; the snapshot event restores consistency.
      }
    },
  });
}

function mapState(serverState) {
  switch (serverState) {
    case 'Running': case 'Preparing': return 'running';
    case 'Stopping': return 'stopping';
    case 'Aborted': return 'aborted';
    case 'Completed': return 'completed';
    default: return 'idle';
  }
}

function flushSuits() {
  flushScheduled = false;
  const state = get();
  const pending = suitBuffer;
  suitBuffer = [];

  for (const dto of pending) {
    const suits = state.search.suits;
    if (suits.some(s => s.suitId === dto.suitId)) continue;

    // binary insert by rank
    let lo = 0, hi = suits.length;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (compareSuits(dto, suits[mid]) < 0) hi = mid;
      else lo = mid + 1;
    }
    suits.splice(lo, 0, dto);
  }

  // Only re-render the detail panels when the selection actually changed — during streaming,
  // rebuilding them on every batch of incoming suits is what makes the UI feel sluggish.
  if (!state.search.selectedSuitId && state.search.suits.length > 0) {
    state.search.selectedSuitId = state.search.suits[0].suitId;
    notify('suits', 'suit-selected');
  } else
    notify('suits');
}

export function selectSuit(suitId) {
  get().search.selectedSuitId = suitId;
  notify('suit-selected');
}

export function selectedSuit() {
  const search = get().search;
  return search.suits.find(s => s.suitId === search.selectedSuitId) ?? null;
}

export function moveSelection(delta) {
  const search = get().search;
  if (search.suits.length === 0) return;
  const index = search.suits.findIndex(s => s.suitId === search.selectedSuitId);
  const next = Math.min(search.suits.length - 1, Math.max(0, (index < 0 ? 0 : index + delta)));
  selectSuit(search.suits[next].suitId);
}

export function shutdownEvents() {
  closeEvents();
}
