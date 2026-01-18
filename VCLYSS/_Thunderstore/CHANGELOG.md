# Changelog
<details><summary>Updates</summary>

## 1.2.2
### 🛠️ Updates
*   **Updated Dependencies:** Codeyapper v2.2.0.

## 1.2.1
### 🛠️ Bug Fixes
*   **Fixed "Silent Return" Bug:** Fixed a critical issue where players would lose the ability to hear or speak to others after leaving a map instance (dungeon/hub) and coming back. The audio system now performs a full "hard reset" on map transitions to ensure a clean connection.
*   **Fixed "Zombie" Voice Bubbles:** Fixed an issue where voice bubbles would detach or duplicate when a player re-entered a zone, sometimes leaving floating icons behind.
*   **Fixed Bubble Positioning:** Ensure voice bubbles always re-attach to the correct player bone after a map transition, keeping them aligned with the player's head.
*   **Improved Stability:** Added additional safety checks during scene loading to prevent rare crashes when joining a laggy lobby.

## 1.2.0
### 🛠️ Critical Stability Fixes
*   **Fixed "Mute on Return" Bug:** Implemented a robust **Resync System**. When players switch maps or return to a previous zone, their audio engines are automatically reset, ensuring they can be heard immediately without needing to rejoin the lobby.

### ✨ Visuals & UI
*   **Microphone Status Indicator:** Added a new UI element in the bottom right corner.
    *   Shows **[MIC OPEN]** (Green) when transmitting.
    *   Shows **[MIC CLOSED]** (Red) when muted or PTT key is not held.
*   **System Status:** Added a **"VCLYSS: READY"** indicator to confirm when the voice system has successfully initialized.
*   **Bubble Fixes:** Fixed the speech bubble attaching to the wrong position after map changes. It now correctly follows the player's head height across all races.

### ⚙️ Logic Improvements
*   **Map Isolation:** You will now strictly only hear players who are in the same Map Instance (Dungeon/Hub) as you.
*   **Proximity Fallback:** Added a fail-safe that allows you to hear players if they are physically close to you, even if the server hasn't fully synced their map status yet.

## 1.1.0
### 🔧 Critical Fixes
*   **Fixed "Naked Player" Bug:** Completely overhauled the initialization logic. The mod now waits for the game to fully load player assets (clothes/skin) before processing network packets. This prevents the thread-blocking issue that caused players to spawn as floating heads or without equipment.
*   **Fixed Audio Looping:** Resolved an issue where the last second of audio would repeat indefinitely if a packet was dropped or delayed.
*   **Fixed Stuttering:** Reverted to a more stable audio buffering method to ensure smooth playback without robotic artifacts.
*   **Fixed Mic Threshold:** The "Mic Activation Threshold" setting now works correctly on the sender side. It will no longer send "silence" packets if your background noise is below the limit, saving bandwidth and fixing visual desync.

### ✨ Visual Improvements
*   **Better Bubble Positioning:** The speech bubble now attaches to the game's native `_effect_chatBubble` bone. It will now track with the player's head height correctly across different races.
*   **Bubble Animation:** Added the native `RotateObject` component to speech bubbles so they spin just like the text chat bubbles.
*   **Bubble Scaling:** Added a new **"Bubble Scale"** slider in the Mod Settings (F1). You can now adjust how big the icon is to your preference.

### 💻 For Developers (API)
*   **New API:** Added `VCLYSS.VoiceAPI` for easier integration with other mods.
    *   `IsPlayerSpeaking(Player)`
    *   `SetPlayerMute(Player, bool)`
    *   `SetPlayerVolume(Player, float)`
    *   `SetPlayerSpatialOverride(Player, bool?)`
    *   Events: `OnPlayerStartSpeaking`, `OnPlayerStopSpeaking`

### ⚙️ Other
*   **Debug Mode:** Added a "Debug Mode" toggle in settings. Enabling this shows detailed traffic flow logs in the console (disabled by default to reduce spam).
*   **Safety Buffer:** Added a safety delay on join to ensure the network stack is stable before voice data transmission begins.

## 1.0.0
- Initial release
</details>



<details><summary>Mini Updates</summary>

## BETA
- Join discord to see beta changelogs
</details>