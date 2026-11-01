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
    box.textContent = 'Bookmark Sync is not installed on this computer. Run BookmarkSync.ps1 to install it.';
    box.className = 'status bad';
  }
});

document.getElementById('version').textContent = `v${api.runtime.getManifest().version} - MUK1`;
api.storage.onChanged.addListener(render);
render();
