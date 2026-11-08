// Shows where a bookmark that was just added already exists, and sends the
// answer to the background script. Closing the window keeps the bookmark.

const api = globalThis.browser ?? globalThis.chrome;
const details = JSON.parse(new URLSearchParams(location.search).get('d') ?? '{}');

document.getElementById('added').textContent = `New: "${details.title || 'Untitled'}" in ${details.place}`;
const list = document.getElementById('existing');
for (const item of details.existing ?? []) {
  const row = document.createElement('li');
  row.textContent = `"${item.title || 'Untitled'}" in ${item.place}`;
  list.appendChild(row);
}
if (details.more > 0) {
  const row = document.createElement('li');
  row.textContent = `and ${details.more} more`;
  list.appendChild(row);
}

function answer(keep) {
  api.runtime.sendMessage({ type: 'dupe-answer', keep });
  window.close();
}
document.getElementById('keep').addEventListener('click', () => answer(true));
document.getElementById('remove').addEventListener('click', () => answer(false));
