// Builds the store pictures in store/ from the real popup and duplicate-prompt pages.
// Needs Playwright (npm i playwright). Run: node tools/make-store-art.js
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const src = path.join(root, 'extension', 'src');
const out = path.join(root, 'store');
const iconSvg = fs.readFileSync(path.join(out, 'icon.svg'), 'utf8');
const iconUri = 'data:image/svg+xml;base64,' + Buffer.from(iconSvg).toString('base64');

const BG = `background: radial-gradient(900px 500px at 85% 10%, #2f4fb0 0%, transparent 60%), radial-gradient(700px 500px at 0% 100%, #2c2a85 0%, transparent 55%), linear-gradient(135deg,#0b1445,#13237a 60%,#1a38b8);`;
const BASE = `<style>*{box-sizing:border-box}body{margin:0;font-family:Inter,system-ui,sans-serif;color:#fff;overflow:hidden}</style>`;

// Mock browser api so the real pages render without the extension installed.
const mock = (status) => `
  const store = { status: ${JSON.stringify(status)} };
  globalThis.chrome = {
    runtime: { getManifest: () => ({ version: '1.0.0' }), getURL: (p) => p,
      sendMessage: async () => {}, sendNativeMessage: async () => ({ version: '1.0.0' }) },
    storage: { local: { get: async (k) => ({ status: store.status }) }, onChanged: { addListener() {} } },
  };`;

async function shotOf(browser, file, init, query, selector, width) {
  const ctx = await browser.newContext({ viewport: { width, height: 700 }, deviceScaleFactor: 2, colorScheme: 'light' });
  const page = await ctx.newPage();
  await page.addInitScript(init);
  await page.goto('file://' + path.join(src, file) + (query || ''));
  await page.waitForTimeout(400);
  const el = await page.$(selector);
  const buf = await el.screenshot();
  await ctx.close();
  return 'data:image/png;base64,' + buf.toString('base64');
}

async function render(browser, html, w, h, file) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  await page.setContent(html);
  await page.waitForTimeout(300);
  await page.screenshot({ path: file });
  await ctx.close();
}

const card = (uri, w) => `<img src="${uri}" style="width:${w}px;border-radius:14px;box-shadow:0 24px 60px rgba(5,10,60,.55)">`;

(async () => {
  const browser = await chromium.launch();
  const popupOk = await shotOf(browser, 'popup.html', mock({ ok: true, message: 'Synced, 3 added, 1 removed', at: Date.now() - 120000 }), '', 'body', 280);
  const popupAll = await shotOf(browser, 'popup.html', mock({ ok: true, message: 'Up to date', at: Date.now() - 30000 }), '', 'body', 280);
  const details = { title: 'Pasta recipes', place: 'Bookmarks bar > Cooking', existing: [{ title: 'Pasta recipes', place: 'Other bookmarks > Recipes' }], more: 0 };
  const dupe = await shotOf(browser, 'dupe.html', mock(null), '?d=' + encodeURIComponent(JSON.stringify(details)), 'body', 460);

  const shot = (title, sub, pic, w) => `<html><head>${BASE}</head><body style="${BG}width:1280px;height:800px">
    <div style="position:absolute;left:90px;top:230px;width:520px"><div style="font-size:54px;font-weight:800;line-height:1.08;letter-spacing:-1px">${title}</div>
    <div style="margin-top:22px;font-size:24px;line-height:1.35;color:#c9d6ff">${sub}</div></div>
    <div style="position:absolute;left:760px;top:190px">${card(pic, w)}</div></body></html>`;
  await render(browser, shot('Your bookmarks, the same everywhere', 'Add one in Edge and it appears in Chrome and Firefox a moment later.', popupOk, 400), 1280, 800, path.join(out, 'screenshots', 'shot1-same-everywhere.png'));
  await render(browser, shot('Asks before it saves a copy', 'If a bookmark already exists, it shows which folder it is in. You choose to keep both or drop the new one.', dupe, 460), 1280, 800, path.join(out, 'screenshots', 'shot2-duplicate-prompt.png'));
  await render(browser, shot('Syncs the moment you change one', 'No timers and no account. A small program on your computer keeps every browser in step.', popupAll, 400), 1280, 800, path.join(out, 'screenshots', 'shot3-live-sync.png'));

  const chips = ['Live sync', 'Backups', 'No account'].map((c) => `<span style="display:inline-block;margin-right:12px;padding:9px 18px;border-radius:999px;background:rgba(255,255,255,.12);border:1px solid rgba(255,255,255,.25);font-size:20px;font-weight:600;color:#dbe4ff">${c}</span>`).join('');
  await render(browser, `<html><head>${BASE}</head><body style="${BG}width:1400px;height:560px">
    <img src="${iconUri}" style="position:absolute;left:90px;top:70px;width:104px;height:104px;border-radius:24px">
    <div style="position:absolute;left:216px;top:104px;font-size:30px;font-weight:500;color:#c0cdf5">Chrome, Edge and Firefox</div>
    <div style="position:absolute;left:90px;top:206px;font-size:84px;font-weight:800;line-height:1.02;letter-spacing:-2px">Bookmark<br>Sync</div>
    <div style="position:absolute;left:90px;top:398px;font-size:30px;color:#c9d6ff">Keep your bookmarks the same in every browser.</div>
    <div style="position:absolute;left:90px;top:456px">${chips}</div>
    <div style="position:absolute;left:830px;top:70px">${card(popupOk, 420)}</div></body></html>`, 1400, 560, path.join(out, 'promo-marquee-1400x560.png'));

  await render(browser, `<html><head>${BASE}</head><body style="${BG}width:440px;height:280px">
    <img src="${iconUri}" style="position:absolute;left:30px;top:34px;width:72px;height:72px;border-radius:17px">
    <div style="position:absolute;left:30px;top:124px;font-size:44px;font-weight:800;line-height:1.03;letter-spacing:-1px">Bookmark<br>Sync</div>
    <div style="position:absolute;left:30px;top:228px;font-size:17px;color:#c9d6ff">Same bookmarks in every browser</div></body></html>`, 440, 280, path.join(out, 'promo-tile-440x280.png'));

  await render(browser, `<html><body style="margin:0;background:transparent"><img src="${iconUri}" style="width:512px;height:512px;display:block"></body></html>`, 512, 512, path.join(out, 'icon-512.png'));
  await browser.close();
})();
