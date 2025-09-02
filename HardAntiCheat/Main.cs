using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace HardAntiCheat
{
    #region Data Structures
	public struct PlayerPositionData
	{
		public Vector3 Position;
		public float Timestamp;
	}

	public struct PlayerStatsData
	{
		public int Level;
		public int Experience;
	}

	public struct PlayerAirborneData
	{
		public float AirTime;
		public Vector3 LastGroundedPosition;
	}
    #endregion

	[BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
	[BepInDependency("Marioalexsan.PerfectGuard", BepInDependency.DependencyFlags.SoftDependency)]
	public class Main : BaseUnityPlugin
	{
		internal static ManualLogSource Log;
		internal static string InfractionLogPath;

		// --- CONFIGURATION ENTRIES ---
		public static ConfigEntry<bool> EnableAntiCheat;
		public static ConfigEntry<bool> DisableForHost;
		public static ConfigEntry<bool> EnableMovementChecks;
		public static ConfigEntry<bool> EnableAirborneChecks;
		public static ConfigEntry<bool> EnableSpeedChecks;
		public static ConfigEntry<bool> EnableCurrencyChecks;
		public static ConfigEntry<bool> EnableItemChecks;
		public static ConfigEntry<bool> EnableExperienceChecks;
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
		public static readonly Dictionary<uint, int> ServerPlayerCurrency = new Dictionary<uint, int>();
		private readonly Harmony harmony = new Harmony(ModInfo.GUID);

		private void Awake()
		{
			Log = Logger;
			InfractionLogPath = Path.Combine(Path.GetDirectoryName(Info.Location), $"{ModInfo.NAME}_InfractionLog.txt");

			EnableAntiCheat = Config.Bind("1. General", "Enable AntiCheat", true, "Master switch to enable or disable all anti-cheat modules.");
			DisableForHost = Config.Bind("1. General", "Disable Detections for Host", true, "If true, the player hosting the server will not be checked for infractions. Recommended for hosts who use admin commands.");

			EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Speed Checks", true, "Checks if players are moving faster than physically possible across the map.");
			EnableAirborneChecks = Config.Bind("2. Movement Detections", "Enable Fly/Infinite Jump Checks", true, "Checks if players are airborne for an impossibly long time.");
			EnableSpeedChecks = Config.Bind("2. Movement Detections", "Enable Max MoveSpeed Check", true, "Prevents players from setting their base movement speed stat to illegal values.");

			EnableCurrencyChecks = Config.Bind("3. Economy Detections", "Enable Currency Checks", true, "Prevents players from adding impossibly large amounts of currency at once and corrects any invalid currency totals.");
			EnableItemChecks = Config.Bind("3. Economy Detections", "Enable Item Enchantment Checks", true, "Prevents players from applying illegal enchantments/modifiers to their items by reverting them.");

			EnableExperienceChecks = Config.Bind("4. Stat Detections", "Enable Experience/Level Checks", true, "Prevents players from gaining huge amounts of XP or multiple levels at once.");

			EnableCooldownChecks = Config.Bind("5. Combat Detections", "Enable Skill Cooldown Checks", true, "Prevents players from using skills faster than their cooldowns allow (also counters God Mode spam).");
			EnableCastTimeChecks = Config.Bind("5. Combat Detections", "Enable Skill Cast Time Checks", true, "Prevents players from instantly using skills that have a cast/channel time.");
			EnableReviveChecks = Config.Bind("5. Combat Detections", "Enable Self-Revive Checks", true, "Prevents players from reviving themselves while dead.");

			EnablePunishmentSystem = Config.Bind("6. Punishments", "Enable Punishment System", true, "If enabled, the server will automatically take action against players who accumulate too many infractions.");
			WarningsUntilAction = Config.Bind("6. Punishments", "Infractions Until Action", 5, "Number of infractions a player can commit before the selected action is taken.");
			ActionType = Config.Bind("6. Punishments", "Action Type", "Kick", new ConfigDescription("The action to take when a player reaches the infraction limit.", new AcceptableValueList<string>("Kick", "Ban")));

			harmony.PatchAll();
			Log.LogInfo($"[{ModInfo.NAME}] has been loaded. Infractions will be logged to: {InfractionLogPath}");
		}

		public static void LogInfraction(NetworkBehaviour instance, string cheatType, string details)
		{
			Player player = instance.GetComponent<Player>();
			string playerName = player?._nickname ?? "Unknown";
			string playerID = player?._steamID ?? $"netId:{instance.netId}";
			string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Player: {playerName} (ID: {playerID}) | Type: {cheatType} | Details: {details}";
			Log.LogWarning(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }

			if (!EnablePunishmentSystem.Value || AtlyssNetworkManager._current._soloMode) return;

			uint netId = instance.netId;
			if (!ServerPlayerInfractionCount.ContainsKey(netId)) ServerPlayerInfractionCount[netId] = 0;
			ServerPlayerInfractionCount[netId]++;

			int currentInfractions = ServerPlayerInfractionCount[netId];
			int maxInfractions = WarningsUntilAction.Value;

			if (currentInfractions >= maxInfractions)
			{
				if (NetworkServer.active && player != null && HostConsole._current != null && player.connectionToClient != null)
				{
					// --- NEW PUNISHMENT LOGIC ---
					HC_PeerListEntry targetPeer = null;
					foreach (var entry in HostConsole._current._peerListEntries)
					{
						if (entry._netId != null && entry._netId.netId == netId)
						{
							targetPeer = entry;
							break;
						}
					}

					if (targetPeer != null)
					{
						string action = ActionType.Value.ToLower();
						string punishmentDetails = $"Player {playerName} (ID: {playerID}) was automatically {action.ToUpper()}ed for reaching {currentInfractions}/{maxInfractions} infractions.";
						Log.LogWarning(punishmentDetails);
						try { File.AppendAllText(InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PUNISHMENT] " + punishmentDetails + Environment.NewLine); }
						catch (Exception ex) { Log.LogError($"Failed to write punishment to log: {ex.Message}"); }

						// Select the peer and execute the action
						HostConsole._current._selectedPeerEntry = targetPeer;
						if (action == "kick")
						{
							HostConsole._current.Kick_Peer();
						}
						else
						{
							HostConsole._current.Ban_Peer();
						}

						ClearAllPlayerData(netId);
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
			ServerPlayerCurrency.Remove(netId);
		}
	}

    #region Player Spawn Grace Period
	[HarmonyPatch(typeof (PlayerMove), "Start")]
	public static class PlayerSpawnPatch
	{
		private const float GRACE_PERIOD_SECONDS = 3.0f;
		public static void Postfix(PlayerMove __instance)
		{
			if (!NetworkServer.active) return;
			Main.ServerPlayerGracePeriod[__instance.netId] = Time.time + GRACE_PERIOD_SECONDS;
			Main.Log.LogInfo($"Player (netId: {__instance.netId}) has spawned. Applying movement check grace period for {GRACE_PERIOD_SECONDS} seconds.");
		}
	}
    #endregion

    #region Server Authority Protection (Self-Revive)
	[HarmonyPatch]
	public static class ServerAuthorityValidationPatch
	{
		[HarmonyPatch(typeof (StatusEntity), "Cmd_RevivePlayer")]
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

		[HarmonyPatch(typeof (StatusEntity), "Cmd_ReplenishAll")]
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
	[HarmonyPatch(typeof (PlayerMove), "set_Network_movSpeed")]
	public static class SpeedValidationPatch
	{
		private const float MAX_LEGAL_MOVESPEED = 40f;
		public static bool Prefix(PlayerMove __instance, ref float value)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableSpeedChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
			if (value > MAX_LEGAL_MOVESPEED)
			{
				Main.LogInfraction(__instance, "Stat Manipulation (Move Speed)", $"Attempted to set move speed to {value}. Allowed max: {MAX_LEGAL_MOVESPEED}.");
				value = MAX_LEGAL_MOVESPEED;
			}
			return true;
		}
	}

	[HarmonyPatch(typeof (PlayerMove), "Update")]
	public static class MovementAndAirborneValidationPatch
	{
		private const float MAX_PLAYER_SPEED = 25f;
		private const float GRACE_BUFFER_DISTANCE = 3.0f;
		private const float MAX_ALLOWED_AIR_TIME = 10.0f;
		public static void Postfix(PlayerMove __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return;

			uint netId = __instance.netId;
			if (Main.ServerPlayerGracePeriod.TryGetValue(netId, out float gracePeriodEndTime))
			{
				if (Time.time < gracePeriodEndTime) { return; }
				else
				{
					Main.ServerPlayerGracePeriod.Remove(netId);
					Main.Log.LogInfo($"Grace period for Player (netId: {netId}) has ended. Resuming movement checks.");
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
						if (timeElapsed > 0.1f)
						{
							float distanceTraveled = Vector3.Distance(lastPositionData.Position, currentPosition);
							float maxPossibleDistance = (MAX_PLAYER_SPEED * timeElapsed) + GRACE_BUFFER_DISTANCE;
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
				bool isGrounded = Physics.SphereCast(new Ray(currentPosition, Vector3.down), 0.3f, 1.0f, CastLayers.GroundMask);
				if (!Main.ServerPlayerAirborneStates.TryGetValue(netId, out PlayerAirborneData airData)) { airData = new PlayerAirborneData { AirTime = 0f, LastGroundedPosition = currentPosition }; }
				if (isGrounded)
				{
					airData.AirTime = 0f;
					airData.LastGroundedPosition = currentPosition;
				}
				else { airData.AirTime += Time.deltaTime; }
				if (airData.AirTime > MAX_ALLOWED_AIR_TIME)
				{
					Main.LogInfraction(__instance, "Movement Hack (Fly/Infinite Jump)", $"Airborne for {airData.AirTime:F1} seconds. Reverting to last ground position.");
					__instance.transform.position = airData.LastGroundedPosition;
					airData.AirTime = 0f;
				}
				Main.ServerPlayerAirborneStates[netId] = airData;
			}
		}
	}
    #endregion

    #region Currency & Item Audit Protection
	[HarmonyPatch]
	public static class AuditAndValidationPatch
	{
		private const int MAX_PLAUSIBLE_CURRENCY_GAIN = 50000;

		[HarmonyPatch(typeof (PlayerInventory), "Cmd_AddCurrency")]
		[HarmonyPrefix]
		public static bool ValidateCurrencyAdd(PlayerInventory __instance, int _value)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCurrencyChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
			if (_value > MAX_PLAUSIBLE_CURRENCY_GAIN)
			{
				Main.LogInfraction(__instance, "Currency Manipulation", $"Attempted to add an impossibly large amount ({_value}). Blocked.");
				return false;
			}
			if (_value < 0)
			{
				Main.LogInfraction(__instance, "Currency Manipulation", $"Attempted to add a negative amount ({_value}). Blocked.");
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof (PlayerInventory), "Cmd_AddCurrency")]
		[HarmonyPostfix]
		public static void UpdateServerCurrencyOnAdd(PlayerInventory __instance, int _value)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCurrencyChecks.Value) return;
			if (Main.ServerPlayerCurrency.ContainsKey(__instance.netId)) { Main.ServerPlayerCurrency[__instance.netId] += _value; }
		}

		[HarmonyPatch(typeof (PlayerInventory), "Cmd_SubtractCurrency")]
		[HarmonyPrefix]
		public static bool ValidateCurrencySubtract(PlayerInventory __instance, int _value)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCurrencyChecks.Value) return true;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return true;
			if (_value < 0)
			{
				Main.LogInfraction(__instance, "Currency Manipulation", $"Attempted to subtract a negative amount ({_value}). Blocked.");
				return false;
			}
			if (Main.ServerPlayerCurrency.TryGetValue(__instance.netId, out int serverBalance) && _value > serverBalance)
			{
				Main.LogInfraction(__instance, "Currency Manipulation", $"Attempted to subtract more currency ({_value}) than they have ({serverBalance}). Blocked.");
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof (PlayerInventory), "Cmd_SubtractCurrency")]
		[HarmonyPostfix]
		public static void UpdateServerCurrencyOnSubtract(PlayerInventory __instance, int _value)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCurrencyChecks.Value) return;
			if (Main.ServerPlayerCurrency.ContainsKey(__instance.netId)) { Main.ServerPlayerCurrency[__instance.netId] -= _value; }
		}

		[HarmonyPatch(typeof (PlayerInventory), "Update")]
		[HarmonyPostfix]
		public static void AuditInventoryState(PlayerInventory __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return;

			if (Main.EnableCurrencyChecks.Value)
			{
				uint netId = __instance.netId;
				int clientReportedCurrency = __instance._heldCurrency;

				if (!Main.ServerPlayerCurrency.ContainsKey(netId)) { Main.ServerPlayerCurrency[netId] = clientReportedCurrency; }
				else
				{
					int serverAuthoritativeCurrency = Main.ServerPlayerCurrency[netId];
					if (clientReportedCurrency != serverAuthoritativeCurrency)
					{
						Main.LogInfraction(__instance, "Currency Desync / Manipulation", $"Client currency ({clientReportedCurrency}) did not match server record ({serverAuthoritativeCurrency}). Reverting.");
						__instance.Network_heldCurrency = serverAuthoritativeCurrency;
					}
				}
			}

			if (Main.EnableItemChecks.Value)
			{
				foreach (ItemData item in __instance._heldItems)
				{
					if (item == null || !item._isEquipped) continue;

					ScriptableEquipment baseItemBlueprint = GameManager._current.Locate_Item(item._itemName) as ScriptableEquipment;
					if (baseItemBlueprint == null) continue;

					if (item._modifierID != 0)
					{
						bool isValidModifier = false;
						if (baseItemBlueprint._statModifierTable != null)
						{
							foreach (var legalModifierSlot in baseItemBlueprint._statModifierTable._statModifierSlots)
							{
								if (legalModifierSlot._equipModifier._modifierID == item._modifierID)
								{
									isValidModifier = true;
									break;
								}
							}
						}
						if (!isValidModifier)
						{
							Main.LogInfraction(__instance, "Item Manipulation (Enchant Audit)", $"Found illegal modifier ID ({item._modifierID}) on equipped item {item._itemName}. Reverting.");
							item._modifierID = 0;
						}
					}
				}
			}
		}
	}
    #endregion

    #region Experience and Level Manipulation Protection
	[HarmonyPatch]
	public static class ExperienceValidationPatch
	{
		private const int MAX_PLAUSIBLE_XP_GAIN = 50000;
		[HarmonyPatch(typeof (PlayerStats), "set_Network_currentExp")]
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
			if (experienceGain > MAX_PLAUSIBLE_XP_GAIN)
			{
				Main.LogInfraction(__instance, "Stat Manipulation (Experience)", $"Attempted to gain {experienceGain} XP at once. Blocked.");
				value = oldExperience;
			}
			PlayerStatsData currentStats = Main.ServerPlayerStats[netId];
			currentStats.Experience = value;
			Main.ServerPlayerStats[netId] = currentStats;
			return true;
		}
		[HarmonyPatch(typeof (PlayerStats), "set_Network_currentLevel")]
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
	[HarmonyPatch(typeof (PlayerCasting), "Server_CastSkill")]
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
					Main.LogInfraction(__instance, "Skill Cooldown Manipulation", $"Used skill '{skillToCast.name}' too early. Blocked.");
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
		[HarmonyPatch(typeof (PlayerCasting), "Cmd_InitSkill")]
		[HarmonyPostfix]
		public static void RecordCastStartTime(NetworkBehaviour __instance)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCastTimeChecks.Value) return;
			if (Main.DisableForHost.Value && __instance.isServer && __instance.isClient) return;
			Main.ServerPlayerCastStartTime[__instance.netId] = Time.time;
		}

		[HarmonyPatch(typeof (PlayerCasting), "Cmd_CastInit")]
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

		[HarmonyPatch(typeof (PlayerCasting), "Cmd_CastInit")]
		[HarmonyPostfix]
		public static void CleanupCastStartTime(NetworkBehaviour __instance)
		{
			if (!NetworkServer.active) return;
			Main.ServerPlayerCastStartTime.Remove(__instance.netId);
		}
	}
    #endregion

    #region Network Connection Cleanup
	[HarmonyPatch(typeof (AtlyssNetworkManager), "OnServerDisconnect")]
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