NIGHT TECH FIELD KIT (USB)
Night Tech Solutions · (864) 381-8510 · nighttech.solutions

SETTING UP THE STICK
Copy everything in this zip to the top of a USB stick, so the stick has:
  Night Tech Field Kit\Field Kit\NightTechFieldKit.exe
  Night Tech Field Kit\Recovery\NightTechRecovery.exe
  Night Tech Field Kit\Wipe\NightTechWipe.exe
Nothing needs installing on the customer's computer. .NET is included.

ON A CUSTOMER'S COMPUTER
1. Plug in the stick and open Night Tech Field Kit\Field Kit\NightTechFieldKit.exe
2. Type the ticket number from Night Tech Hub, the customer's name and your name.
3. Click "Run all checks". It takes about a minute and changes nothing on the computer.
4. Click "Open customer report" to show or print the results.
5. Back at the shop, attach the .txt report to the ticket in Night Tech Hub (Files and photos).

Field Kit's reports and logs are saved on the stick (Field Kit\Reports and Field Kit\Logs), and Recovery's logs in
Recovery\Logs, not on the customer's computer. The "portable.txt" file next to each program is what tells it to do
that. Don't delete it. (Recovery's drive images go wherever you choose: use a shop drive with enough space.)

Field Kit's checks are read only. In its Tools tab, the buttons marked CHANGES WINDOWS (system file repair, DISM,
memory test) do change the computer, so they ask first: get the customer's OK.

For drive temperature, SSD wear and BitLocker status, click "Restart as administrator".
If a drive clicks, grinds or isn't detected, stop: it goes to a recovery lab.

Windows SmartScreen may warn the first time because the programs aren't code-signed yet:
click "More info", then "Run anyway".

CLEANUP (the $50 PC cleanup)
Open "Cleanup\NightTechCleanup.exe" (it asks for administrator rights). "Run quick cleanup" does the standard job:
restore point, before measurements, junk, startup suggestions, Defender update and quick scan, Windows component cleanup
and drive optimization. Then review the Programs and Security tabs with the customer, and save the report on Finish.

BURN-IN (stress test with a pass/fail sheet)
Open "Burn-in\NightTechBurnIn.exe" (it asks for administrator rights). Plug laptops in, close other programs, pick a
length (1 hour for a repair check, overnight for a refurb or a "crashes sometimes" job) and start. It loads every
processor core and checks every answer, tests the free memory with patterns, writes and checks a test file on the
chosen drives, watches temperature and speed, and reads drive health, battery wear and Windows' hardware error log.
Save the sheet: give the customer the .html, upload the .txt to the Hub ticket.

MAC CLEANUP
On a Mac, open the "Mac Cleanup" folder and double-click "Start Mac Cleanup.command" (see its README.txt).
