// Browser-build inventory ingestion: folder picker + drag-drop of the Mag-Tools directory
// (or loose *.Inventory.xml files). Everything is read locally; nothing is uploaded anywhere.

import * as actions from '../actions.js';
import { toast } from './toast.js';

let dirInput;

export function init() {
  dirInput = document.createElement('input');
  dirInput.type = 'file';
  dirInput.webkitdirectory = true;
  dirInput.multiple = true;
  dirInput.hidden = true;
  document.body.appendChild(dirInput);

  dirInput.addEventListener('change', () => {
    const entries = [...dirInput.files]
      .filter(f => /\.Inventory\.xml$/i.test(f.name))
      .map(f => ({ path: f.webkitRelativePath || f.name, file: f }));
    handleEntries(entries);
    dirInput.value = '';
  });

  document.addEventListener('click', e => {
    if (e.target.closest('[data-load-inventory]')) open();
  });

  const overlay = document.createElement('div');
  overlay.className = 'drop-overlay';
  overlay.hidden = true;
  overlay.innerHTML = '<div class="drop-overlay__box">Drop your Mag-Tools folder<br><span>or *.Inventory.xml files — parsed locally, nothing is uploaded</span></div>';
  document.body.appendChild(overlay);

  let dragDepth = 0;

  window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth++;
    overlay.hidden = false;
  });

  window.addEventListener('dragover', e => {
    if (hasFiles(e)) e.preventDefault();
  });

  window.addEventListener('dragleave', () => {
    if (--dragDepth <= 0) {
      dragDepth = 0;
      overlay.hidden = true;
    }
  });

  window.addEventListener('drop', async e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0;
    overlay.hidden = true;
    handleEntries(await collectDropped(e.dataTransfer));
  });
}

export function open() {
  dirInput.click();
}

function hasFiles(e) {
  return [...(e.dataTransfer?.types ?? [])].includes('Files');
}

function handleEntries(entries) {
  if (entries.length === 0) {
    toast('No *.Inventory.xml files found in that selection', true);
    return;
  }
  actions.loadInventoryFiles(entries);
}

function maybeAdd(out, path, file) {
  if (/\.Inventory\.xml$/i.test(path)) out.push({ path, file });
}

async function collectDropped(dataTransfer) {
  const out = [];
  const walkers = [];

  for (const item of dataTransfer.items) {
    const entry = item.webkitGetAsEntry?.();
    if (entry) walkers.push(walk(entry, out));
    else {
      const file = item.getAsFile?.();
      if (file) maybeAdd(out, file.name, file);
    }
  }

  await Promise.all(walkers);
  return out;
}

function walk(entry, out) {
  return new Promise(resolve => {
    if (entry.isFile) {
      entry.file(f => { maybeAdd(out, entry.fullPath.replace(/^\//, ''), f); resolve(); }, () => resolve());
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      const readAll = accumulated => reader.readEntries(async batch => {
        if (batch.length === 0) {
          await Promise.all(accumulated.map(e => walk(e, out)));
          resolve();
        } else
          readAll(accumulated.concat([...batch]));
      }, () => resolve());
      readAll([]);
    } else
      resolve();
  });
}
