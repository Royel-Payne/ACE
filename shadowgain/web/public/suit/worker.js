// The .NET runtime lives HERE, inside a Web Worker — the page's UI thread never runs .NET and
// can never freeze. Suit/progress events stream out via postMessage even while a search has
// this worker's loop pegged.

import { dotnet } from './_framework/dotnet.js';

const runtime = await dotnet.create();

runtime.setModuleImports('worker', {
  events: {
    emit: (type, json) => self.postMessage({ kind: 'event', type, json }),
  },
});

const config = runtime.getConfig();
const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
const api = exports.MagSuitBuilderWasm.WasmApi;

self.postMessage({ kind: 'ready' });

self.onmessage = ({ data: m }) => {
  let result = null;

  try {
    switch (m.cmd) {
      case 'loadInventory': result = api.LoadInventory(m.paths, m.contents); break;
      case 'cantrips': result = api.GetCantrips(); break;
      case 'setFlags': result = JSON.stringify(api.SetItemFlags(m.itemKey, !!m.locked, !!m.excluded, m.locked != null, m.excluded != null)); break;
      case 'startSearch': api.StartSearch(m.requestJson); break;
      case 'stopSearch': api.StopSearch(); break;
      case 'status': result = api.GetStatus(); break;
      default: throw new Error('Unknown command: ' + m.cmd);
    }

    self.postMessage({ kind: 'reply', id: m.id, json: result });
  } catch (err) {
    self.postMessage({ kind: 'reply', id: m.id, error: String(err?.message ?? err) });
  }
};
