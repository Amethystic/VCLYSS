using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace HardAntiCheat
{
    #region Data Structures
    public struct PlayerPositionData { public Vector3 Position; public float Timestamp; }
	public struct PlayerStatsData { public int Level; public int Experience; }
	public struct PlayerAirborneData { public float AirTime; public Vector3 LastGroundedPosition; public int ServerSideJumpCount; }
    #endregion

    #region Utility Class
    public static class Util
    {
        public static bool IsHost(NetworkBehaviour instance)
        {
            Player player = instance.GetComponent<Player>();
            return player != null && player._isHostPlayer;
        }
    }
    #endregion

	[BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
	[BepInDependency("Marioalexsan.PerfectGuard", BepInDependency.DependencyFlags.SoftDependency)]
	public class Main : BaseUnityPlugin
	{
		private readonly Harmony harmony = new Harmony(ModInfo.GUID);
		internal static ManualLogSource Log;
		internal static string InfractionLogPath;

		// --- CONFIGURATION ENTRIES ---
		public static ConfigEntry<bool> EnableAntiCheat;
		public static ConfigEntry<bool> DisableForHost;
        public static ConfigEntry<int> MaxLogFileSizeMB;
        public static ConfigEntry<bool> EnableMovementChecks;
		public static ConfigEntry<float> MaxEffectiveSpeed;
        public static ConfigEntry<float> MovementGraceBuffer;
        public static ConfigEntry<float> MovementTimeThreshold;
		public static ConfigEntry<bool> EnableAirborneChecks;
		public static ConfigEntry<bool> EnableSpeedChecks;
		public static ConfigEntry<bool> EnableExperienceChecks;
		public static ConfigEntry<int> JumpThreshold;
        public static ConfigEntry<int> MaxPlausibleXPGain;
		public static ConfigEntry<bool> EnableCooldownChecks;
		public static ConfigEntry<bool> EnableReviveChecks;
		public static ConfigEntry<bool> EnablePunishmentSystem;
		public static ConfigEntry<int> WarningsUntilAction;
		public static ConfigEntry<string> ActionType;
        public static ConfigEntry<bool> EnableDetailedLogs;
        public static ConfigEntry<bool> LogPlayerName;
        public static ConfigEntry<bool> LogPlayerID;
        public static ConfigEntry<bool> LogInfractionCount;
        public static ConfigEntry<bool> LogInfractionDetails;


		// --- SERVER-SIDE DATA DICTIONARIES ---
		public static readonly Dictionary<uint, Dictionary<string, float>> ServerRemainingCooldowns = new Dictionary<uint, Dictionary<string, float>>();
        public static readonly Dictionary<uint, PlayerPositionData> ServerPlayerPositions = new Dictionary<uint, PlayerPositionData>();
		public static readonly Dictionary<uint, PlayerStatsData> ServerPlayerStats = new Dictionary<uint, PlayerStatsData>();
		public static readonly Dictionary<uint, PlayerAirborneData> ServerPlayerAirborneStates = new Dictionary<uint, PlayerAirborneData>();
        public static readonly Dictionary<uint, float> ServerPlayerInitialSpeeds = new Dictionary<uint, float>();
		public static readonly Dictionary<uint, int> ServerPlayerInfractionCount = new Dictionary<uint, int>();
		public static readonly Dictionary<uint, float> ServerPlayerGracePeriod = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerPunishmentCooldown = new Dictionary<uint, float>();
		
		private void Awake()
		{
			Log = Logger;
            string pluginsPath = Directory.GetParent(Path.GetDirectoryName(Info.Location)).FullName;
            string targetLogDirectory = Path.Combine(pluginsPath, "HardAntiCheat");
            Directory.CreateDirectory(targetLogDirectory);
            InfractionLogPath = Path.Combine(targetLogDirectory, $"{ModInfo.NAME}_InfractionLog.txt");

			EnableAntiCheat = Config.Bind("1. General", "Enable AntiCheat", true, "Master switch to enable or disable all anti-cheat modules.");
			DisableForHost = Config.Bind("1. General", "Disable Detections for Host", true, "If true, the player hosting the server will not be checked for infractions. Recommended for hosts who use admin commands.");
			MaxLogFileSizeMB = Config.Bind("1. General", "Max Log File Size (MB)", 5, "If the infraction log exceeds this size, it will be archived on startup to prevent it from growing infinitely.");

            EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Distance Checks", true, "Checks the final result of player movement to catch physics-based speed hacks and teleports.");
            MaxEffectiveSpeed = Config.Bind("2. Movement Detections", "Max Effective Speed", 100f, "The maximum plausible speed (units per second) a player can move. Increase this if lagging players get false flagged.");
            MovementGraceBuffer = Config.Bind("2. Movement Detections", "Movement Grace Buffer", 10.0f, "A flat distance buffer added to the calculation to account for dashes, knockbacks, and small lag spikes.");
            MovementTimeThreshold = Config.Bind("2. Movement Detections", "Movement Time Threshold", 5.5f, "The time (in seconds) between position checks. Higher values are more lenient on lag but less precise.");
			EnableAirborneChecks = Config.Bind("2. Movement Detections", "Enable Fly/Infinite Jump Checks", true, "Checks if players are airborne for an impossibly long time and have an invalid number of max jumps.");
			EnableSpeedChecks = Config.Bind("2. Movement Detections", "Enable Base Speed Stat Audits", true, "Continuously checks if a player's base movement speed stat has been illegally modified and reverts it.");
			JumpThreshold = Config.Bind("2. Movement Detections", "Jump threshold", 8, "The maximum number of jumps a player is allowed to perform before returning to the ground.");

			EnableExperienceChecks = Config.Bind("3. Stat Detections", "Enable Experience/Level Checks", true, "Prevents players from gaining huge amounts of XP or multiple levels at once based on the 'Max Plausible XP Gain' limit.");
            MaxPlausibleXPGain = Config.Bind("3. Stat Detections", "Max Plausible XP Gain", 77000, "The maximum amount of XP a player can gain in a single transaction. Adjust this based on your server's max XP rewards.");

			EnableCooldownChecks = Config.Bind("4. Combat Detections", "Enable Skill Cooldown Checks", true, "Silently enforces server-side cooldowns, blocking premature skill usage.");
			EnableReviveChecks = Config.Bind("4. Combat Detections", "Enable Self-Revive Checks", true, "Prevents players from reviving themselves while dead.");

			EnablePunishmentSystem = Config.Bind("5. Punishments", "Enable Punishment System", true, "If enabled, the server will automatically take action against players who accumulate too many infractions.");
			WarningsUntilAction = Config.Bind("5. Punishments", "Infractions Until Action", 5, "Number of infractions a player can commit before the selected action is taken.");
			ActionType = Config.Bind("5. Punishments", "Action Type", "Kick", new ConfigDescription("The action to take when a player reaches the infraction limit.", new AcceptableValueList<string>("Kick", "Ban")));

            EnableDetailedLogs = Config.Bind("6. Logging", "Enable Detailed Logs", true, "Master switch for detailed infraction logs. If false, a minimal log is created. If true, uses the options below.");
            LogPlayerName = Config.Bind("6. Logging", "Log Player Name", true, "Include the player's name in detailed logs.");
            LogPlayerID = Config.Bind("6. Logging", "Log Player ID", true, "Include the player's SteamID/netId in detailed logs.");
            LogInfractionDetails = Config.Bind("6. Logging", "Log Infraction Details", true, "Include the specific reason/details of the infraction in detailed logs.");
            LogInfractionCount = Config.Bind("6. Logging", "Log Infraction Count", true, "Include the player's current warning count in detailed logs.");


            CheckAndArchiveLogFile();

			harmony.PatchAll();
			Log.LogInfo($"[{ModInfo.NAME}] has been loaded. Infractions will be logged to: {InfractionLogPath}");
		}

        private void CheckAndArchiveLogFile()
        {
            try
            {
                if (File.Exists(InfractionLogPath))
                {
                    FileInfo logFileInfo = new FileInfo(InfractionLogPath);
                    long maxLogSizeBytes = MaxLogFileSizeMB.Value * 1024L * 1024L;

                    if (logFileInfo.Length > maxLogSizeBytes)
                    {
                        string archivePath = Path.Combine(
                            Path.GetDirectoryName(InfractionLogPath),
                            $"{Path.GetFileNameWithoutExtension(InfractionLogPath)}_ARCHIVED_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
                        );
                        File.Move(InfractionLogPath, archivePath);
                        Log.LogInfo($"Infraction log exceeded {MaxLogFileSizeMB.Value}MB and was archived to: {archivePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Error while checking/archiving log file: {ex.Message}");
            }
        }

		public static void LogInfraction(NetworkBehaviour instance, string cheatType, string details)
		{
            uint netId = instance.netId;
            if (ServerPunishmentCooldown.TryGetValue(netId, out float cooldownEndTime) && Time.time < cooldownEndTime) { return; }
            ServerPunishmentCooldown.Remove(netId);

			if (!ServerPlayerInfractionCount.ContainsKey(netId)) ServerPlayerInfractionCount[netId] = 0;
			ServerPlayerInfractionCount[netId]++;
			int currentInfractions = ServerPlayerInfractionCount[netId];
			int maxInfractions = WarningsUntilAction.Value;
            
			Player player = instance.GetComponent<Player>();
			string playerName = player?._nickname ?? "Unknown";
			string playerID = player?._steamID ?? $"netId:{netId}";
			
            // --- REWRITTEN DYNAMIC LOGGING ---
            string logMessage;
            if (EnableDetailedLogs.Value)
            {
                var sb = new StringBuilder();
                sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]");

                var segments = new List<string>();
                if (LogPlayerName.Value) segments.Add($"Player: {playerName}");
                if (LogPlayerID.Value) segments.Add($"ID: {playerID}");
                if (segments.Any()) sb.Append(" " + string.Join(" | ", segments));

                sb.Append($" | Type: {cheatType}");

                if (LogInfractionDetails.Value) sb.Append($" | Details: {details}");
                if (LogInfractionCount.Value) sb.Append($" | Warning {currentInfractions}/{maxInfractions}");

                logMessage = sb.ToString();
            }
            else
            {
                logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Player: {playerName} flagged for {cheatType} ({currentInfractions}/{maxInfractions})";
            }

			Log.LogWarning(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }

			if (!EnablePunishmentSystem.Value || AtlyssNetworkManager._current._soloMode) return;
			
			if (currentInfractions >= maxInfractions)
			{
				if (NetworkServer.active && player != null && HostConsole._current != null && player.connectionToClient != null)
				{
                    HC_PeerListEntry targetPeer = null;
                    foreach(var entry in HostConsole._current._peerListEntries)
                    {
                        if (entry._netId != null && entry._netId.netId == netId) { targetPeer = entry; break; }
                    }

                    if (targetPeer != null)
                    {
                        ServerPunishmentCooldown[netId] = Time.time + 60f;

                        string action = ActionType.Value.ToLower();
					    string punishmentDetails = $"Player {playerName} (ID: {playerID}) was automatically {action.ToUpper()}ed for reaching {currentInfractions}/{maxInfractions} infractions.";
					    Log.LogWarning(punishmentDetails);
					    try { File.AppendAllText(InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PUNISHMENT] " + punishmentDetails + Environment.NewLine); }
					    catch (Exception ex) { Log.LogError($"Failed to write punishment to log: {ex.Message}"); }
                        
                        HostConsole._current._selectedPeerEntry = targetPeer;
                        if(action == "kick") 
                        {
                            HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails); 
                            HostConsole._current.Kick_Peer(); 
                        } 
                        else 
                        {
                            HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails); 
                            HostConsole._current.Ban_Peer(); 
                        }
                        
                        ServerPlayerInfractionCount.Remove(netId);
                    }
                    else
                    {
                        Log.LogError($"Could not find PeerListEntry for player {playerName} (netId: {netId}) to take action.");
                    }
				}
			}
		}

        public static void ClearAllPlayerData(uint netId)
        {
            ServerRemainingCooldowns.Remove(netId);
            ServerPlayerPositions.Remove(netId);
            ServerPlayerStats.Remove(netId);
            ServerPlayerAirborneStates.Remove(netId);
            ServerPlayerInitialSpeeds.Remove(netId);
            ServerPlayerInfractionCount.Remove(netId);
            ServerPlayerGracePeriod.Remove(netId);
            ServerPunishmentCooldown.Remove(netId);
        }
	}
    
    #region Player Spawn & Server Initialization
	[HarmonyPatch(typeof(PlayerMove), "Start")]
	public static class PlayerSpawnPatch
	{
		private const float GRACE_PERIOD_SECONDS = 3.0f;
        private static bool hasServerInitialized = false;

		public static void Postfix(PlayerMove __instance)
		{
			if (!NetworkServer.active) return;
            
            if (!hasServerInitialized)
            {
                Main.Log.LogInfo("First player has spawned. HardAntiCheat server-side modules are now active and monitoring players.");
                hasServerInitialized = true;
            }

            uint netId = __instance.netId;
            Main.ServerPlayerGracePeriod[netId] = Time.time + GRACE_PERIOD_SECONDS;
            if (!Main.ServerPlayerInitialSpeeds.ContainsKey(netId))
            {
                Main.ServerPlayerInitialSpeeds[netId] = __instance.Network_movSpeed;
                Main.Log.LogInfo($"[{netId}] Recorded initial move speed for player: {__instance.Network_movSpeed}");
            }
		}
	}
    #endregion
    
    #region Server Authority Protection (Self-Revive)
	[HarmonyPatch]
	public static class ServerAuthorityValidationPatch
	{
		[HarmonyPatch(typeof(StatusEntity), "Cmd_RevivePlayer")]
		[HarmonyPrefix]
		public static bool ValidateRevive(StatusEntity __instance, Player _p)
		{
            if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (__instance.netId == _p.netId)
			{
				Main.LogInfraction(__instance, "Unauthorized Action (Self-Revive)", $"Player attempted to revive themselves. Blocked.");
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof(StatusEntity), "Cmd_ReplenishAll")]
		[HarmonyPrefix]
		public static bool ValidateReplenish(StatusEntity __instance)
		{
			if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (__instance.Network_currentHealth <= 0)
			{
				Main.LogInfraction(__instance, "Unauthorized Action (Replenish while Dead)", $"Player attempted to replenish HP/MP while dead. Blocked.");
				return false;
			}
			return true;
		}
	}
    #endregion

    #region Speed Hack & Airborne (Fly/InfJump) Protection
    [HarmonyPatch(typeof(PlayerMove), "Init_Jump")]
    public static class JumpValidationPatch
    {
        public static bool Prefix(PlayerMove __instance)
        {
            if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableAirborneChecks.Value) return true;
            
            uint netId = __instance.netId;
            if (!Main.ServerPlayerAirborneStates.ContainsKey(netId))
            {
                Main.ServerPlayerAirborneStates[netId] = new PlayerAirborneData { AirTime = 0f, LastGroundedPosition = __instance.transform.position, ServerSideJumpCount = 0 };
            }
            
            PlayerAirborneData airData = Main.ServerPlayerAirborneStates[netId];
            if(airData.ServerSideJumpCount >= Main.JumpThreshold.Value) { return false; }
            
            airData.ServerSideJumpCount++;
            Main.ServerPlayerAirborneStates[netId] = airData;
            return true;
        }
    }

	[HarmonyPatch(typeof(PlayerMove), "Update")]
	public static class MovementAndAirborneValidationPatch
	{
		private const float MAX_ALLOWED_AIR_TIME = 10.0f;

		public static void Postfix(PlayerMove __instance)
		{
            if (Main.DisableForHost.Value && Util.IsHost(__instance)) return;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;

			uint netId = __instance.netId;
			if (Main.ServerPlayerGracePeriod.TryGetValue(netId, out float gracePeriodEndTime))
			{
				if (Time.time < gracePeriodEndTime) { return; } else { Main.ServerPlayerGracePeriod.Remove(netId); }
			}
			
            if (Main.EnableSpeedChecks.Value)
			{
				if (Main.ServerPlayerInitialSpeeds.TryGetValue(netId, out float initialSpeed))
                {
                    if (__instance.Network_movSpeed > initialSpeed)
                    {
                        Main.LogInfraction(__instance, "Stat Manipulation (Move Speed)", $"Detected illegal move speed of {__instance.Network_movSpeed}. Reverting to initial speed of {initialSpeed}.");
                        __instance.Network_movSpeed = initialSpeed;
                    }
                }
			}

			Vector3 currentPosition = __instance.transform.position;
			if (Main.EnableMovementChecks.Value)
			{
				if (Main.ServerPlayerPositions.TryGetValue(netId, out PlayerPositionData lastPositionData))
				{
					if (currentPosition != lastPositionData.Position)
					{
						float timeElapsed = Time.time - lastPositionData.Timestamp;
						if (timeElapsed > Main.MovementTimeThreshold.Value)
						{
							float distanceTraveled = Vector3.Distance(lastPositionData.Position, currentPosition);
							float maxPossibleDistance = (Main.MaxEffectiveSpeed.Value * timeElapsed) + Main.MovementGraceBuffer.Value;
							if (distanceTraveled > maxPossibleDistance)
							{
                                string details = $"Moved {distanceTraveled:F1} units in {timeElapsed:F2}s. Reverting position.";
                                foreach(var conn in NetworkServer.connections.Values)
                                {
                                    if(conn?.identity == null || conn.identity.netId == netId) continue;
                                    if(Vector3.Distance(currentPosition, conn.identity.transform.position) < 1.0f)
                                    {
                                        details += " Teleported directly to another player.";
                                        break;
                                    }
                                }
								Main.LogInfraction(__instance, "Movement Hack (Teleport/Speed)", details);
								__instance.transform.position = lastPositionData.Position;
                                Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = lastPositionData.Position, Timestamp = Time.time };
								return;
							}
						}
					}
				}
				Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = currentPosition, Timestamp = Time.time };
			}

			if (Main.EnableAirborneChecks.Value)
			{
                if (__instance._maxJumps > Main.JumpThreshold.Value)
                {
                    Main.LogInfraction(__instance, "Stat Manipulation (Max Jumps)", $"Client reported _maxJumps of {__instance._maxJumps}. Reverting to {Main.JumpThreshold.Value}.");
                    __instance._maxJumps = Main.JumpThreshold.Value;
                }
				
                bool isGrounded = __instance.RayGroundCheck();
                if (!Main.ServerPlayerAirborneStates.TryGetValue(netId, out PlayerAirborneData airData))
                { airData = new PlayerAirborneData { AirTime = 0f, LastGroundedPosition = currentPosition, ServerSideJumpCount = 0 }; }

				if (isGrounded)
                {
                    airData.AirTime = 0f;
                    airData.LastGroundedPosition = currentPosition;
                    airData.ServerSideJumpCount = 0;
                }
				else
                {
                    airData.AirTime += Time.deltaTime;
                }

				if (airData.AirTime > MAX_ALLOWED_AIR_TIME)
				{
					Main.LogInfraction(__instance, "Movement Hack (Fly/Sustained Jump)", $"Airborne for {airData.AirTime:F1} seconds. Reverting to last ground position.");
					__instance.transform.position = airData.LastGroundedPosition;
					airData.AirTime = 0f;
                    airData.ServerSideJumpCount = 0;
				}
				Main.ServerPlayerAirborneStates[netId] = airData;
			}
		}
	}
    #endregion
    
    #region Experience and Level Manipulation Protection
	[HarmonyPatch]
	public static class ExperienceValidationPatch
	{
		[HarmonyPatch(typeof(PlayerStats), "set_Network_currentExp")]
		[HarmonyPrefix]
		public static bool ValidateExperienceChange(PlayerStats __instance, ref int value)
		{
            if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;
			
            uint netId = __instance.netId;
			if (!Main.ServerPlayerStats.TryGetValue(netId, out PlayerStatsData stat))
			{
				Main.ServerPlayerStats[netId] = new PlayerStatsData { Experience = value, Level = __instance.Network_currentLevel };
				return true;
			}
			int oldExperience = stat.Experience;
			int experienceGain = value - oldExperience;

			if (experienceGain > Main.MaxPlausibleXPGain.Value)
			{
				Main.LogInfraction(__instance, "Stat Manipulation (Experience)", $"Attempted to gain {experienceGain} XP at once. Blocked.");
				value = oldExperience;
			}
			PlayerStatsData currentStats = Main.ServerPlayerStats[netId];
			currentStats.Experience = value;
			Main.ServerPlayerStats[netId] = currentStats;
			return true;
		}
		[HarmonyPatch(typeof(PlayerStats), "set_Network_currentLevel")]
		[HarmonyPrefix]
		public static bool ValidateLevelChange(PlayerStats __instance, ref int value)
		{
			if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;

			uint netId = __instance.netId;
			if (!Main.ServerPlayerStats.TryGetValue(netId, out PlayerStatsData stat))
			{
				Main.ServerPlayerStats[netId] = new PlayerStatsData { Experience = __instance.Network_currentExp, Level = value };
				return true;
			}
			int oldLevel = stat.Level;
			int levelGain = value - oldLevel;
			if (levelGain > 1)
			{
				Main.LogInfraction(__instance, "Stat Manipulation (Level)", $"Attempted to jump from level {oldLevel} to {value}. Blocked.");
				value = oldLevel;
			}
			PlayerStatsData currentStats = Main.ServerPlayerStats[netId];
			currentStats.Level = value;
			Main.ServerPlayerStats[netId] = currentStats;
			return true;
		}
	}
    #endregion

    #region Skill Cooldown Protection (REWRITTEN)
	[HarmonyPatch]
	public static class CombatValidationPatch
	{
        [HarmonyPatch(typeof(PlayerCasting), "Update")]
        [HarmonyPostfix]
        public static void CooldownUpdate(PlayerCasting __instance)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCooldownChecks.Value) return;

            uint netId = __instance.netId;
            if (Main.ServerRemainingCooldowns.TryGetValue(netId, out var playerSkills))
            {
                var skillsToRemove = new List<string>();
                var skillKeys = new List<string>(playerSkills.Keys);

                foreach (var skillName in skillKeys)
                {
                    float timeToReduce = Time.deltaTime + (Time.deltaTime * __instance._cooldownMod);
                    float newRemainingTime = playerSkills[skillName] - timeToReduce;

                    if (newRemainingTime <= 0)
                    {
                        skillsToRemove.Add(skillName);
                    }
                    else
                    {
                        playerSkills[skillName] = newRemainingTime;
                    }
                }

                foreach (var skillName in skillsToRemove)
                {
                    playerSkills.Remove(skillName);
                }
            }
        }
        
        [HarmonyPatch(typeof(PlayerCasting), "Server_CastSkill")]
		[HarmonyPrefix]
		public static bool UnifiedCooldownValidation(PlayerCasting __instance)
		{
            if (Main.DisableForHost.Value && Util.IsHost(__instance)) return true;
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCooldownChecks.Value) return true;

            ScriptableSkill skillToCast = __instance._currentCastSkill;
            if (skillToCast == null) return true; 

			uint netId = __instance.netId;
			
            if (Main.ServerRemainingCooldowns.TryGetValue(netId, out var playerSkills) && playerSkills.ContainsKey(skillToCast.name))
			{
                __instance.Server_InterruptCast();
				return false; 
			}

            if (!Main.ServerRemainingCooldowns.ContainsKey(netId)) Main.ServerRemainingCooldowns[netId] = new Dictionary<string, float>();
			Main.ServerRemainingCooldowns[netId][skillToCast.name] = skillToCast._skillRankParams._baseCooldown;

			return true;
		}
	}
    #endregion

    #region Network Connection Cleanup
	[HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerDisconnect")]
	public static class PlayerDisconnectPatch
	{
		public static void Postfix(NetworkConnectionToClient conn)
		{
			if (conn?.identity != null)
			{
				uint netId = conn.identity.netId;
                Main.ClearAllPlayerData(netId);
			}
		}
	}
    #endregion
}