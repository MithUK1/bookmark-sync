// Bookmark Sync background script.
//
// Sends this browser's bookmarks to the Bookmark Sync helper on this
// computer, gets the master list back and changes this browser's bookmarks
// to match. The helper is started by the browser for each request and
// exits straight after, so nothing runs in between.

const api = globalThis.browser ?? globalThis.chrome;
const HOST = 'com.muk1.bookmarksync';
const SEP = '\x1f';
const KEY_SEP = '\x1e';
const ALLOWED_SCHEMES = ['http:', 'https:', 'ftp:', 'file:', 'javascript:'];
const TRACKING = /^(utm_|fbclid$|gclid$|mc_eid$)/;
const POLL_MINUTES = 5;
const CHANGE_DELAY_MS = 4000;

// Browser differences in one place: the ids of the two areas that sync
// (bookmarks bar and other bookmarks) and where new items go.
const ROOT_LABEL = {
  '1': 'toolbar',
  '2': 'other',
  'toolbar_____': 'toolbar',
  'menu________': 'other',
  'unfiled_____': 'other',
};
const CREATE_ROOT_IDS = {
  toolbar: ['1', 'toolbar_____'],
  other: ['2', 'unfiled_____'],
};

const BROWSER = detectBrowser();
let running = false;
let again = false;
let applying = false;
let timer = null;

// Works out which browser this is, so the helper can tell them apart.
function detectBrowser() {
  if (typeof browser !== 'undefined' && api.runtime.getBrowserInfo) return 'firefox';
  return /Edg\//.test(navigator.userAgent) ? 'edge' : 'chrome';
}

// Only real addresses sync. Browser pages such as chrome:// never do.
function syncable(url) {
  try {
    return ALLOWED_SCHEMES.includes(new URL(url).protocol);
  } catch {
    return false;
  }
}

// Same rules as Get-NormalUrl in BookmarkSync.ps1, change them together.
// Ignores http/https, host case, www., default ports, trailing slashes,
// tracking parameters and the #fragment.
function normUrl(url) {
  const text = url.trim();
  const match = /^(https?):\/\/([^/?#]*)([^?#]*)(\?[^#]*)?/i.exec(text);
  if (!match) return text;
  let host = match[2].toLowerCase();
  host = host.slice(host.lastIndexOf('@') + 1);
  if (host.startsWith('www.')) host = host.slice(4);
  host = host.replace(/:(80|443)$/, '');
  const path = match[3].replace(/\/+$/, '');
  let query = '';
  if (match[4]) {
    const kept = match[4].slice(1).split('&').filter((pair) => pair && !TRACKING.test(pair.split('=')[0].toLowerCase()));
    if (kept.length) query = '?' + kept.join('&');
  }
  return `//${host}${path}${query}`;
}

// A bookmark's identity: its folder path plus its normalised address.
function keyOf(path, url) {
  return path.join(SEP) + KEY_SEP + normUrl(url);
}

// Walks one folder, collecting bookmarks and sub-folders with their paths.
function collect(node, path, out) {
  for (const child of node.children ?? []) {
    if (child.url !== undefined) {
      if (syncable(child.url)) {
        out.bookmarks.push({ id: child.id, url: child.url, title: child.title ?? '', path, index: child.index });
      }
    } else if (child.children) {
      const childPath = [...path, child.title];
      out.folders.push({ id: child.id, path: childPath });
      collect(child, childPath, out);
    }
  }
}

// Reads every bookmark and folder in the two areas that sync.
async function snapshot() {
  const [tree] = await api.bookmarks.getTree();
  const out = { bookmarks: [], folders: [] };
  for (const root of tree.children) {
    const label = ROOT_LABEL[root.id];
    if (label) collect(root, [label], out);
  }
  return out;
}

// What the helper needs from a snapshot, without browser ids.
function payload(snap) {
  return {
    browser: BROWSER,
    bookmarks: snap.bookmarks.map(({ url, title, path, index }) => ({ url, title, path, index })),
    folders: snap.folders.map((folder) => folder.path),
  };
}

// Sends one request to the helper and returns its reply.
async function callHost(message) {
  const reply = await api.runtime.sendNativeMessage(HOST, message);
  if (!reply) throw new Error('No reply from Bookmark Sync');
  if (!reply.ok) throw new Error(reply.error);
  return reply;
}

// Turns base64 pieces back into one block of text.
function joinParts(parts) {
  const chunks = parts.map((part) => Uint8Array.from(atob(part), (c) => c.charCodeAt(0)));
  const bytes = new Uint8Array(chunks.reduce((total, chunk) => total + chunk.length, 0));
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.length;
  }
  return new TextDecoder().decode(bytes);
}

// The master list can be bigger than one reply, so the rest is fetched
// piece by piece after the first.
async function fetchMaster(first) {
  const parts = [first.data];
  for (let part = 1; part < first.parts; part++) {
    const reply = await callHost({ type: 'part', browser: BROWSER, part });
    parts.push(reply.data);
  }
  const master = JSON.parse(joinParts(parts));
  return { bookmarks: master.bookmarks ?? [], folders: master.folders ?? [] };
}

// Changes this browser's bookmarks to match the master list: removes what
// is not in it, adds what is missing, creates folders as needed, then
// removes folders left empty that the master does not have.
async function apply(target) {
  const stats = { created: 0, deleted: 0, errors: 0 };
  const wanted = new Map(target.bookmarks.map((bm) => [keyOf(bm.path, bm.url), bm]));
  const wantedFolders = new Set(target.folders.map((folder) => folder.join(SEP)));

  const before = await snapshot();
  const seen = new Set();
  for (const bm of before.bookmarks) {
    const key = keyOf(bm.path, bm.url);
    if (wanted.has(key) && !seen.has(key)) {
      seen.add(key);
      continue;
    }
    try {
      await api.bookmarks.remove(bm.id);
      stats.deleted++;
    } catch {
      stats.errors++;
    }
  }

  const afterDelete = await snapshot();
  const folderIds = new Map();
  for (const folder of afterDelete.folders) {
    const key = folder.path.join(SEP);
    if (!folderIds.has(key)) folderIds.set(key, folder.id);
  }

  const [tree] = await api.bookmarks.getTree();
  const rootIds = {};
  for (const label of Object.keys(CREATE_ROOT_IDS)) {
    rootIds[label] = tree.children.find((child) => CREATE_ROOT_IDS[label].includes(child.id))?.id;
  }

  // Returns the id of the folder at this path, creating any missing levels.
  async function ensureFolder(path) {
    if (path.length === 1) return rootIds[path[0]];
    const key = path.join(SEP);
    if (folderIds.has(key)) return folderIds.get(key);
    const parentId = await ensureFolder(path.slice(0, -1));
    const created = await api.bookmarks.create({ parentId, title: path[path.length - 1] });
    folderIds.set(key, created.id);
    return created.id;
  }

  const have = new Set(afterDelete.bookmarks.map((bm) => keyOf(bm.path, bm.url)));
  const missing = target.bookmarks
    .filter((bm) => !have.has(keyOf(bm.path, bm.url)))
    .sort((a, b) => a.path.join(SEP).localeCompare(b.path.join(SEP)) || a.index - b.index);

  for (const bm of missing) {
    try {
      const parentId = await ensureFolder(bm.path);
      await api.bookmarks.create({ parentId, title: bm.title, url: bm.url });
      stats.created++;
    } catch {
      stats.errors++;
    }
  }

  for (const folder of target.folders) {
    try {
      await ensureFolder(folder);
    } catch {
      stats.errors++;
    }
  }

  await removeEmptyFolders(wantedFolders, stats);
  return stats;
}

// Removes empty folders the master list does not have, deepest first.
async function removeEmptyFolders(wantedFolders, stats) {
  const [tree] = await api.bookmarks.getTree();
  const empties = [];
  function walk(node, path) {
    for (const child of node.children ?? []) {
      if (child.url !== undefined || !child.children) continue;
      const childPath = [...path, child.title];
      walk(child, childPath);
      if (child.children.length === 0 && !wantedFolders.has(childPath.join(SEP))) empties.push(child.id);
    }
  }
  for (const root of tree.children) {
    const label = ROOT_LABEL[root.id];
    if (label) walk(root, [label]);
  }
  for (const id of empties) {
    try {
      await api.bookmarks.remove(id);
    } catch {
      stats.errors++;
    }
  }
}

// Turns a helper error into a plain message for the popup.
function describeError(err) {
  const text = String(err?.message ?? err);
  if (/not found|no such native application|not installed/i.test(text)) {
    return 'Bookmark Sync is not installed on this computer. Run BookmarkSync.ps1 to install it.';
  }
  return `Bookmark Sync could not sync: ${text}`;
}

// Saves the last result for the popup and shows a red ! on failure.
async function setStatus(ok, message) {
  await api.storage.local.set({ status: { ok, message, at: Date.now() } });
  await api.action.setBadgeText({ text: ok ? '' : '!' });
  if (!ok) await api.action.setBadgeBackgroundColor({ color: '#b40000' });
}

// One full sync. A change made while a sync is running triggers one more
// round straight after, rather than two syncs at once.
async function sync() {
  if (running) {
    again = true;
    return;
  }
  running = true;
  try {
    do {
      again = false;
      const first = await callHost({ type: 'sync', ...payload(await snapshot()) });
      if (first.unchanged) {
        await setStatus(true, 'Up to date');
        continue;
      }
      const target = await fetchMaster(first);
      applying = true;
      let stats;
      try {
        stats = await apply(target);
      } finally {
        applying = false;
      }
      await callHost({ type: 'ack', ...payload(await snapshot()), stats });
      await setStatus(true, `Synced, ${stats.created} added, ${stats.deleted} removed`);
    } while (again);
  } catch (err) {
    await setStatus(false, describeError(err));
  } finally {
    running = false;
  }
}

// Waits a few seconds after a change so a burst of edits syncs once. Our
// own changes while applying the master list are ignored.
function scheduleSync() {
  if (applying) return;
  clearTimeout(timer);
  timer = setTimeout(sync, CHANGE_DELAY_MS);
}

for (const event of ['onCreated', 'onRemoved', 'onChanged', 'onMoved', 'onImportEnded']) {
  api.bookmarks[event]?.addListener(scheduleSync);
}

api.alarms.create('poll', { periodInMinutes: POLL_MINUTES });
api.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'poll') sync();
});
api.runtime.onStartup.addListener(sync);
api.runtime.onInstalled.addListener(sync);
api.runtime.onMessage.addListener((message) => {
  if (message === 'sync-now') sync();
});
