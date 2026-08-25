// Worker-backed replacement for the local web app's fetch/SSE api.js — same exported surface,
// so actions.js and every component work unchanged. The .NET solver runs inside worker.js.

let worker = null;
let readyPromise = null;
const pending = new Map();
let nextId = 1;
let handlers = null;

let cachedFiles = null;      // { paths: [], contents: [] } — kept for Stop's respawn cycle
let cachedInventory = null;  // last InventoryDto
const flagState = new Map(); // itemKey -> { locked, excluded }, replayed after respawn

function spawn() {
  worker = new Worker(new URL('../worker.js', import.meta.url), { type: 'module' });

  readyPromise = new Promise(resolve => {
    worker.addEventListener('message', function onReady({ data }) {
      if (data.kind === 'ready') {
        worker.removeEventListener('message', onReady);
        resolve();
      }
    });
  });

  worker.onmessage = ({ data: m }) => {
    if (m.kind === 'event') {
      let data = null;
      try { data = JSON.parse(m.json); } catch { return; }
      handlers?.[m.type]?.(data);
    } else if (m.kind === 'reply') {
      const p = pending.get(m.id);
      if (!p) return;
      pending.delete(m.id);
      if (m.error) p.reject(new Error(m.error));
      else p.resolve(m.json ? JSON.parse(m.json) : null);
    }
  };
}

async function call(cmd, args = {}) {
  await readyPromise;
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    worker.postMessage({ cmd, id, ...args });
  });
}

async function replayFlags() {
  for (const [itemKey, flags] of flagState)
    await call('setFlags', { itemKey, locked: flags.locked ?? null, excluded: flags.excluded ?? null });
}

const EMPTY_INVENTORY = { rootPath: '(browser)', loadedAtUtc: null, warnings: [], armorSets: [], servers: [] };

// ---- IndexedDB persistence ----
// The picked folder's XML strings are kept in the browser's own storage so a refresh comes
// back with the inventory already loaded. Local to this machine and origin — nothing leaves
// the browser. Every helper swallows failure (private windows, cleared site data): persistence
// is a convenience, and the folder picker always works without it.

const DB_NAME = 'sb-inventory';
const STORE = 'files';

function idbOpen() {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function idbSave(files) {
  try {
    const db = await idbOpen();
    await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put({ ...files, savedAt: Date.now() }, 'current');
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    });
    db.close();
  } catch { /* persistence unavailable — fine */ }
}

async function idbLoad() {
  try {
    const db = await idbOpen();
    const rec = await new Promise((resolve, reject) => {
      const req = db.transaction(STORE).objectStore(STORE).get('current');
      req.onsuccess = () => resolve(req.result ?? null);
      req.onerror = () => reject(req.error);
    });
    db.close();
    return rec?.paths?.length ? rec : null;
  } catch { return null; }
}

export const api = {
  async inventory() {
    if (cachedInventory) return cachedInventory;

    // Fresh page: bring back the last visit's files from browser storage
    const stored = await idbLoad();
    if (stored) {
      cachedFiles = { paths: stored.paths, contents: stored.contents };
      cachedInventory = await call('loadInventory', cachedFiles);
      cachedInventory.restored = true;
    }
    return cachedInventory ?? EMPTY_INVENTORY;
  },

  // NEW in the browser build: parse dropped/picked Mag-Tools XML files
  async loadFiles(entries) {
    // entries: [{ path, file }] — read everything up front so Stop's respawn can replay
    const paths = entries.map(e => e.path);
    const contents = await Promise.all(entries.map(e => e.file.text()));
    cachedFiles = { paths, contents };
    flagState.clear();
    cachedInventory = await call('loadInventory', cachedFiles);
    idbSave(cachedFiles); // after the worker accepted them — a bad pick never overwrites a good cache
    return cachedInventory;
  },

  async reloadInventory() {
    if (!cachedFiles)
      throw new Error('Load your Mag-Tools inventory first — drag the folder onto the page, or use Load inventory.');
    cachedInventory = await call('loadInventory', cachedFiles);
    await replayFlags();
    return cachedInventory;
  },

  cantrips: () => call('cantrips'),

  async setFlags(itemKey, flags) {
    const current = flagState.get(itemKey) ?? {};
    flagState.set(itemKey, { ...current, ...flags });
    return call('setFlags', { itemKey, locked: flags.locked ?? null, excluded: flags.excluded ?? null });
  },

  async startSearch(request) {
    await call('startSearch', { requestJson: JSON.stringify(request) });
    return { searchId: crypto.randomUUID() };
  },

  // Hard stop: the worker may be deep inside the armor search and unable to process messages,
  // so kill it, respawn, and restore inventory + flags from the cached file contents.
  async stopSearch() {
    for (const p of pending.values()) p.reject(new Error('Search stopped'));
    pending.clear();
    worker.terminate();
    spawn();

    if (cachedFiles) {
      cachedInventory = await call('loadInventory', cachedFiles);
      await replayFlags();
    }

    handlers?.completed?.({ state: 'Aborted', elapsedSeconds: 0, suitsFound: 0 });
    return null;
  },

  status: () => call('status'),
};

export function openEvents(h) {
  handlers = h;
}

export function closeEvents() {
  handlers = null;
}

spawn();
