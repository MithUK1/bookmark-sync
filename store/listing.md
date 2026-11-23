# Store listing text

## Name
Bookmarks Sync Plus

## Summary (max 132 characters)
Keeps your bookmarks the same in Chrome, Edge and Firefox. Works with the free Bookmarks Sync Plus program on your Windows computer.

## Description
Bookmarks Sync Plus keeps your bookmarks the same in every browser on your computer. Add a bookmark in Edge, and a moment later it is in Chrome and Firefox too. Delete one in Firefox, and it goes from the others.

This extension needs the free Bookmarks Sync Plus program for Windows (see the link below). The program does the syncing on your own computer, so there is no account, no server and no sign-in.

- Live sync: each browser holds a connection to the program and is told the moment another browser changes. No timers.
- Same folders in the same order in every browser, including the bookmarks bar.
- Backups of each browser: before a change, daily, weekly, monthly and yearly, each as a file you can import into any browser or restore from the program window.
- Asks before it saves a duplicate: shows which folder the other copy is in, and lets you keep both or remove the new one. An option can delete duplicates for you.
- Optional master browser: pick one browser and the others copy it exactly.
- Runs from the tray and can start with Windows.

Private by design: bookmarks go from one browser to another through the program on your computer. Nothing is sent over the internet and nothing is collected.

Needs the Bookmarks Sync Plus program for Windows 10 or 11: https://github.com/MithUK1/bookmark-sync-plus#install

## Category
Productivity (Chrome) / Bookmarks (Firefox)

## Chrome Web Store: privacy tab
Single purpose: Keep browser bookmarks in step across Chrome, Edge and Firefox through the Bookmarks Sync Plus program on the same computer.

Permission justifications:
- bookmarks: Reads the bookmarks and folders in this browser and adds, moves, renames and removes them so every browser matches the master list.
- nativeMessaging: Talks to the Bookmarks Sync Plus program installed on the same computer, which holds the master list and the backups. This is the only place the bookmarks are sent.
- storage: Saves the last sync result shown in the toolbar popup and a note of when the extension last reloaded itself.
- alarms: Runs a safety check every 10 minutes in case a change notification from the program was missed.

Data usage: no user data is collected, transmitted off the computer or sold. Bookmarks are passed only to the Bookmarks Sync Plus program on the same computer. Remote code: none.
Privacy policy URL: https://github.com/MithUK1/bookmark-sync-plus#privacy

## Firefox (AMO)
Data collection: none required (declared in the manifest as data_collection_permissions required ["none"]; needs Firefox 140 or newer).
Source code: https://github.com/MithUK1/bookmark-sync-plus (build with build.cmd, output in extension\firefox; the extension source is in extension\src)
Notes for the reviewer: the add-on needs the Bookmarks Sync Plus program (a Windows exe from the same repository's releases) for its native messaging host, so it shows "not installed" until that is run.

## Images
- store\icon-512.png (also icon.svg)
- store\promo-tile-440x280.png
- store\promo-marquee-1400x560.png
- store\screenshots\shot1-same-everywhere.png, shot2-duplicate-prompt.png, shot3-live-sync.png (1280 x 800, made by tools\make-store-art.js from the real popup and prompt pages)
