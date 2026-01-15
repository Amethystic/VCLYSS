# VCLYSS
**Voice Chat plugin for Atlyss multiplayer.**

Adds fully functional **Proximity Voice Chat** to ATLYSS. Talk to your friends, organize dungeon runs, or just hang out in the plaza with 3D spatial audio.

[**Join the Discord Community**](https://discord.gg/ePhX4Fb2we)

## Features
*   **Proximity Chat:** Voices are 3D and get quieter as players walk away.
*   **Visual Indicators:**
    *   **Speech Bubbles:** A 3D bubble appears over a player's head when they talk.
    *   **Overlay:** See who is talking in the UI.
    *   **Lip Sync:** Player mouths move when they speak!
*   **Host Safety:** The mod automatically checks if the Lobby Host has VCLYSS installed. If they don't, the mod disables itself to prevent connection issues.
*   **Mic Test:** Loopback mode to hear yourself and adjust your settings.
*   **Panic Button:** A "Mute Everyone" toggle in the settings for emergencies.

## Installation
1.  Install **BepInEx** (if you haven't already).
2.  Install **CodeTalker** (Required for networking).
3.  Install **Nessie-EasySettings** (Required for the configuration menu).
4.  Drop `VCLYSS.dll` into your `BepInEx/plugins` folder.

## Settings
Press **F1** (or your Mod Settings bind) to configure:

*   **Input Mode:** Choose between **Push-to-Talk**, **Toggle**, or **Always On**. (Default Key: **T**)
*   **Mic Threshold:** Adjust this if your mic is picking up breathing or keyboard clicks.
*   **Spatial Audio:**
    *   **Min Distance:** How close you need to be for 100% volume.
    *   **Max Distance:** The distance at which voices become silent.
*   **Visuals:**
    *   Toggle the overlay, head icons, or lip sync.
    *   **Bubble Scale:** Adjust the size of the speech bubble icon.

---

## 🛠️ For Developers (API)
VCLYSS exposes a static API designed for other mods (Radio mods, Admin tools, Status Effects, etc.).

### 1. How to Reference
Add `VCLYSS.dll` as a reference in your project, or use Reflection/Soft Dependency if you don't want a hard requirement.

### 2. Usage Examples

**Muting a Player (e.g., Blocking/Admin Mod)**
```csharp
// Mutes the player LOCALLY (you won't hear them, but others might)
VCLYSS.VoiceAPI.SetPlayerMute(targetPlayer, true);
```

**Changing Volume (e.g., Deafness Status Effect)**
```csharp
// Set volume to 10%
VCLYSS.VoiceAPI.SetPlayerVolume(targetPlayer, 0.1f);

// Reset to normal
VCLYSS.VoiceAPI.SetPlayerVolume(targetPlayer, 1.0f);
```

**Forcing Global Audio (e.g., Walkie-Talkie / Phone Mod)**
```csharp
// Force audio to be 2D (Global) regardless of distance
VCLYSS.VoiceAPI.SetPlayerSpatialOverride(targetPlayer, false);

// Force audio to be 3D (Spatial)
VCLYSS.VoiceAPI.SetPlayerSpatialOverride(targetPlayer, true);

// Reset to user's Config settings
VCLYSS.VoiceAPI.SetPlayerSpatialOverride(targetPlayer, null);
```

**Reacting to Speech (e.g., Custom UI)**
```csharp
void Start() {
    VCLYSS.VoiceAPI.OnPlayerStartSpeaking += (player) => {
        Debug.Log($"{player._nickname} started talking!");
    };
    
    VCLYSS.VoiceAPI.OnPlayerStopSpeaking += (player) => {
        Debug.Log($"{player._nickname} stopped talking.");
    };
}
```

### 3. Soft Dependency Check
If you want to support VCLYSS but not require it:
```csharp
if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.soggy.vclyss"))
{
    // Use Reflection or a separate wrapper method to call the API
}
```

---

## Credits
Massive thanks to the people who helped make this mod possible:

*   **Zera** (@origamidisaster)
*   **Kami** (@wolfkann)
*   **Soggy_Pancake** (@soggy_pancake)
*   **Marioalexsan** (@marioalexsan)