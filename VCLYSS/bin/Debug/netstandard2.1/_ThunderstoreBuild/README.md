# VCLYSS
Voice Chat plugin for Atlyss multiplayer. (In Private Testing)
- Dont reveal this outside of the discord server
- But please do invite people into it, we dont want no incoming issues that will interfere with the testing phase
  https://discord.gg/ePhX4Fb2we

Adds fully functional **Proximity Voice Chat** to ATLYSS.
Talk to your friends, organize dungeon runs, or just hang out in the plaza with 3D spatial audio.

## Features
*   **Proximity Chat:** Voices get quieter as players walk away.
*   **Visual Indicators:** A speech bubble appears above the head of whoever is talking.
*   **Host Control:** If the Host doesn't have the mod, it automatically disables itself to prevent crashes.
*   **Moderation:** Fully compatible with **HostModeration**. If a player is kicked/banned, their voice is instantly cut.
*   **Panic Button:** A "Mute Everyone" toggle in the settings for emergencies.

## Settings (Press F1 / Mod Settings)
*   **Microphone:** Supports Push-to-Talk or Toggle.
*   **Threshold:** Adjustable mic gate to filter out background noise.
*   **Spatial Audio:** Adjust how far voices travel (Earmuff Mode).
*   **Volume:** Master volume slider.

## Installation
1.  Install **BepInEx**.
2.  Install **CodeYapper** (Required for networking).
3.  Install **Nessie-EasySettings** (Required for the menu).
4.  Drop `VCLYSS.dll` into your `BepInEx/plugins` folder.

## For Developers
This mod exposes a public API for other mods to mute/unmute players programmatically.
```csharp
if (Chainloader.PluginInfos.ContainsKey("com.soggy.vclyss"))
{
    VCLYSS.VoiceManager.Instance.MutePlayer(steamID);
}