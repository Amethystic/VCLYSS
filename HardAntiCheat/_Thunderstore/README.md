# HardAntiCheat

### A powerful, configurable, server-side anti-cheat engine.

This mod is a server-authoritative anti-cheat designed to block common exploits by making the server the source of truth for player actions. It is highly configurable, allowing hosts to tailor the detections to their server's needs.

All detected infractions are logged with player details in the `BepInEx\plugins\HardAntiCheat_InfractionLog.txt` file for server admin review.

## Instructions

In order to utilize this plugin, you must install it and load the game once for the configuration file to be generated. Once loaded, close the game and head into `BepInEx\Configs` (or if you use a mod manager, you should be able to edit the config from its UI). Once there, look for a file named **`HardAntiCheat.cfg`** (the exact name is based on the mod's GUID).

## Recommended Mods

These mods are not required dependencies but are confirmed to be compatible and can enhance the server experience alongside HardAntiCheat.

| **Mod Name**                                                                  | **Description**                                              |
| ----------------------------------------------------------------------------- | ------------------------------------------------------------ |
| [PerfectGuard](https://thunderstore.io/c/atlyss/p/Marioalexsan/PerfectGuard/) | Adds a client-side perfect guard / parry mechanic to combat. |

## General Configurations

| **Setting**                   | **Default** | **Description**                                                                                  |
| ----------------------------- | ----------- | ------------------------------------------------------------------------------------------------ |
| `Enable AntiCheat`            | ✅          | Master switch to enable or disable all anti-cheat modules.                                       |
| `Disable Detections for Host` | ✅          | If true, the player hosting will not be checked for infractions. Recommended for admin commands. |

## Movement Detections

| **Setting**                         | **Default** | **Description**                                                                                                   |
| ----------------------------------- | ----------- | ----------------------------------------------------------------------------------------------------------------- |
| `Enable Teleport/Distance Checks`   | ✅          | Checks if players are moving faster than physically possible, preventing speed hacks and teleportation.           |
| `Enable Fly/Infinite Jump Checks`   | ✅          | Checks if players are airborne for an impossibly long time and if their max jump stat has been illegally modified. |
| `Enable Base Speed Stat Audits`     | ✅          | Prevents players from setting their base movement speed stat to illegal values by reverting it.                   |

## Stat Detections

| **Setting**                      | **Default** | **Description**                                                                                                        |
| -------------------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------- |
| `Enable Experience/Level Checks` | ✅          | Prevents players from gaining huge amounts of XP or multiple levels at once.                                           |
| `Max Plausible XP Gain`          | 50000       | The maximum amount of XP a player can gain in a single transaction. Adjust based on your server's max XP rewards.      |

## Combat Detections

| **Setting**                     | **Default** | **Description**                                                                                                         |
| ------------------------------- | ----------- | ----------------------------------------------------------------------------------------------------------------------- |
| `Enable Skill Cooldown Checks`  | ✅          | Prevents using skills faster than their cooldowns allow. Dynamically accounts for cooldown-reducing effects like **Haste**. |
| `Enable Skill Cast Time Checks` | ✅          | Prevents instantly using skills that have a cast/channel time. Dynamically accounts for cast speed effects like **Haste**.  |
| `Enable Self-Revive Checks`     | ✅          | Prevents players from reviving themselves or replenishing their stats while dead.                                       |

## Punishments

| **Setting**                | **Default** | **Description**                                                            |
| -------------------------- | ----------- | -------------------------------------------------------------------------- |
| `Enable Punishment System` | ✅          | Enables the server to automatically take action against cheating players.  |
| `Infractions Until Action` | 5           | Number of infractions allowed before action is triggered.                  |
| `Action Type`              | Kick        | The action to take (`Kick` or `Ban`) when the infraction limit is reached. |