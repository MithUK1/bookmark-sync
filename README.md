# Bookmark Sync

Keeps your bookmarks the same in Chrome, Edge and Firefox, with nothing running in the background.

Add a bookmark in Edge, and a few seconds later it is in Chrome and Firefox too. Delete one in Firefox, and it goes from the others.

```
Before                         After
Chrome:  Recipes, News         Chrome:  Recipes, News, Weather
Edge:    Weather               Edge:    Recipes, News, Weather
Firefox: News                  Firefox: Recipes, News, Weather
```

## What it does

- Keeps one master list of bookmarks on this computer and brings every browser in line with it.
- First sync of a browser adds everything it has and removes nothing. After that, adds and deletes in any browser reach the others.
- Removes duplicates (optional). The same address is kept once, preferring the bookmarks bar. `www.`, a trailing slash and tracking codes do not make two addresses different.
- Backs up automatically before changes, and on demand. Each backup is a `.html` file (imports into any browser) and a `.json` file (restore it from the window).
- Keeps the same folders in the same order in every browser, so the bookmarks bars and folders look alike. If you reorder a folder in one browser, that order is used.
- **View master list** shows the one list every browser is kept to match.
- Never touches browser pages such as `chrome://` or `about:`.
- Nothing runs between syncs. The browser starts the helper when it needs it, and it exits straight after.

## Requirements

- Windows 10 or 11 with Windows PowerShell 5.1 (built in).
- [Google Chrome](https://www.google.com/chrome/), [Microsoft Edge](https://www.microsoft.com/edge) and/or [Firefox](https://www.mozilla.org/firefox/) 115 or newer.

## Install

1. Run `BookmarkSync.ps1` (right-click, Run with PowerShell). It installs itself for your user, no admin rights, adds a **Bookmark Sync** shortcut to the Desktop and the Start menu (these open the installed app) and one in the folder you ran it from (this opens that copy, so a newer download upgrades when you use it), and opens the Bookmark Sync window.
2. Build the extension: run `build.ps1`. It writes `dist\chromium` and `dist\firefox`.
3. Add the extension to each browser:
   - **Chrome:** `chrome://extensions`, turn on Developer mode, Load unpacked, pick `dist\chromium`.
   - **Edge:** `edge://extensions`, turn on Developer mode, Load unpacked, pick `dist\chromium`.
   - **Firefox:** sign `dist\bookmark-sync-firefox-<version>.zip` as an unlisted add-on on addons.mozilla.org and install the signed `.xpi`. For a quick try, `about:debugging`, This Firefox, Load Temporary Add-on, pick the zip (removed when Firefox restarts).

**Updating:** run a newer `BookmarkSync.ps1` the same way. It compares versions, changes only what differs (program file, launchers, browser registration, icon, shortcuts, new settings) and says what it changed. Settings, bookmarks and backups are kept, and an older copy changes nothing. **Settings > Repair install** rewrites everything if the install breaks.

Each browser syncs a few seconds after a bookmark change, when it starts, and on a timer (every minute by default). A browser learns of a change made in another browser at its next check. The toolbar button shows the last sync, has **Sync now**, and opens the Bookmark Sync window.

## Configuration

Settings are in the window under **Settings**, and in `%LOCALAPPDATA%\BookmarkSync\config.json`.

| Setting | Default | What it does |
| --- | --- | --- |
| `RemoveDuplicates` | `true` | Keep one copy of each address, preferring the bookmarks bar. |
| `PollMinutes` | `1` | How often each browser checks for changes (1 to 60). Reload the extension after changing it. |
| `AutoBackupsToKeep` | `40` | How many automatic backups to keep (5 to 500). Backups you make are always kept. |
| `WindowLeft`, `WindowTop` | `-1` | Where the window was last placed. `-1` centres it. |

Everything else lives in the same folder: `state.json` (master list and what each browser last had), `Backups\`, and `BookmarkSync.log`.

## Tests

```
powershell -File tests\BookmarkSync.Tests.ps1
```

Covers the address rules, duplicates, merging between browsers, folder order, backups and restore, the settings file and the message format the browsers use.

## Privacy

Bookmarks never leave this computer. The extension talks only to the Bookmark Sync helper installed on the same machine.

| Permission | Why |
| --- | --- |
| `bookmarks` | Read your bookmarks and change them to match the master list. |
| `nativeMessaging` | Talk to the Bookmark Sync helper on this computer. |
| `alarms` | Check for changes on a timer. |
| `storage` | Remember the last sync result for the toolbar button. |

## Notes

- New bookmarks are added at the end of their folder, so exact order is not synced.
- The Firefox bookmarks menu and other bookmarks count as one area.
- Removing Bookmark Sync: **Settings > Remove all app data**. It unregisters the helper, removes the shortcuts and sends the app folder to the Recycle Bin. Remove the extension from each browser yourself.
