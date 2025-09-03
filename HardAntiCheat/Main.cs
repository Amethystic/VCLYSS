using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HardAntiCheat
{
    #region Data Structures
    public struct PlayerPositionData { public Vector3 Position; public float Timestamp; }
	public struct PlayerStatsData { public int Level; public int Experience; }
	public struct PlayerAirborneData { public float AirTime; public Vector3 LastGroundedPosition; public int ServerSideJumpCount; }
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
        public static ConfigEntry<bool> EnableMovementChecks;
		public static ConfigEntry<bool> EnableAirborneChecks;
		public static ConfigEntry<bool> EnableExperienceChecks;
        public static ConfigEntry<int> MaxPlausibleXPGain;
		public static ConfigEntry<bool> EnableCooldownChecks;
		public static ConfigEntry<bool> EnableCastTimeChecks;
		public static ConfigEntry<bool> EnableReviveChecks;
		public static ConfigEntry<bool> EnablePunishmentSystem;
		public static ConfigEntry<int> WarningsUntilAction;
		public static ConfigEntry<string> ActionType;

		// --- SERVER-SIDE DATA DICTIONARIES ---
		public static readonly Dictionary<uint, Dictionary<string, float>> ServerPlayerCooldowns = new Dictionary<uint, Dictionary<string, float>>();
		public static readonly Dictionary<uint, float> ServerPlayerCastStartTime = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, PlayerPositionData> ServerPlayerPositions = new Dictionary<uint, PlayerPositionData>();
		public static readonly Dictionary<uint, PlayerStatsData> ServerPlayerStats = new Dictionary<uint, PlayerStatsData>();
		public static readonly Dictionary<uint, PlayerAirborneData> ServerPlayerAirborneStates = new Dictionary<uint, PlayerAirborneData>();
		public static readonly Dictionary<uint, int> ServerPlayerInfractionCount = new Dictionary<uint, int>();
		public static readonly Dictionary<uint, float> ServerPlayerGracePeriod = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerPunishmentCooldown = new Dictionary<uint, float>();
		
		private void Awake()
		{
			Log = Logger;
			InfractionLogPath = Path.Combine(Path.GetDirectoryName(Info.Location), $"{ModInfo.NAME}_InfractionLog.txt");

			EnableAntiCheat = Config.Bind("1. General", "Enable AntiCheat", true, "Master switch to enable or disable all anti-cheat modules.");
			DisableForHost = Config.Bind("1. General", "Disable Detections for Host", true, "If true, the player hosting the server will not be checked for infractions. Recommended for hosts who use admin commands.");
			
            EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Speed Checks", true, "Checks if players are moving faster than physically possible across the map (catches teleports and physics-based speed hacks).");
			EnableAirborneChecks = Config.Bind("2. Movement Detections", "Enable Fly/Infinite Jump Checks", true, "Checks if players are airborne for an impossibly long time and have an invalid number of max jumps.");
            
			EnableExperienceChecks = Config.Bind("3. Stat Detections", "Enable Experience/Level Checks", true, "Prevents players from gaining huge amounts of XP or multiple levels at once based on the 'Max Plausible XP Gain' limit.");
            MaxPlausibleXPGain = Config.Bind("3. Stat Detections", "Max Plausible XP Gain", 50000, "The maximum amount of XP a player can gain in a single transaction. Adjust this based on your server's max XP rewards.");

			EnableCooldownChecks = Config.Bind("4. Combat Detections", "Enable Skill Cooldown Checks", true, "Silently enforces server-side cooldowns, blocking premature skill usage from cheaters.");
			EnableCastTimeChecks = Config.Bind("4. Combat Detections", "Enable Skill Cast Time Checks", true, "Prevents players from instantly using skills that have a cast/channel time.");
			EnableReviveChecks = Config.Bind("4. Combat Detections", "Enable Self-Revive Checks", true, "Prevents players from reviving themselves while dead.");

			EnablePunishmentSystem = Config.Bind("5. Punishments", "Enable Punishment System", true, "If enabled, the server will automatically take action against players who accumulate too many infractions.");
			WarningsUntilAction = Config.Bind("5. Punishments", "Infractions Until Action", 5, "Number of infractions a player can commit before the selected action is taken.");
			ActionType = Config.Bind("5. Punishments", "Action Type", "Kick", new ConfigDescription("The action to take when a player reaches the infraction limit.", new AcceptableValueList<string>("Kick", "Ban")));

			harmony.PatchAll();
			Log.LogInfo($"[{ModInfo.NAME}] has been loaded. Infractions will be logged to: {InfractionLogPath}");
		}

		public static void LogInfraction(NetworkBehaviour instance, string cheatType, string details)
		{
            uint netId = instance.netId;
            if (ServerPunishmentCooldown.TryGetValue(netId, out float cooldownEndTime) && Time.time < cooldownEndTime) { return; }
            ServerPunishmentCooldown.Remove(netId);

			Player player = instance.GetComponent<Player>();
			string playerName = player?._nickname ?? "Unknown";
			string playerID = player?._steamID ?? $"netId:{netId}";
			string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Player: {playerName} (ID: {playerID}) | Type: {cheatType} | Details: {details}";
			Log.LogWarning(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }

			if (!EnablePunishmentSystem.Value || AtlyssNetworkManager._current._soloMode) return;
			
			if (!ServerPlayerInfractionCount.ContainsKey(netId)) ServerPlayerInfractionCount[netId] = 0;
			ServerPlayerInfractionCount[netId]++;

			int currentInfractions = ServerPlayerInfractionCount[netId];
			int maxInfractions = WarningsUntilAction.Value;

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
                        string action = ActionType.Value.ToLower();
					    string punishmentDetails = $"Player {playerName} (ID: {playerID}) was automatically {action.ToUpper()}ed for reaching {currentInfractions}/{maxInfractions} infractions.";
					    Log.LogWarning(punishmentDetails);
					    try { File.AppendAllText(InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PUNISHMENT] " + punishmentDetails + Environment.NewLine); }
					    catch (Exception ex) { Log.LogError($"Failed to write punishment to log: {ex.Message}"); }
                        
                        ServerPunishmentCooldown[netId] = Time.time + 60f;

                        HostConsole._current._selectedPeerEntry = targetPeer;
                        if(action == "kick") { HostConsole._current.Kick_Peer(); } else { HostConsole._current.Ban_Peer(); }
                        
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
            ServerPlayerCooldowns.Remove(netId);
            ServerPlayerCastStartTime.Remove(netId);
            ServerPlayerPositions.Remove(netId);
            ServerPlayerStats.Remove(netId);
            ServerPlayerAirborneStates.Remove(netId);
            ServerPlayerInfractionCount.Remove(netId);
            ServerPlayerGracePeriod.Remove(netId);
            ServerPunishmentCooldown.Remove(netId);
        }
	}
    
    #region Player Spawn Grace Period
	[HarmonyPatch(typeof(PlayerMove), "Start")]
	public static class PlayerSpawnPatch
	{
		private const float GRACE_PERIOD_SECONDS = 3.0f;
		public static void Postfix(PlayerMove __instance)
		{
			if (!NetworkServer.active) return;
			Main.ServerPlayerGracePeriod[__instance.netId] = Time.time + GRACE_PERIOD_SECONDS;
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
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
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
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
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
        private const int MAX_LEGAL_JUMPS = 3;

        public static bool Prefix(PlayerMove __instance)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableAirborneChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
            
            uint netId = __instance.netId;
            if (Main.ServerPlayerAirborneStates.TryGetValue(netId, out PlayerAirborneData airData))
            {
                if(airData.ServerSideJumpCount >= MAX_LEGAL_JUMPS) { return false; }
                airData.ServerSideJumpCount++;
                Main.ServerPlayerAirborneStates[netId] = airData;
            }
            return true;
        }
    }

	[HarmonyPatch(typeof(PlayerMove), "Update")]
	public static class MovementAndAirborneValidationPatch
	{
		private const float MAX_EFFECTIVE_SPEED = 120f;
		private const float GRACE_BUFFER_DISTANCE = 5.0f;
		private const float MAX_ALLOWED_AIR_TIME = 10.0f;
        private const int MAX_LEGAL_MAXJUMPS = 3;
		public static void Postfix(PlayerMove __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return;

			uint netId = __instance.netId;
			if (Main.ServerPlayerGracePeriod.TryGetValue(netId, out float gracePeriodEndTime))
			{
				if (Time.time < gracePeriodEndTime) { return; } else { Main.ServerPlayerGracePeriod.Remove(netId); }
			}

			Vector3 currentPosition = __instance.transform.position;
			if (Main.EnableMovementChecks.Value)
			{
				if (Main.ServerPlayerPositions.TryGetValue(netId, out PlayerPositionData lastPositionData))
				{
					if (currentPosition != lastPositionData.Position)
					{
						float timeElapsed = Time.time - lastPositionData.Timestamp;
						if (timeElapsed > 0.1f)
						{
							float distanceTraveled = Vector3.Distance(lastPositionData.Position, currentPosition);
							float maxPossibleDistance = (MAX_EFFECTIVE_SPEED * timeElapsed) + GRACE_BUFFER_DISTANCE;
							if (distanceTraveled > maxPossibleDistance)
							{
								Main.LogInfraction(__instance, "Movement Hack (Teleport/Speed)", $"Moved {distanceTraveled:F1} units in {timeElapsed:F2}s. Reverting position.");
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
                if (__instance._maxJumps > MAX_LEGAL_MAXJUMPS)
                {
                    Main.LogInfraction(__instance, "Stat Manipulation (Max Jumps)", $"Client reported _maxJumps of {__instance._maxJumps}. Reverting to {MAX_LEGAL_MAXJUMPS}.");
                    __instance._maxJumps = MAX_LEGAL_MAXJUMPS;
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
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
			uint netId = __instance.netId;
			if (!Main.ServerPlayerStats.ContainsKey(netId))
			{
				Main.ServerPlayerStats[netId] = new PlayerStatsData { Experience = value, Level = __instance.Network_currentLevel };
				return true;
			}
			int oldExperience = Main.ServerPlayerStats[netId].Experience;
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
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
			uint netId = __instance.netId;
			if (!Main.ServerPlayerStats.ContainsKey(netId))
			{
				Main.ServerPlayerStats[netId] = new PlayerStatsData { Experience = __instance.Network_currentExp, Level = value };
				return true;
			}
			int oldLevel = Main.ServerPlayerStats[netId].Level;
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

    #region Skill Cooldown & Cast Time Protection
	[HarmonyPatch(typeof(PlayerCasting), "Server_CastSkill")]
	public static class ServerCastValidationPatch
	{
		public static bool Prefix(PlayerCasting __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCooldownChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;

            ScriptableSkill skillToCast = __instance._currentCastSkill;
            if (skillToCast == null) return true;

			uint netId = __instance.netId;
			
            if (Main.ServerPlayerCooldowns.TryGetValue(netId, out var playerSkills) && playerSkills.TryGetValue(skillToCast.name, out float lastUsedTime))
			{
				float officialCooldown = skillToCast._skillRankParams._baseCooldown;
				if (Time.time - lastUsedTime < officialCooldown)
				{
					return false;
				}
			}

			if (!Main.ServerPlayerCooldowns.ContainsKey(netId)) Main.ServerPlayerCooldowns[netId] = new Dictionary<string, float>();
			Main.ServerPlayerCooldowns[netId][skillToCast.name] = Time.time;
			return true;
		}
	}

	[HarmonyPatch]
	public static class CastTimeValidationPatch
	{
		[HarmonyPatch(typeof(PlayerCasting), "Cmd_InitSkill")]
		[HarmonyPostfix]
		public static void RecordCastStartTime(NetworkBehaviour __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCastTimeChecks.Value) return;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return;
			Main.ServerPlayerCastStartTime[__instance.netId] = Time.time;
		}

		[HarmonyPatch(typeof(PlayerCasting), "Cmd_CastInit")]
		[HarmonyPrefix]
		public static bool ValidateCastFinishTime(PlayerCasting __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCastTimeChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;

			uint netId = __instance.netId;
			if (!Main.ServerPlayerCastStartTime.TryGetValue(netId, out float castStartTime))
			{
				Main.LogInfraction(__instance, "Skill Cast Time Manipulation", $"Finished a cast that was never started. Blocked.");
				return false;
			}
            
            ScriptableSkill currentSkill = __instance._currentCastSkill;
            if (currentSkill == null) return true;

			float officialCastTime = currentSkill._skillRankParams._baseCastTime;
			float elapsedTime = Time.time - castStartTime;

			if (elapsedTime < (officialCastTime * 0.9f))
			{
				Main.LogInfraction(__instance, "Skill Cast Time Manipulation", $"Finished a {officialCastTime}s cast in {elapsedTime:F2}s. Blocked.");
				Main.ServerPlayerCastStartTime.Remove(netId);
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof(PlayerCasting), "Cmd_CastInit")]
		[HarmonyPostfix]
		public static void CleanupCastStartTime(NetworkBehaviour __instance)
		{
			if (!NetworkServer.active) return;
			Main.ServerPlayerCastStartTime.Remove(__instance.netId);
		}
	}
    #endregion

    #region Network Connection Cleanup
	[HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerDisconnect")]
	public static class PlayerDisconnectPatch
	{
		public static void Postfix(NetworkConnectionToClient _conn)
		{
			if (_conn?.identity != null)
			{
				uint netId = _conn.identity.netId;
                Main.ClearAllPlayerData(netId);
			}
		}
	}
    #endregion
}