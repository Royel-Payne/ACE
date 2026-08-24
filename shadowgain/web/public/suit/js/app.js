import { get, subscribe } from './state.js';
import * as actions from './actions.js';
import { apply as applyPersisted } from './persist.js';
import * as topbar from './components/topbar.js';
import * as sidebar from './components/sidebar.js';
import * as inventoryTable from './components/inventoryTable.js';
import * as buildPanel from './components/buildPanel.js';
import * as cantripPicker from './components/cantripPicker.js';
import * as suitList from './components/suitList.js';
import * as suitDetail from './components/suitDetail.js';
import * as tinkerPlan from './components/tinkerPlan.js';
import * as fileLoader from './components/fileLoader.js';

applyPersisted(get());

// View switching: both views stay mounted; only `hidden` toggles, so scroll positions survive
function renderView() {
  const { view } = get();
  document.getElementById('view-build').hidden = view !== 'build';
  document.getElementById('view-results').hidden = view !== 'results';
}

subscribe('view', renderView);

window.addEventListener('hashchange', () => {
  const view = location.hash.replace('#', '');
  if ((view === 'build' || view === 'results') && view !== get().view)
    actions.setView(view);
});

topbar.init();
sidebar.init();
inventoryTable.init();
buildPanel.init();
cantripPicker.init();
suitList.init();
suitDetail.init();
tinkerPlan.init();
fileLoader.init();

const initialView = location.hash.replace('#', '');
actions.setView(initialView === 'results' ? 'results' : 'build');

actions.loadAll();

window.addEventListener('beforeunload', () => actions.shutdownEvents());

// Debug handle
window.__sb = { get, actions };
