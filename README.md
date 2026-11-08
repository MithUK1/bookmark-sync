# Bookmark Sync

Keeps your bookmarks the same in Chrome, Edge and Firefox, with one small program that sits in the tray.

Add a bookmark in Edge, and a moment later it is in Chrome and Firefox too. Delete one in Firefox, and it goes from the others.

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
- Backs up each browser separately, labelled by kind: before a change, daily, weekly, monthly, yearly and manual. It keeps 7 daily, 4 weekly, 12 monthly and 5 yearly backups per browser, plus the most recent before-a-change ones. Dailies are taken just after midnight, or at the next start if the computer was off. Each backup is a `.html` file (imports into any browser) and a `.json` file (restore it from the window).
- Keeps the same folders in the same order in every browser, so the bookmarks bars and folders look alike. If you reorder a folder in one browser, that order is used.
- **View master list** shows the one list every browser is kept to match.
- **Master browser** (Settings, optional): pick one browser, or Automatic (Firefox, then Chrome, then Edge), and the others copy it exactly: its adds, deletes, moves between folders, renames and order. Anything changed in the other browsers is put back. An empty list from the master browser is ignored.
- Never touches browser pages such as `chrome://` or `about:`.
- Watches for changes instead of using a timer: each browser holds a connection to the program, and is told the moment another browser changes.
- Runs from the tray: right-click the icon to open the window, see each browser's state, pick the master browser, back up now, turn Start with Windows on or off, or exit. Closing the window leaves it in the tray.

## Requirements

- Windows 10 or 11 (uses the .NET Framework 4.8 that comes with Windows).
- [Google Chrome](https://www.google.com/chrome/), [Microsoft Edge](https://www.microsoft.com/edge) and/or [Firefox](https://www.mozilla.org/firefox/) 115 or newer.

## Install

1. Build the program and extension: run `build.ps1` (needs the .NET SDK for the program). It writes `dist\BookmarkSync.exe`, `dist\chromium` and `dist\firefox`.
2. Run `BookmarkSync.exe`. It installs itself for your user, no admin rights, registers the helper with each browser, adds a **Bookmark Sync** shortcut to the Desktop, the Start menu and the folder you ran it from (the Desktop and Start menu open the installed app, the folder one opens that copy so a newer download upgrades when you use it), starts with Windows, and opens the window.
3. Add the extension to each browser:
   - **Chrome:** `chrome://extensions`, turn on Developer mode, Load unpacked, pick `dist\chromium`.
   - **Edge:** `edge://extensions`, turn on Developer mode, Load unpacked, pick `dist\chromium`.
   - **Firefox:** sign `dist\bookmark-sync-firefox-<version>.zip` as an unlisted add-on on addons.mozilla.org and install the signed `.xpi`. For a quick try, `about:debugging`, This Firefox, Load Temporary Add-on, pick the zip (removed when Firefox restarts).

**Updating:** run a newer `BookmarkSync.exe` the same way. It compares versions, changes only what differs (program file, browser registration, shortcuts, new settings) and says what it changed. Settings, bookmarks and backups are kept, and an older copy changes nothing. **Settings > Repair install** rewrites everything if the install breaks.

Each browser syncs a few seconds after a bookmark change, when it starts, and whenever the program tells it another browser changed. A slow check every 10 minutes catches anything missed. The toolbar button shows the last sync, has **Sync now**, and opens the Bookmark Sync window.

## Configuration

Settings are in the window under **Settings**, and in `%LOCALAPPDATA%\BookmarkSync\config.json`.

| Setting | Default | What it does |
| --- | --- | --- |
| `RemoveDuplicates` | `true` | Keep one copy of each address, preferring the bookmarks bar. |
| `AutoBackupsToKeep` | `40` | How many before-a-change backups to keep per browser (5 to 500). Backups you make are always kept. |
| `DailiesToKeep` | `7` | Daily backups to keep per browser. |
| `WeekliesToKeep` | `4` | Weekly backups to keep per browser. |
| `MonthlyToKeep` | `12` | Monthly backups to keep per browser. |
| `YearliesToKeep` | `5` | Yearly backups to keep per browser. |
| `StartWithWindows` | `true` | Start in the tray when you sign in. |
| `MasterBrowser` | `off` | `off`, `auto`, `firefox`, `chrome` or `edge`. The browser the others copy. |
| `BrowserPriority` | `firefox,chrome,edge` | Order used when `MasterBrowser` is `auto`. |
| `WindowLeft`, `WindowTop` | `-1` | Where the window was last placed. `-1` centres it. |

Everything else lives in the same folder: `state.json` (master list and what each browser last had), `Backups\`, and `BookmarkSync.log`.

## Tests

```
cd app\Tests
dotnet run
```

Covers the address rules, duplicates, merging between browsers, folder order, the master browser, backups and how long they are kept, restore, the settings file and the message format the browsers use.

## Privacy

Bookmarks never leave this computer. The extension talks only to the Bookmark Sync helper installed on the same machine.

| Permission | Why |
| --- | --- |
| `bookmarks` | Read your bookmarks and change them to match the master list. |
| `nativeMessaging` | Talk to the Bookmark Sync helper on this computer. |
| `alarms` | A slow safety check in case a message was missed. |
| `storage` | Remember the last sync result for the toolbar button. |

## Notes

- New bookmarks are added at the end of their folder, so exact order is not synced.
- The Firefox bookmarks menu and other bookmarks count as one area.
- Removing Bookmark Sync: **Settings > Remove all app data**. It unregisters the helper, removes the shortcuts and sends the app folder to the Recycle Bin. Remove the extension from each browser yourself.

The extension's reorder step has its own check against a fake bookmarks API: `node tests/reorder.test.js`, also with `chrome` or `firefox` as an argument.
