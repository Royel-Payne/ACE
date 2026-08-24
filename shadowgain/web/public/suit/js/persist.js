// Versioned localStorage persistence of the user's setup (not search results).

const KEY = 'sb.v1';
let saveTimer = 0;

export function load() {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

export function apply(state) {
  const saved = load();
  if (!saved) return;

  try {
    if (Array.isArray(saved.characters)) state.filters.characters = new Set(saved.characters);
    if (Array.isArray(saved.itemTypes)) state.filters.itemTypes = new Set(saved.itemTypes);
    if (Number.isFinite(saved.minCoreAL)) state.filters.minCoreAL = saved.minCoreAL;
    if (Number.isFinite(saved.maxCoreAL)) state.filters.maxCoreAL = saved.maxCoreAL;
    if (Number.isFinite(saved.primarySetId)) state.build.primarySetId = saved.primarySetId;
    if (Number.isFinite(saved.secondarySetId)) state.build.secondarySetId = saved.secondarySetId;
    if (typeof saved.allowTransfers === 'boolean') state.build.allowTransfers = saved.allowTransfers;
    if (Array.isArray(saved.cantrips)) state.build.cantrips = new Map(saved.cantrips);
    if (typeof saved.activePreset === 'string') state.build.activePreset = saved.activePreset;
    if (saved.lastSummary) state.search.lastSummary = saved.lastSummary;
  } catch {
    // Corrupt save — ignore
  }
}

export function save(state) {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    try {
      localStorage.setItem(KEY, JSON.stringify({
        characters: [...state.filters.characters],
        itemTypes: [...state.filters.itemTypes],
        minCoreAL: state.filters.minCoreAL,
        maxCoreAL: state.filters.maxCoreAL,
        primarySetId: state.build.primarySetId,
        secondarySetId: state.build.secondarySetId,
        allowTransfers: state.build.allowTransfers,
        cantrips: [...state.build.cantrips],
        activePreset: state.build.activePreset,
        lastSummary: state.search.lastSummary,
      }));
    } catch {
      // storage unavailable — fine
    }
  }, 300);
}
