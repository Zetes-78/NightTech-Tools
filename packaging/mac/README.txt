NIGHT TECH MAC CLEANUP
Night Tech Solutions · (864) 381-8510 · nighttech.solutions

Start it:
1. Double-click "Start Mac Cleanup.command". If macOS says it can't be opened, right-click it > Open > Open.
   (Or in Terminal:  cd to this folder, then  zsh "Start Mac Cleanup.command")
2. Type the Mac's administrator password when asked (nothing shows while typing; that's normal).
3. Give Terminal "Full Disk Access" first so browser caches can be cleared:
   System Settings > Privacy & Security > Full Disk Access > turn on Terminal. Turn it off again when you're done.
4. Ask the customer to save their work and quit their apps and browsers (Cmd+Q). A Mac lets an open app's
   files be removed while it's using them, so clean with apps closed.

Menu:
1 Quick cleanup: measure, remove junk, suggest background items to turn off, security checks, clear DNS, measure again.
2 Junk: shows each kind of junk and its size; you pick what to remove. Trash is only emptied if you choose it.
3 Background agents: lists what starts automatically, with a suggestion for each (adware is flagged). Turning one off
  moves its file to /Library/Application Support/Night Tech/Disabled agents (only an
  administrator can open it), so it can be put back ('r' in that menu).
4 Security checks: FileVault, Gatekeeper, System Integrity Protection, firewall, Time Machine, updates, proxy,
  configuration profiles and browser search engines/extensions. Read only.
5 Repairs: verify the startup disk (read only), clear DNS.
6 Finish: measures again and saves the report (.html for the customer, .txt, and .json for the Hub ticket).
  From the USB kit, reports are saved in the Reports folder next to the program; otherwise in Documents.

Never removed: documents, photos, passwords, browser history, bookmarks or sign-ins.
