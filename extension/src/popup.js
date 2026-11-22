// Bookmark Sync popup: last sync result, Sync now, and a button that
// opens the Bookmark Sync window on this computer.

const api = globalThis.browser ?? globalThis.chrome;
const HOST = 'com.muk1.bookmarksync';

// Shows the last sync result and how long ago it was.
async function render() {
  const { status } = await api.storage.local.get('status');
  const box = document.getElementById('status');
  if (!status) {
    box.textContent = 'Not synced yet';
    box.className = 'status';
    return;
  }
  const minutes = Math.round((Date.now() - status.at) / 60000);
  const when = minutes < 1 ? 'just now' : `${minutes} min ago`;
  box.textContent = `${status.message} (${when})`;
  box.className = status.ok ? 'status ok' : 'status bad';
}

document.getElementById('sync').addEventListener('click', async () => {
  document.getElementById('status').textContent = 'Syncing...';
  await api.runtime.sendMessage('sync-now');
  setTimeout(render, 2500);
});

document.getElementById('open').addEventListener('click', async () => {
  try {
    await api.runtime.sendNativeMessage(HOST, { type: 'open' });
    window.close();
  } catch {
    const box = document.getElementById('status');
    box.textContent = 'Bookmark Sync is not installed on this computer. Run BookmarkSync.exe once to install it.';
    box.className = 'status bad';
  }
});

// The ? button shows what each part of this window does.
document.getElementById('helpButton').addEventListener('click', () => {
  const box = document.getElementById('helpText');
  if (!box.hidden) {
    box.hidden = true;
    return;
  }
  box.replaceChildren(...HELP.map(([term, text]) => {
    const row = document.createElement('p');
    const bold = document.createElement('strong');
    bold.textContent = `${term}: `;
    row.append(bold, text);
    return row;
  }));
  box.hidden = false;
});

// "v0.5.0 - 1a2b3c4d": the version and the first 8 characters of a checksum of this extension's script.
async function showVersion() {
  const version = api.runtime.getManifest().version;
  let label = `v${version}`;
  try {
    const bytes = await (await fetch(api.runtime.getURL('background.js'))).arrayBuffer();
    const hash = await crypto.subtle.digest('SHA-256', bytes);
    label += ` - ${[...new Uint8Array(hash)].slice(0, 4).map((b) => b.toString(16).padStart(2, '0')).join('')}`;
  } catch {
    // The label is cosmetic; the version alone is enough.
  }
  document.getElementById('version').textContent = label;

  // The extension and the program share one version number; say so when they differ.
  try {
    const reply = await api.runtime.sendNativeMessage(HOST, { type: 'status' });
    if (reply?.version && reply.version !== version) {
      const box = document.getElementById('mismatch');
      box.textContent = `The program is v${reply.version} but this extension is v${version}. Reload the extension on the browser's extensions page.`;
      box.hidden = false;
    }
  } catch {
    // Not installed: the Open button already explains that.
  }
}
showVersion();
api.storage.onChanged.addListener(render);
render();
