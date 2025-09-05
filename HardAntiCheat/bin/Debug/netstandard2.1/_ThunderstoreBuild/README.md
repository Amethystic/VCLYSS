# HardAntiCheat

### A powerful, configurable, server-side anti-cheat engine for Atlyss.

This mod is a server-authoritative anti-cheat designed to block common exploits by making the server the source of truth for player actions. It is highly configurable, allowing hosts to tailor the detections to their server's specific mods and balance.

All detected infractions are logged with player details in the `BepInEx\plugins\HardAntiCheat\HardAntiCheat_InfractionLog.txt` file for server admin review.

## Instructions

1.  Install the mod and run the game once to generate the configuration file.
2.  Close the game.
3.  Navigate to `BepInEx\config` and open the file named `HardAntiCheat.cfg`.
4.  Adjust the settings as needed for your server. Using a configuration manager mod to edit these values in-game is also supported.

## Recommended Mods

These mods are not required dependencies but are confirmed to be compatible and can enhance the server experience alongside HardAntiCheat.

| **Mod Name**                                                                  | **Description**                                              |
| ----------------------------------------------------------------------------- | ------------------------------------------------------------ |
| [PerfectGuard](https://thunderstore.io/c/atlyss/p/Marioalexsan/PerfectGuard/) | Adds a client-side perfect guard / parry mechanic to combat. |

---

## Configuration Settings

### General

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable AntiCheat` | ✅ | Master switch to enable or disable all anti-cheat modules. |
| `Disable Detections for Host` | ✅ | If true, the player hosting will not be checked. Recommended for admins. |
| `Max Log File Size (MB)` | 5 | If the infraction log exceeds this size on startup, it will be archived. |

### Movement Detections

This module validates player movement to prevent speed, teleport, and fly hacking.

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable Teleport/Distance Checks` | ✅ | Checks if players are moving faster than physically possible based on distance over time. |
| `Max Effective Speed` | 100.0 | The maximum speed (units/sec) used in the distance check. Increase this if lagging players or certain skills cause false flags. |
| `Movement Grace Buffer` | 10.0 | A flat distance buffer added to the distance check to account for dashes, knockbacks, and lag spikes. |
| `Movement Time Threshold` | 5.5 | The time (in seconds) between position checks. Higher values are more lenient on lag but less precise. |
| `Teleport Distance Threshold` | 50.0 | Any movement flagged by the distance check that *also* covers more than this distance is logged as a "Teleport" instead of "Speed". |
| `Enable Fly/Infinite Jump Checks` | ✅ | Detects players airborne for too long. Uses a **vertical stall heuristic** to intelligently ignore legitimate ledge grabs and climbing. |
| `Enable Base Speed Stat Audits` | ✅ | Prevents players from illegally modifying their base movement speed. Uses the `Speed Tolerance Multiplier` to allow for buffs. |
| `Speed Tolerance Multiplier` | 3.0 | Allows player speed to exceed their base speed by this multiplier (e.g., `3.0` = 200% bonus speed) before being clamped. |
| `Jump Threshold` | 8 | The maximum number of consecutive jumps a player can perform before needing to touch the ground. |
| `Speed Hack Detection Cooldown` | 2.0 | Cooldown (in seconds) before another speed stat infraction is logged for the same player. Prevents log spam. |
| `Jump Hack Detection Cooldown` | 2.0 | Cooldown (in seconds) before another jump stat infraction is logged for the same player. |
| `Airborne Hack Detection Cooldown`| 10.0 | Cooldown (in seconds) before another airborne infraction is logged for the same player. |

### Stat Detections

This module validates changes to player stats like experience and levels.

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable Experience/Level Checks` | ✅ | Prevents players from gaining huge amounts of XP or multiple levels at once. |
| `Max Plausible XP Gain` | 77000 | The maximum XP a player can gain in a single transaction. Adjust based on your server's max XP rewards. |

### Combat Detections

This module enforces server-side authority over combat actions.

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable Skill Cooldown Checks` | ✅ | Prevents using skills faster than their cooldowns allow. Dynamically accounts for cooldown-reducing effects. |
| `Enable Self-Revive Checks` | ✅ | Prevents players from reviving themselves or replenishing their stats while dead. |

### Punishments

Configure automatic server actions for players who accumulate too many infractions.

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable Punishment System` | ✅ | Enables the server to automatically kick or ban cheating players. Punishments are announced in server chat. |
| `Infractions Until Action` | 5 | Number of infractions allowed before the selected punishment is triggered. |
| `Action Type` | Kick | The action to take (`Kick` or `Ban`) when the infraction limit is reached. |

### Logging

Control the level of detail in the infraction logs.

| **Setting** | **Default** | **Description** |
| :--- | :---: | :--- |
| `Enable Detailed Logs` | ✅ | Master switch for detailed infraction logs. If false, only punishments are logged. |
| `Log Player Name` | ✅ | Include the player's name in detailed logs. |
| `Log Player ID` | ✅ | Include the player's SteamID/netId in detailed logs. |
| `Log Infraction Details` | ✅ | Include the specific reason/details of the infraction. |
| `Log Infraction Count` | ✅ | Include the player's current warning count in the log entry. |