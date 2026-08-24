// Pure helpers: slot ordering, level colors, ranking, clipboard text.

export const ARMOR_SLOTS = [
  ['HeadWear', 'Head'],
  ['ChestArmor', 'Chest'],
  ['AbdomenArmor', 'Abdomen'],
  ['UpperArmArmor', 'Upper Arms'],
  ['LowerArmArmor', 'Lower Arms'],
  ['HandWear', 'Hands'],
  ['UpperLegArmor', 'Upper Legs'],
  ['LowerLegArmor', 'Lower Legs'],
  ['FootWear', 'Feet'],
];

export const ACCESSORY_SLOTS = [
  ['ChestWear', 'Shirt'],
  ['UpperLegWear', 'Pants'],
  ['NeckWear', 'Necklace'],
  ['TrinketOne', 'Trinket'],
  ['WristWearLeft', 'Bracelet L'],
  ['WristWearRight', 'Bracelet R'],
  ['FingerWearLeft', 'Ring L'],
  ['FingerWearRight', 'Ring R'],
];

const SLOT_ORDER = new Map([...ARMOR_SLOTS, ...ACCESSORY_SLOTS].map(([key], i) => [key, i]));

// A piece's Slots string can be a multi-flag like "WristWearLeft, WristWearRight"
export function slotSortKey(slots) {
  for (const part of slots.split(',').map(s => s.trim())) {
    if (SLOT_ORDER.has(part)) return SLOT_ORDER.get(part);
  }
  return 99;
}

export function slotLabel(slots) {
  const parts = slots.split(',').map(s => s.trim());
  for (const [key, label] of [...ARMOR_SLOTS, ...ACCESSORY_SLOTS]) {
    if (parts.includes(key)) return label;
  }
  return slots;
}

export function isArmorSlot(slots) {
  const parts = slots.split(',').map(s => s.trim());
  return ARMOR_SLOTS.some(([key]) => parts.includes(key));
}

export function levelClass(level) {
  return 'dot--' + (level || 'off').toLowerCase();
}

export const LEVELS = ['minor', 'major', 'epic', 'legendary'];

export function levelName(level) {
  return level ? level[0].toUpperCase() + level.slice(1) : '';
}

// Rank comparator — must match SuitStore.Compare on the server
export function compareSuits(a, b) {
  if (b.count !== a.count) return b.count - a.count;
  if (b.totalBaseArmorLevel !== a.totalBaseArmorLevel) return b.totalBaseArmorLevel - a.totalBaseArmorLevel;
  if (b.totalEffectiveLegendaries !== a.totalEffectiveLegendaries) return b.totalEffectiveLegendaries - a.totalEffectiveLegendaries;
  if (b.totalEffectiveEpics !== a.totalEffectiveEpics) return b.totalEffectiveEpics - a.totalEffectiveEpics;
  if (a.totalSetTinkers !== b.totalSetTinkers) return a.totalSetTinkers - b.totalSetTinkers;
  return a.suitId - b.suitId;
}

export function elapsedText(seconds) {
  seconds = Math.floor(seconds);
  if (seconds < 60) return seconds + ' s';
  return Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0');
}

// Loot attribute set bonuses (ids 13-30), per the Item Sets wiki page
export const SET_BONUSES = {
  13: 'Light · Heavy · 2H · Dirty Fighting · Recklessness  +  Melee Defense at 4',
  14: 'Creature · Item · Life · War · Void  +  Magic Defense at 4',
  15: 'Missile Weapons  +  Missile Defense at 4',
  16: 'Magic/Melee/Missile Defense · Shield  +  Stamina Rejuv at 4',
  17: 'Armor · Item · Magic Item · Weapon Tinkering  +  Salvaging at 4',
  18: 'Alchemy · Cooking · Fletching · Lockpick  +  Loyalty at 4',
  19: 'Endurance · Strength  +  Health at 4',
  20: 'Coordination · Quickness  +  Stamina at 4',
  21: 'Focus · Willpower · Summoning  +  Mana at 4',
  22: 'Dual Wield · Finesse · Jump · Run · Sneak  +  Coordination at 4',
  23: 'Blade Resistance  +  Fire Resistance at 4',
  24: 'Bludgeon Resistance  +  Cold Resistance at 4',
  25: 'Pierce Resistance  +  Acid Resistance at 4',
  26: 'Fire Resistance  +  Lightning Resistance at 4',
  27: 'Acid Resistance  +  Pierce Resistance at 4',
  28: 'Cold Resistance  +  Bludgeon Resistance at 4',
  29: 'Lightning Resistance  +  Blade Resistance at 4',
  30: 'Strength · Endurance · Coordination · Quickness · Focus · Self',
};

// Effective cantrip coverage laid out on the 7x7 family matrix: for every family, the best level
// the suit achieves (or null). Matching is by spell NAME against the catalog, so alternate spell
// ids that share a cantrip's name still land in the right cell. Cantrips that don't correspond to
// a matrix family are returned separately so nothing is hidden.
export function effectiveCantripMatrix(suit, cantripsDto) {
  const rank = { legendary: 4, epic: 3, major: 2, minor: 1 };
  const nameMap = new Map(); // spell name -> {key, level}

  for (const family of cantripsDto?.families ?? []) {
    for (const level of ['legendary', 'epic', 'major', 'minor']) {
      const entry = family[level];
      if (entry?.name) nameMap.set(entry.name.toLowerCase(), { key: family.key, level });
    }
  }

  const byFamily = new Map(); // familyKey -> level
  const unmatched = new Map(); // family label -> {level, family}
  const levelRank = { Legendary: 'legendary', Epic: 'epic', Major: 'major', Minor: 'minor' };

  for (const piece of suit.pieces) {
    // All cantrips the pieces carry, not just search-relevant ones — bonus coverage that rides
    // along (e.g. Majors on a piece picked for its Epic) counts too, matching the WinForms grid.
    for (const spell of piece.allSpells ?? piece.searchSpells ?? []) {
      const level = levelRank[spell.cantripLevel];
      if (!level) continue;

      const hit = nameMap.get(spell.name.toLowerCase());
      if (hit) {
        const current = byFamily.get(hit.key);
        if (!current || rank[level] > rank[current]) byFamily.set(hit.key, level);
      } else {
        const label = spell.name.replace(/^(Legendary|Epic|Major|Minor)\s+/, '');
        const current = unmatched.get(label);
        if (!current || rank[level] > rank[current.level]) unmatched.set(label, { level, family: label });
      }
    }
  }

  const cells = (cantripsDto?.families ?? []).map(f => ({
    key: f.key,
    name: f.name,
    column: f.column,
    row: f.row,
    level: byFamily.get(f.key) ?? null,
  }));

  return {
    cells,
    covered: byFamily.size + unmatched.size,
    unmatched: [...unmatched.values()].sort((a, b) => rank[b.level] - rank[a.level]),
  };
}

export function suitText(suit) {
  const lines = [];
  lines.push('Suit — ' + suit.display);
  lines.push('');

  const sorted = [...suit.pieces].sort((a, b) => slotSortKey(a.slots) - slotSortKey(b.slots));
  for (const piece of sorted) {
    // The full ItemInfo string is the identity players match against an in-game ID
    let line = '  ' + slotLabel(piece.slots).padEnd(12) + '(' + piece.owner + ') ' + (piece.info ?? piece.name);
    if (piece.isSetTinkeredVariant && piece.effectiveSetName)
      line += '  ** SET TINK -> ' + piece.effectiveSetName + ' **';
    lines.push(line.trimEnd());
    if (piece.isSetTinkeredVariant && piece.donor)
      lines.push('              <- consumes (' + piece.donor.owner + ') ' + (piece.donor.info ?? piece.donor.name));
  }

  const plan = planText(suit);
  if (plan) {
    lines.push('');
    lines.push(plan);
  }

  return lines.join('\n');
}

export function planText(suit) {
  const tinked = suit.pieces.filter(p => p.isSetTinkeredVariant);
  if (tinked.length === 0) return '';

  const lines = ['Required set tinkering (' + tinked.length + ' transfer' + (tinked.length === 1 ? '' : 's') + '):'];
  let step = 1;

  for (const piece of tinked) {
    lines.push('  ' + step + ') ' + piece.name + ' (' + piece.owner + '): ' +
      (piece.originalSetName ?? '—') + ' -> ' + (piece.effectiveSetName ?? '—'));
    for (const instruction of piece.instructions ?? [])
      lines.push('     - ' + instruction);
    lines.push('     Target: (' + piece.owner + ') ' + (piece.info ?? piece.name));
    if (piece.donor)
      lines.push('     Donor:  (' + piece.donor.owner + ') ' + (piece.donor.info ?? piece.donor.name));
    step++;
  }

  return lines.join('\n');
}
