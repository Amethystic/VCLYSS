# Changelog
<details><summary>Updates</summary>

## 1.1.0 - Ensures that it loads in and initializes In game to let other mods breath within a 4 second window likw roblox
- Session Monitor: Added a new coroutine SessionMonitor in VoiceSystem that detects when Player._mainPlayer is valid. 
- Safety Buffer: Added a 4-second delay after the player spawns before enabling voice, ensuring all character assets (clothes, skin) have time to load without thread interruption.
- Icon Height: Raised the bubble position from 3.8f to 4.5f so it clears player accessories better.
- Voice Activity in PTT: I have re-implemented the CfgMicThreshold logic. Now, even if Steam sends "background noise" packets, the mod will silence them (and hide the bubble) if they are below your threshold setting. I also forced the volume to 0 immediately when you release the PTT key, preventing the "visual tail" that looks like Voice Activity decay.
- Updated HandleMicInput to Decompress and Measure the audio locally before sending it. This fixes the Threshold issue.
- Added a bubble scale so u can customize how big u want the bubble to be
- Fixed player going naked (Getting packets while joining) bug

## 1.0.0
- Initial release
</details>



<details><summary>Mini Updates</summary>

## BETA
- Join discord to see beta changelogs
</details>