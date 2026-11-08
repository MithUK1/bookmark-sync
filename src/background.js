// Bookmark Sync background script.
//
// Sends this browser's bookmarks to the Bookmark Sync helper on this
// computer, gets the master list back and changes this browser's bookmarks
// to match. The browser keeps one connection open to the helper, which
// watches for changes made by the other browsers and tells this one the
// moment it needs to sync.

const api = globalThis.browser ?? globalThis.chrome;
const HOST = 'com.muk1.bookmarksync';
const SEP = '\x1f';
const KEY_SEP = '\x1e';
const ALLOWED_SCHEMES = ['http:', 'https:', 'ftp:', 'file:', 'javascript:'];
const TRACKING = /^(utm_|fbclid$|gclid$|mc_eid$)/;
// A slow safety check, and the way a lost connection is retried.
const SAFETY_MINUTES = 10;
const RECONNECT_MS = 30 * 1000;
const REQUEST_TIMEOUT_MS = 60 * 1000;
const CHANGE_DELAY_MS = 4000;
// Most moves one sync makes, so a huge reshuffle cannot freeze the browser;
// the rest is done by the next sync a few seconds later.
const MAX_MOVES = 500;
// After this many syncs in a row that change the same things, stop and wait.
const STUCK_LIMIT = 3;
const PAUSE_MS = 15 * 60 * 1000;

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
let dirtyStored = false;
let lastSignature = '';
let stuckCount = 0;
let pausedUntil = 0;
// Whether to ask before a new duplicate stays (set by the helper), and the
// questions waiting for an answer: window id -> bookmark id.
let askDuplicates = false;
let importing = false;
const pendingDupes = new Map();

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

// Same rules as Rules.NormalUrl in the app, change them together.
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

// An item's name inside its folder's order list. Bookmarks go by address,
// folders by name. Same rules as Rules.BookmarkToken and Rules.FolderToken in
// the app.
function bookmarkToken(url) {
  return `b|${normUrl(url)}`;
}

function folderToken(name) {
  return `f|${name}`;
}

// A bookmark's identity: its folder path plus its normalised address.
function keyOf(path, url) {
  return path.join(SEP) + KEY_SEP + normUrl(url);
}

// Walks one folder, collecting bookmarks and sub-folders with their paths.
function collect(node, path, out) {
  const items = [];
  const listed = new Set();
  out.order.push({ path, items });
  for (const child of node.children ?? []) {
    if (child.url !== undefined) {
      if (syncable(child.url)) {
        out.bookmarks.push({ id: child.id, url: child.url, title: child.title ?? '', path, index: child.index });
        const token = bookmarkToken(child.url);
        if (!listed.has(token)) {
          listed.add(token);
          items.push(token);
        }
      }
    } else if (child.children) {
      const token = folderToken(child.title);
      if (!listed.has(token)) {
        listed.add(token);
        items.push(token);
      }
      const childPath = [...path, child.title];
      out.folders.push({ id: child.id, path: childPath });
      collect(child, childPath, out);
    }
  }
}

// Reads every bookmark and folder in the two areas that sync.
async function snapshot() {
  const [tree] = await api.bookmarks.getTree();
  const out = { bookmarks: [], folders: [], order: [] };
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
    order: snap.order,
  };
}

// The open connection to the helper and the requests waiting for a reply.
let port = null;
let nextId = 1;
const waiting = new Map();
let reconnectTimer = null;

// Opens the connection if it is not open and says hello, so the helper
// knows which browser this is and starts watching for changes.
function connect() {
  if (port) return port;
  clearTimeout(reconnectTimer);
  const opened = api.runtime.connectNative(HOST);
  port = opened;
  opened.onMessage.addListener((message) => {
    if (typeof message?.askDuplicates === 'boolean') askDuplicates = message.askDuplicates;
    if (message?.type === 'changed') {
      sync();
      return;
    }
    const entry = waiting.get(message?.id);
    if (!entry) return;
    waiting.delete(message.id);
    clearTimeout(entry.timeout);
    if (!message.ok) entry.reject(new Error(message.error));
    else entry.resolve(message);
  });
  opened.onDisconnect.addListener(() => {
    const reason = api.runtime.lastError?.message ?? 'The connection to Bookmark Sync closed';
    if (port === opened) port = null;
    for (const [id, entry] of waiting) {
      clearTimeout(entry.timeout);
      entry.reject(new Error(reason));
      waiting.delete(id);
    }
    // Try again shortly; if the helper is not installed this just keeps failing quietly.
    reconnectTimer = setTimeout(() => {
      connect();
      sync(true);
    }, RECONNECT_MS);
  });
  opened.postMessage({ type: 'hello', browser: BROWSER, id: 0 });
  return opened;
}

// Sends one request to the helper and returns its reply.
function callHost(message) {
  return new Promise((resolve, reject) => {
    let connection;
    try {
      connection = connect();
    } catch (err) {
      reject(err);
      return;
    }
    const id = nextId++;
    const timeout = setTimeout(() => {
      waiting.delete(id);
      reject(new Error('Bookmark Sync did not answer'));
    }, REQUEST_TIMEOUT_MS);
    waiting.set(id, { resolve, reject, timeout });
    try {
      connection.postMessage({ ...message, id });
    } catch (err) {
      waiting.delete(id);
      clearTimeout(timeout);
      reject(err);
    }
  });
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
  return { bookmarks: master.bookmarks ?? [], folders: master.folders ?? [], order: master.order ?? [] };
}

// Changes this browser's bookmarks to match the master list: removes what
// is not in it, adds what is missing, creates folders as needed, then
// removes folders left empty that the master does not have.
async function apply(target, mirror) {
  const stats = { created: 0, deleted: 0, moved: 0, errors: 0, capped: false };
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

  // With a master browser the titles are copied as well.
  if (mirror) {
    const titled = new Set();
    for (const bm of afterDelete.bookmarks) {
      const key = keyOf(bm.path, bm.url);
      const goal = wanted.get(key);
      if (!goal || titled.has(key)) continue;
      titled.add(key);
      if ((goal.title ?? '') !== bm.title) {
        try {
          await api.bookmarks.update(bm.id, { title: goal.title ?? '' });
          stats.renamed = (stats.renamed ?? 0) + 1;
        } catch {
          stats.errors++;
        }
      }
    }
  }

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
  await reorder(target.order, stats);
  return stats;
}

// Puts each folder's items in the master's order, keeping track of the
// folder's order in memory instead of asking the browser after every move.
// Items the master does not list (browser pages that never sync) are left
// where they are. Stops after MAX_MOVES and leaves the rest to the next sync.
//
// Each item is moved back to just before the one now in its place, never
// forward, so the browsers' different rules for forward moves do not matter.
async function reorder(order, stats) {
  const [tree] = await api.bookmarks.getTree();
  const folders = new Map();
  function walk(node, path) {
    folders.set(path.join(SEP), node);
    for (const child of node.children ?? []) {
      if (child.url === undefined && child.children) walk(child, [...path, child.title]);
    }
  }
  for (const root of tree.children) {
    const label = ROOT_LABEL[root.id];
    if (label) walk(root, [label]);
  }

  for (const entry of order) {
    const folder = folders.get(entry.path.join(SEP));
    if (!folder) continue;
    try {
      const children = folder.children ?? [];
      const idOf = new Map();
      for (const child of children) {
        const token = child.url !== undefined ? (syncable(child.url) ? bookmarkToken(child.url) : null) : folderToken(child.title);
        if (token && !idOf.has(token)) idOf.set(token, child.id);
      }
      const wanted = entry.items.filter((token) => idOf.has(token)).map((token) => idOf.get(token));
      const wantedSet = new Set(wanted);
      const full = children.map((child) => child.id);
      const seq = full.filter((id) => wantedSet.has(id));

      for (let i = 0; i < wanted.length; i++) {
        if (seq[i] === wanted[i]) continue;
        if (stats.moved >= MAX_MOVES) {
          stats.capped = true;
          return;
        }
        const from = full.indexOf(wanted[i]);
        const target = full.indexOf(seq[i]);
        await api.bookmarks.move(wanted[i], { parentId: folder.id, index: target });
        stats.moved++;
        full.splice(from, 1);
        full.splice(full.indexOf(seq[i]), 0, wanted[i]);
        seq.splice(seq.indexOf(wanted[i]), 1);
        seq.splice(i, 0, wanted[i]);
      }

      // One check per folder that the browser agreed with the model.
      if (stats.moved > 0) {
        const actual = (await api.bookmarks.getChildren(folder.id)).map((child) => child.id).filter((id) => wantedSet.has(id));
        if (actual.some((id, i) => id !== wanted[i])) stats.errors++;
      }
    } catch {
      stats.errors++;
    }
  }
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
    return 'Bookmark Sync is not installed on this computer. Run BookmarkSync.exe once to install it.';
  }
  return `Bookmark Sync could not sync: ${text}`;
}

// Saves the last result for the popup and shows a red ! on failure.
async function setStatus(ok, message) {
  // Repeating the same result only needs saving now and then.
  const { status } = await api.storage.local.get('status');
  if (!status || status.ok !== ok || status.message !== message || Date.now() - status.at > 5 * 60 * 1000) {
    await api.storage.local.set({ status: { ok, message, at: Date.now() } });
  }
  await api.action.setBadgeText({ text: ok ? '' : '!' });
  if (!ok) await api.action.setBadgeBackgroundColor({ color: '#b40000' });
}

// One sync. A cheap check asks the helper whether this browser is already
// up to date and nothing changed here; only when that is not so is the whole
// bookmark tree read and exchanged. A change made while a sync is running
// triggers one more round straight after, rather than two at once.
async function sync(force = false) {
  if (running) {
    again = true;
    return;
  }
  if (pendingDupes.size > 0) return;
  if (!force && Date.now() < pausedUntil) return;
  running = true;
  try {
    let full = force;
    do {
      again = false;
      const { dirty } = await api.storage.local.get('dirty');
      const needFull = full || dirty !== false;
      full = false;
      if (!needFull) {
        const quick = await callHost({ type: 'check', browser: BROWSER });
        if (quick.current) {
          await setStatus(true, 'Up to date');
          continue;
        }
      }

      // Changes from here on mark the browser as changed again.
      dirtyStored = false;
      await api.storage.local.set({ dirty: false });
      const first = await callHost({ type: 'sync', ...payload(await snapshot()) });
      if (first.unchanged) {
        stuckCount = 0;
        await setStatus(true, 'Up to date');
        continue;
      }
      const target = await fetchMaster(first);
      applying = true;
      let stats;
      try {
        stats = await apply(target, first.mirror);
      } finally {
        applying = false;
      }
      // A sync that stopped early is reported as not applied, so the next
      // check carries on.
      await callHost({ type: 'ack', ...payload(await snapshot()), stats, revision: stats.capped ? 0 : first.revision });
      const parts = [`${stats.created} added`, `${stats.deleted} removed`, `${stats.moved} moved`];
      if (stats.renamed) parts.push(`${stats.renamed} renamed`);
      await setStatus(true, `Synced, ${parts.join(', ')}${stats.capped ? ', more to do' : ''}`);

      if (stats.capped) {
        stuckCount = 0;
        setTimeout(() => sync(true), 5000);
      } else {
        // The same changes again and again mean this browser cannot match.
        const signature = `${stats.created}/${stats.deleted}/${stats.moved}/${stats.errors}`;
        stuckCount = signature === lastSignature && signature !== '0/0/0/0' ? stuckCount + 1 : 0;
        lastSignature = signature;
        if (stuckCount >= STUCK_LIMIT) {
          pausedUntil = Date.now() + PAUSE_MS;
          stuckCount = 0;
          await setStatus(false, 'This browser keeps changing back, so syncing is paused for 15 minutes. Check for pages or folders the other browsers cannot hold.');
          break;
        }
      }
    } while (again);
  } catch (err) {
    await setStatus(false, describeError(err));
  } finally {
    running = false;
  }
}

// Waits a few seconds after a change so a burst of edits syncs once. Our
// own changes while applying the master list are ignored.
function onBookmarkChange() {
  if (applying) return;
  if (!dirtyStored) {
    dirtyStored = true;
    api.storage.local.set({ dirty: true });
  }
  clearTimeout(timer);
  timer = setTimeout(sync, CHANGE_DELAY_MS);
}

// Where an existing bookmark sits, in words.
function placeName(path) {
  const names = { toolbar: 'Bookmarks bar', other: 'Other bookmarks' };
  return path.map((part, i) => (i === 0 ? names[part] ?? part : part)).join(' > ');
}

// A new bookmark whose address already exists somewhere: show where, and
// let the person keep or remove it. Syncing waits for the answer.
async function checkDuplicate(id, node) {
  if (applying || importing || !askDuplicates || !node.url || !syncable(node.url)) return;
  const wanted = normUrl(node.url);
  const snap = await snapshot();
  const existing = snap.bookmarks.filter((b) => b.id !== id && normUrl(b.url) === wanted);
  if (existing.length === 0) return;
  const details = {
    id,
    title: node.title ?? '',
    place: placeName(snap.bookmarks.find((b) => b.id === id)?.path ?? ['other']),
    existing: existing.slice(0, 5).map((b) => ({ title: b.title, place: placeName(b.path) })),
    more: Math.max(0, existing.length - 5),
  };
  const url = `${api.runtime.getURL('dupe.html')}?d=${encodeURIComponent(JSON.stringify(details))}`;
  const created = await api.windows.create({ url, type: 'popup', width: 460, height: 340 });
  pendingDupes.set(created.id, id);
}

// The question was answered (or closed, which keeps the bookmark).
async function finishDuplicate(windowId, keep) {
  const id = pendingDupes.get(windowId);
  if (id === undefined) return;
  pendingDupes.delete(windowId);
  if (!keep) {
    try {
      await api.bookmarks.remove(id);
    } catch (err) {
      console.warn('Could not remove the duplicate', err);
    }
  }
  if (pendingDupes.size === 0) sync();
}

api.bookmarks.onCreated.addListener((id, node) => {
  checkDuplicate(id, node).catch((err) => console.warn('Duplicate check failed', err));
});
api.bookmarks.onImportBegan?.addListener(() => { importing = true; });
api.bookmarks.onImportEnded?.addListener(() => { importing = false; });
api.windows.onRemoved.addListener((windowId) => finishDuplicate(windowId, true));

for (const event of ['onCreated', 'onRemoved', 'onChanged', 'onMoved', 'onImportEnded']) {
  api.bookmarks[event]?.addListener(onBookmarkChange);
}

// A slow check in case a push was missed, and a way to wake the connection.
api.alarms.create('safety', { periodInMinutes: SAFETY_MINUTES });
api.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'safety') sync();
});
// Open the connection as soon as the background script starts.
try {
  connect();
} catch (err) {
  console.warn('Bookmark Sync helper not reachable', err);
}
api.runtime.onStartup.addListener(() => sync(true));
api.runtime.onInstalled.addListener(() => sync(true));
api.runtime.onMessage.addListener((message, sender) => {
  if (message === 'sync-now') sync(true);
  if (message?.type === 'dupe-answer' && sender?.tab?.windowId !== undefined) finishDuplicate(sender.tab.windowId, message.keep);
});
