# Changelog

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