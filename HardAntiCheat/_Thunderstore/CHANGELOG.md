# Changelog

<details><summary>Public Test Updates</summary>

## V2 - Reforctory - Public Test Update (Updated each V2 revision/build)
- Fixes "Players getting railed on skill init still" FULLY
- Fixed fly/movement checks
- Fixed host bool issue
- Added logging choices depending on user choice
- Patched Teleportation by call
- Updated Readme (Vx.x.1)
- Canofwhoopass Bugfix (Thx homebrewery)
- Fixed isHost bug

## 1.0.7 - Bugfix - Public Test Update
- Players getting railed on skill init

## 1.0.6 - Stability & Feedback - Public Test Update
- **Fixed Critical Initialization Bug:** The dynamic Haste ID detection has been moved to the correct loading point, fixing a startup error where it would fail to find the game's data. The check is now guaranteed to run once, at the right time, and only on the server.
- **Added Automatic Log Archiving:** To prevent log files from growing infinitely on long-running servers, the infraction log is now automatically archived on startup if it exceeds a configurable size (default is 5MB).
- **Added Public Punishment Announcements:** When a player is automatically kicked or banned, a message is now broadcast to all players on the server, making the anti-cheat's actions transparent.
- **Added Server Start Confirmation:** A message is now logged to the server console when the first player spawns, confirming that the anti-cheat modules are active and monitoring.

## 1.0.5 - Lag Compensation Fix - Public Test Update
- **Overhauled Movement Detection:** The teleport/speed check is now significantly more tolerant of network lag, drastically reducing false positive kicks.
- **Added Lag Configuration:** Introduced new settings (`Max Effective Speed`, `Movement Grace Buffer`, `Movement Time Threshold`) to allow server admins to fine-tune movement detection for high-latency environments.
- **Improved Speed Hack Detection:** The speed stat check is now more intelligent. It dynamically records each player's legitimate speed on spawn and uses that as the baseline, making it more accurate than a fixed value.
- **Fixed Critical Vulnerability:** Corrected a major flaw where cast times for standard, instant-cast skills were not being validated. All skill types are now properly checked.
- **Standardized Log Path:** The infraction log file path is now always `BepInEx\plugins\HardAntiCheat\`, ensuring consistent and easy access for server admins.

## 1.0.4 - Public Test Update
- Intelligent Skill cooldown check improvement
- Should flag speedhack properly now

## 1.0.3 - Public Test Update
- Fixed bugs

## 1.0.2 - Public Test Update
- Airborne check is back
- Fixed bugs

## 1.0.1 - Public Test Update
- Pushed

## 1.0.0
- Initial release
- 
</details>

##

<details><summary>Silent Updates</summary>

## Shh, these are silent updates - just fixes and stuff (Some might not be on here)
- New logo
- Tinker Tappers
- shh CHANGELOG.md incident

</details>