using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CodeTalker.Networking;
using CodeTalker.Packets;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace HardAntiCheat
{
    #region Data & Packet Structures
    public class FluffDetectionState
    {
        public float SparkleTimestamp = 0f;
    }
    public struct PlayerPositionData { public Vector3 Position; public float Timestamp; }
	public struct PlayerStatsData { public int Level; public int Experience; }
	public struct PlayerAirborneData { public float AirTime; public Vector3 LastGroundedPosition; public int ServerSideJumpCount; public float LastVerticalPosition; public float VerticalStallTime; }
    
    public class AntiCheatHandshakeRequest : PacketBase
    {
        public override string PacketSourceGUID => ModInfo.GUID;
    }

    public class AntiCheatHandshakeResponse : PacketBase
    {
        public override string PacketSourceGUID => ModInfo.GUID;
    }
    #endregion

	[BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
	[BepInDependency("Marioalexsan.PerfectGuard", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("CodeTalker")] 
	public class Main : BaseUnityPlugin
	{
		private readonly Harmony harmony = new Harmony(ModInfo.GUID);
		internal static ManualLogSource Log;
		internal static string InfractionLogPath;
        public static Main Instance { get; private set; }


		// --- CONFIGURATION ENTRIES ---
		public static ConfigEntry<bool> EnableAntiCheat;
		public static ConfigEntry<bool> DisableForHost;
        public static ConfigEntry<bool> EnableClientVerification;
        public static ConfigEntry<float> VerificationTimeout;
        public static ConfigEntry<int> MaxLogFileSizeMB;
        public static ConfigEntry<bool> EnableMovementChecks;
		public static ConfigEntry<float> MaxEffectiveSpeed;
        public static ConfigEntry<float> MovementGraceBuffer;
        public static ConfigEntry<float> TeleportDistanceThreshold;
		public static ConfigEntry<bool> EnableAirborneChecks;
		public static ConfigEntry<bool> EnableSpeedChecks;
        public static ConfigEntry<float> SpeedHackDetectionCooldown;
        public static ConfigEntry<float> JumpHackDetectionCooldown;
        public static ConfigEntry<float> AirborneHackDetectionCooldown;
		public static ConfigEntry<bool> EnableExperienceChecks;
		public static ConfigEntry<int> JumpThreshold;
        public static ConfigEntry<int> MaxPlausibleXPGain;
        public static ConfigEntry<int> MaxPlausibleCurrencyGain;
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

		// --- SERVER-SIDE MOD DETECTION DICTIONARIES ---
        public static readonly Dictionary<uint, FluffDetectionState> FluffWatchlist = new Dictionary<uint, FluffDetectionState>();
        
		// --- SERVER-SIDE DATA DICTIONARIES ---
		public static readonly Dictionary<uint, Dictionary<string, float>> ServerRemainingCooldowns = new Dictionary<uint, Dictionary<string, float>>();
        public static readonly Dictionary<uint, PlayerPositionData> ServerPlayerPositions = new Dictionary<uint, PlayerPositionData>();
		public static readonly Dictionary<uint, PlayerStatsData> ServerPlayerStats = new Dictionary<uint, PlayerStatsData>();
		public static readonly Dictionary<uint, PlayerAirborneData> ServerPlayerAirborneStates = new Dictionary<uint, PlayerAirborneData>();
        public static readonly Dictionary<uint, float> ServerPlayerInitialSpeeds = new Dictionary<uint, float>();
		public static readonly Dictionary<uint, int> ServerPlayerInfractionCount = new Dictionary<uint, int>();
		public static readonly Dictionary<uint, float> ServerPlayerGracePeriod = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerPunishmentCooldown = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerSpeedCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerJumpCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerAirborneCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> AuthorizedSelfRevives = new Dictionary<uint, float>();
		
        internal static readonly Dictionary<int, Coroutine> PendingVerification = new Dictionary<int, Coroutine>();
        internal static readonly HashSet<int> VerifiedConnections = new HashSet<int>();

        private const float MOD_SEQUENCE_TIMEOUT = 2f;

        private void Update()
        {
            if (!NetworkServer.active) return;

            List<uint> playersToRemove = new List<uint>();
            foreach (var entry in FluffWatchlist)
            {
                if (Time.time - entry.Value.SparkleTimestamp > MOD_SEQUENCE_TIMEOUT)
                {
                    playersToRemove.Add(entry.Key);
                }
            }
            foreach (var netId in playersToRemove)
            {
                FluffWatchlist.Remove(netId);
            }
        }
		
		private void Awake()
		{
            Instance = this;
			Log = Logger;
            string pluginsPath = Directory.GetParent(Path.GetDirectoryName(Info.Location)).FullName;
            string targetLogDirectory = Path.Combine(pluginsPath, "HardAntiCheat");
            Directory.CreateDirectory(targetLogDirectory);
            InfractionLogPath = Path.Combine(targetLogDirectory, $"{ModInfo.NAME}_InfractionLog.txt");

			EnableAntiCheat = Config.Bind("1. General", "Enable AntiCheat", true, "Master switch to enable or disable all anti-cheat modules.");
			DisableForHost = Config.Bind("1. General", "Disable Detections for Host", true, "If true, the player hosting the server will not be checked for infractions.");
            
            EnableClientVerification = Config.Bind(
                "1. General", 
                "Enable Client Verification", 
                false, 
                "OPTIONAL & NOT RECOMMENDED: If true, kicks players who don't have HardAntiCheat installed. This can be spoofed by cheaters and may block friends. It is better to leave this false and rely on the strong server-side checks."
            );
            
            VerificationTimeout = Config.Bind("1. General", "Verification Timeout", 10.0f, "How many seconds the server will wait for a client to verify before kicking them (if Client Verification is enabled).");
			MaxLogFileSizeMB = Config.Bind("1. General", "Max Log File Size (MB)", 5, "If the infraction log exceeds this size, it will be archived on startup.");

            EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Speed Checks", true, "Checks incoming player movement packets for impossibly fast travel and blocks them.");
            MaxEffectiveSpeed = Config.Bind("2. Movement Detections", "Max Effective Speed", 100f, "The maximum plausible speed (units per second) a player can move. Increase this if lagging players get false flagged.");
            MovementGraceBuffer = Config.Bind("2. Movement Detections", "Movement Grace Buffer", 10.0f, "A flat distance buffer added to the calculation to account for dashes and small lag spikes.");
            TeleportDistanceThreshold = Config.Bind("2. Movement Detections", "Teleport Distance Threshold", 50f, "Any movement faster than plausible that also covers more than this distance is logged as a 'Teleport' instead of a 'Speed Hack'.");
			EnableAirborneChecks = Config.Bind("2. Movement Detections", "Enable Fly/Infinite Jump Checks", true, "Checks if players are airborne for an impossibly long time.");
			EnableSpeedChecks = Config.Bind("2. Movement Detections", "Enable Base Speed Stat Audits", true, "Continuously checks if a player's base movement speed stat has been illegally modified and reverts it.");
			JumpThreshold = Config.Bind("2. Movement Detections", "Jump threshold", 8, "The maximum number of jumps a player is allowed to perform before returning to the ground.");
            SpeedHackDetectionCooldown = Config.Bind("2. Movement Detections", "Speed Hack Detection Cooldown", 2.0f, "How long (in seconds) the anti-cheat will wait before logging another speed stat infraction for the same player.");
            JumpHackDetectionCooldown = Config.Bind("2. Movement Detections", "Jump Hack Detection Cooldown", 2.0f, "How long (in seconds) the anti-cheat will wait before logging another jump stat infraction for the same player.");
            AirborneHackDetectionCooldown = Config.Bind("2. Movement Detections", "Airborne Hack Detection Cooldown", 10.0f, "How long (in seconds) the anti-cheat will wait before logging another airborne infraction for the same player.");

			EnableExperienceChecks = Config.Bind("3. Stat Detections", "Enable Experience/Level Checks", true, "Prevents players from gaining huge amounts of XP or multiple levels at once.");
            MaxPlausibleXPGain = Config.Bind("3. Stat Detections", "Max Plausible XP Gain", 77000, "The maximum amount of XP a player can gain in a single transaction.");
            MaxPlausibleCurrencyGain = Config.Bind("3. Stat Detections", "Max Plausible Currency Gain", 50000, "The maximum amount of Currency a player can add via a direct command.");

			EnableCooldownChecks = Config.Bind("4. Combat Detections", "Enable Skill Cooldown Checks", true, "Silently enforces server-side cooldowns, blocking premature skill usage.");
			EnableReviveChecks = Config.Bind("4. Combat Detections", "Enable Self-Revive Checks", true, "Prevents players from reviving themselves while dead.");

			EnablePunishmentSystem = Config.Bind("5. Punishments", "Enable Punishment System", true, "If enabled, the server will automatically take action against players who accumulate too many infractions.");
			WarningsUntilAction = Config.Bind("5. Punishments", "Infractions Until Action", 5, "Number of infractions a player can commit before the selected action is taken.");
			ActionType = Config.Bind("5. Punishments", "Action Type", "Kick", new ConfigDescription("The action to take when a player reaches the infraction limit.", new AcceptableValueList<string>("Kick", "Ban")));
			
            EnableDetailedLogs = Config.Bind("6. Logging", "Enable Detailed Logs", true, "Master switch for detailed infraction logs.");
            LogPlayerName = Config.Bind("6. Logging", "Log Player Name", true, "Include the player's name in detailed logs.");
            LogPlayerID = Config.Bind("6. Logging", "Log Player ID", true, "Include the player's SteamID/netId in detailed logs.");
            LogInfractionDetails = Config.Bind("6. Logging", "Log Infraction Details", true, "Include the specific reason/details of the infraction in detailed logs.");
            LogInfractionCount = Config.Bind("6. Logging", "Log Infraction Count", true, "Include the player's current warning count in detailed logs.");
            
            CheckAndArchiveLogFile();
            RegisterPacketListeners();
			harmony.PatchAll();
			Log.LogInfo($"[{ModInfo.NAME}] has been loaded. Infractions will be logged to: {InfractionLogPath}");
		}

        #region Client Verification Handshake
        private void RegisterPacketListeners()
        {
            CodeTalkerNetwork.RegisterListener<AntiCheatHandshakeRequest>(ReceiveHandshakeRequest);
            CodeTalkerNetwork.RegisterListener<AntiCheatHandshakeResponse>(ReceiveHandshakeResponse);
        }

        private void ReceiveHandshakeRequest(PacketHeader header, PacketBase packet)
        {
            if (packet is AntiCheatHandshakeRequest)
            {
                Log.LogInfo("Received anti-cheat verification request from server. Responding...");
                CodeTalkerNetwork.SendNetworkPacket(new AntiCheatHandshakeResponse());
            }
        }

        private void ReceiveHandshakeResponse(PacketHeader header, PacketBase packet)
        {
            if (packet is AntiCheatHandshakeResponse)
            {
                ulong senderSteamId = header.SenderID;

                NetworkConnectionToClient targetConn = null;
                foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
                {
                    Player player = conn?.identity?.GetComponent<Player>();
                    if (player != null && player._steamID == senderSteamId.ToString())
                    {
                        targetConn = conn;
                        break;
                    }
                }

                if (targetConn == null) return;

                int senderConnectionId = targetConn.connectionId;
                if (PendingVerification.TryGetValue(senderConnectionId, out Coroutine kickCoroutine))
                {
                    StopCoroutine(kickCoroutine);
                    PendingVerification.Remove(senderConnectionId);
                    VerifiedConnections.Add(senderConnectionId);
                    Log.LogInfo($"Connection ID {senderConnectionId} (Steam ID: {senderSteamId}) has been successfully verified.");
                }
            }
        }

        public IEnumerator KickClientAfterDelay(NetworkConnectionToClient conn)
        {
            yield return new WaitForSeconds(VerificationTimeout.Value);
            if (conn != null && !VerifiedConnections.Contains(conn.connectionId))
            {
                Log.LogWarning($"Disconnecting connection ID {conn.connectionId} for failing to verify HardAntiCheat presence.");
                conn.Disconnect();
            }
            PendingVerification.Remove(conn.connectionId);
        }
        #endregion

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
                        string archivePath = Path.Combine(Path.GetDirectoryName(InfractionLogPath), $"{Path.GetFileNameWithoutExtension(InfractionLogPath)}_ARCHIVED_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                        File.Move(InfractionLogPath, archivePath);
                        Log.LogInfo($"Infraction log exceeded {MaxLogFileSizeMB.Value}MB and was archived to: {archivePath}");
                    }
                }
            }
            catch (Exception ex) { Log.LogError($"Error while checking/archiving log file: {ex.Message}"); }
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

			if (EnablePunishmentSystem.Value && !AtlyssNetworkManager._current._soloMode && currentInfractions >= maxInfractions)
			{
				if (NetworkServer.active && player != null && player.connectionToClient != null)
				{
                    ServerPunishmentCooldown[netId] = Time.time + 60f;
                    string action = ActionType.Value.ToLower();
                    string punishmentDetails = $"Player {playerName} (ID: {playerID}) was automatically {action.ToUpper()}ed for reaching {currentInfractions}/{maxInfractions} infractions.";
                    
                    Log.LogWarning(punishmentDetails);
                    try { File.AppendAllText(InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PUNISHMENT] " + punishmentDetails + Environment.NewLine); }
                    catch (Exception ex) { Log.LogError($"Failed to write punishment to log: {ex.Message}"); }
                    
                    if (HostConsole._current != null) HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails);

                    if (action == "kick")
                    {
                        player.connectionToClient.Disconnect();
                    } 
                    else // Ban
                    {
                        HC_PeerListEntry targetPeer = null;
                        if (HostConsole._current != null)
                        {
                            foreach(var entry in HostConsole._current._peerListEntries)
                            {
                                if (entry._netId != null && entry._netId.netId == netId) { targetPeer = entry; break; }
                            }
                        }
                        
                        if (targetPeer != null)
                        {
                            HostConsole._current._selectedPeerEntry = targetPeer;
                            HostConsole._current.Ban_Peer();
                        }
                        else
                        {
                            player.connectionToClient.Disconnect();
                            Log.LogError($"Could not find PeerListEntry for player {playerName} (netId: {netId}) to BAN, kicked instead.");
                        }
                    }
                    ServerPlayerInfractionCount.Remove(netId);
				}
                return;
			}
            
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
            else { return; }

			Log.LogWarning(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }
		}

        public static void LogDetection(NetworkBehaviour instance, string eventType, string details)
        {
            uint netId = instance.netId;
			Player player = instance.GetComponent<Player>();
			string playerName = player?._nickname ?? "Unknown";
			string playerID = player?._steamID ?? $"netId:{netId}";
            int currentInfractions = ServerPlayerInfractionCount.ContainsKey(netId) ? ServerPlayerInfractionCount[netId] : 0;
            int maxInfractions = WarningsUntilAction.Value;

            string logMessage;
            if (EnableDetailedLogs.Value)
            {
                var sb = new StringBuilder();
                sb.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [DETECTION]");
                var segments = new List<string>();
                if (LogPlayerName.Value) segments.Add($"Player: {playerName}");
                if (LogPlayerID.Value) segments.Add($"ID: {playerID}");
                if (segments.Any()) sb.Append(" " + string.Join(" | ", segments));
                sb.Append($" | Type: {eventType}");
                if (LogInfractionDetails.Value) sb.Append($" | Details: {details}");
                if (LogInfractionCount.Value) sb.Append($" | Current Warnings: {currentInfractions}/{maxInfractions}");
                logMessage = sb.ToString();
            }
            else { return; }

			Log.LogInfo(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }
        }

        public static void ClearAllPlayerData(uint netId, int connectionId)
        {
            ServerRemainingCooldowns.Remove(netId);
            ServerPlayerPositions.Remove(netId);
            ServerPlayerStats.Remove(netId);
            ServerPlayerAirborneStates.Remove(netId);
            ServerPlayerInitialSpeeds.Remove(netId);
            ServerPlayerInfractionCount.Remove(netId);
            ServerPlayerGracePeriod.Remove(netId);
            ServerPunishmentCooldown.Remove(netId);
            ServerSpeedCheckCooldowns.Remove(netId);
            ServerJumpCheckCooldowns.Remove(netId);
            ServerAirborneCheckCooldowns.Remove(netId);
            FluffWatchlist.Remove(netId);

            if (PendingVerification.TryGetValue(connectionId, out Coroutine kickCoroutine))
            {
                if (Instance != null) Instance.StopCoroutine(kickCoroutine);
                PendingVerification.Remove(connectionId);
            }
            VerifiedConnections.Remove(connectionId);
        }
	}
    
    #region Player Connection & Initialization
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
                Main.Log.LogInfo("First player has spawned. HardAntiCheat server-side modules are now active.");
                hasServerInitialized = true;
            }

            uint netId = __instance.netId;
            Main.ServerPlayerGracePeriod[netId] = Time.time + GRACE_PERIOD_SECONDS;
            if (!Main.ServerPlayerInitialSpeeds.ContainsKey(netId))
            {
                Main.ServerPlayerInitialSpeeds[netId] = __instance.Network_movSpeed;
            }
            Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = __instance.transform.position, Timestamp = Time.time };
		}
	}
    #endregion
	
    #region UI Button Watcher
    [HarmonyPatch(typeof(UnityEngine.UI.Button), "Press")]
    public static class ButtonPressValidationPatch
    {
        private static DeathPromptManager _deathPromptManagerInstance;
        [HarmonyPrefix]
        public static bool OnButtonPress(UnityEngine.UI.Button __instance)
        {
            if (_deathPromptManagerInstance == null)
            {
                _deathPromptManagerInstance = UnityEngine.Object.FindObjectOfType<DeathPromptManager>();
                if (_deathPromptManagerInstance == null) return true;
            }
            Button tearButton = Traverse.Create(_deathPromptManagerInstance).Field<Button>("_useTearButton").Value;
            if (tearButton != null && __instance == tearButton)
            {
                Player mainPlayer = Player._mainPlayer;
                if (mainPlayer != null)
                {
                    PlayerInventory inventory = mainPlayer.GetComponent<PlayerInventory>();
                    if (inventory != null)
                    {
                        foreach (ItemData item in inventory._heldItems)
                        {
                            if (item != null && item._itemName == "Angela's Tear")
                            {
                                inventory.Cmd_UseConsumable(item);
                                return true;
                            }
                        }
                    }
                }
            }
            return true;
        }
    }
    #endregion
	
	#region Item Usage Validation
	[HarmonyPatch(typeof(PlayerInventory), "UserCode_Cmd_UseConsumable__ItemData")]
	public static class ItemUsageValidationPatch
	{
		[HarmonyPrefix]
		public static void GrantTokenOnTearUsage(PlayerInventory __instance, ItemData _itemData)
		{
			if (NetworkServer.active && _itemData != null && _itemData._itemName == "Angela's Tear")
			{
				Main.AuthorizedSelfRevives[__instance.netId] = Time.time;
			}
		}
	}
    #endregion
	
    #region Server Authority Protection
	[HarmonyPatch(typeof(StatusEntity), "Cmd_RevivePlayer")]
	public static class ServerAuthorityValidationPatch
	{
		[HarmonyPrefix]
		public static bool ValidateRevive(StatusEntity __instance, Player _p)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (Main.DisableForHost.Value && _p._isHostPlayer) return true;

			if (__instance.netId == _p.netId)
			{
				Main.LogInfraction(__instance, "Unauthorized Action (Direct Self-Revive)", "Blocked direct call to self-revive.");
				return false;
			}
			return true;
		}
    }

    [HarmonyPatch(typeof(StatusEntity), "Cmd_ReplenishAll")]
    public static class ReplenishWhileDeadPatch
    {
        [HarmonyPrefix]
        public static bool ValidateReplenish(StatusEntity __instance)
        {
            Player player = __instance.GetComponent<Player>();
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
            if (Main.DisableForHost.Value && player._isHostPlayer) return true;

            if (__instance.Network_currentHealth <= 0)
            {
                uint netId = player.netId;
                if (Main.AuthorizedSelfRevives.TryGetValue(netId, out float timestamp))
                {
                    if (Time.time - timestamp < 1.5f)
                    {
                        Main.AuthorizedSelfRevives.Remove(netId);
                        return true;
                    }
                }
                Main.LogInfraction(__instance, "Unauthorized Action (Replenish while Dead)", "Blocked replenish call - was not authorized by UI button press.");
                return false;
            }
            return true;
        }
    }
    #endregion
	
	#region Blatent Honeypots
	
	#region Teleportation
    [HarmonyPatch(typeof(NetworkTransformBase), nameof(NetworkTransformBase.CmdTeleport), new Type[] { typeof(Vector3), typeof(Quaternion) })]
    public static class CmdTeleportValidationPatch2
    {
    	public static bool Prefix(NetworkBehaviour __instance)
    	{
    		if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableMovementChecks.Value) return true;
    		Player player = __instance.GetComponent<Player>();
    		if (Main.DisableForHost.Value && player._isHostPlayer) { return true; }
    		Main.LogInfraction(__instance, "Movement Hack (Illegal Teleport Command)", "Player directly called a Teleport command.");
    		return false;
    	}
    }

    [HarmonyPatch(typeof(NetworkTransformBase), nameof(NetworkTransformBase.CmdTeleport), new Type[] { typeof(Vector3) })]
    public static class CmdTeleportValidationPatch
    {
    	public static bool Prefix(NetworkBehaviour __instance)
    	{
    		if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableMovementChecks.Value) return true;
    		Player player = __instance.GetComponent<Player>();
    		if (Main.DisableForHost.Value && player._isHostPlayer) { return true; }
    		Main.LogInfraction(__instance, "Movement Hack (Illegal Teleport Command)", "Player directly called a Teleport command.");
    		return false;
    	}
    }
    #endregion

    [HarmonyPatch]
    public static class CurrencyDetectionPatch
    {
        [HarmonyPatch(typeof(PlayerInventory), "UserCode_Cmd_AddCurrency__Int32")]
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static bool OnAddCurrency(PlayerInventory __instance, int _value)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value) return true;
            Player player = __instance.GetComponent<Player>();
            if (Main.DisableForHost.Value && player._isHostPlayer) return true;
            if (_value > Main.MaxPlausibleCurrencyGain.Value)
            {
                Main.LogInfraction(player, "Money Abuse", $"Attempted to add impossible currency amount: {_value}. Blocked.");
                return false; 
            }
            return true;
        }
    }
    #endregion

	#region Movement & Airborne Protection
    [HarmonyPatch(typeof(NetworkTransformUnreliable), "OnClientToServerSync")]
    public static class NetworkTransformSyncPatch
    {
        [HarmonyPrefix]
        public static bool ValidateMovement(NetworkTransformUnreliable __instance, Vector3? position, Quaternion? rotation)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableMovementChecks.Value || !position.HasValue) return true;

            uint netId = __instance.netId;
            Player player = __instance.GetComponent<Player>();

            if (Main.ServerPlayerGracePeriod.ContainsKey(netId)) return true;

            if (Main.ServerPlayerPositions.TryGetValue(netId, out PlayerPositionData lastPositionData))
            {
                float timeElapsed = Time.time - lastPositionData.Timestamp;
                if (timeElapsed < 0.05f) return true;

                float distanceTraveled = Vector3.Distance(lastPositionData.Position, position.Value);
                float maxPossibleDistance = (Main.MaxEffectiveSpeed.Value * timeElapsed) + Main.MovementGraceBuffer.Value;

                if (distanceTraveled > maxPossibleDistance)
                {
					if (Main.DisableForHost.Value) { if (player._isHostPlayer) return true; }
								
                    string cheatType = distanceTraveled > Main.TeleportDistanceThreshold.Value ? "Movement Hack (Teleport)" : "Movement Hack (Speed)";
                    string details = $"Moved {distanceTraveled:F1} units in {timeElapsed:F2}s. Packet blocked.";
                    Main.LogInfraction(__instance, cheatType, details);
                    return false;
                }
            }
            Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = position.Value, Timestamp = Time.time };
            return true;
        }
    }

	[HarmonyPatch(typeof(PlayerMove), "Init_Jump")]
	public static class JumpValidationPatch
	{
		public static bool Prefix(PlayerMove __instance)
		{
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableAirborneChecks.Value) return true;
			if (Main.DisableForHost.Value && player._isHostPlayer) return true;
            
			uint netId = __instance.netId;
			if (!Main.ServerPlayerAirborneStates.ContainsKey(netId))
				Main.ServerPlayerAirborneStates[netId] = new PlayerAirborneData();
			PlayerAirborneData airData = Main.ServerPlayerAirborneStates[netId];
			if(airData.ServerSideJumpCount >= Main.JumpThreshold.Value) { return false; }
			airData.ServerSideJumpCount++;
			Main.ServerPlayerAirborneStates[netId] = airData;
			return true;
		}
	}
	
	[HarmonyPatch(typeof(PlayerMove), "Update")]
    public static class ContinuousValidationPatch
    {
        private const float MAX_ALLOWED_AIR_TIME = 10.0f;
        private const float VERTICAL_STALL_TOLERANCE = 0.05f;
        private const float VERTICAL_STALL_GRACE_PERIOD = 0.5f;
        private const float MAX_FLIGHT_HEIGHT = 4240f;

        public static void Postfix(PlayerMove __instance)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;
            
            Player player = __instance.GetComponent<Player>();
            uint netId = __instance.netId;
            
            if (Main.ServerPlayerGracePeriod.TryGetValue(netId, out float gracePeriodEndTime))
            {
                if (Time.time < gracePeriodEndTime) { return; } else { Main.ServerPlayerGracePeriod.Remove(netId); }
            }

            if (Main.EnableSpeedChecks.Value)
            {
                if (!Main.ServerSpeedCheckCooldowns.ContainsKey(netId) || Time.time > Main.ServerSpeedCheckCooldowns[netId])
                {
                    if (Main.ServerPlayerInitialSpeeds.TryGetValue(netId, out float initialSpeed))
                    {
                        if (__instance._movSpeed > Main.MaxEffectiveSpeed.Value || __instance._movSpeed < 20)
                        {
	                        if (!player._isHostPlayer || !Main.DisableForHost.Value)
	                        {
                                Main.LogInfraction(__instance, "Stat Manipulation (Move Speed)", $"Illegal move speed of {__instance._movSpeed}. Reverting.");
                                __instance.Reset_MoveSpeed();
                                Main.ServerSpeedCheckCooldowns[netId] = Time.time + Main.SpeedHackDetectionCooldown.Value;
                            }
                        }
                    }
                }
            }

            if (Main.EnableAirborneChecks.Value)
            {
                Vector3 currentPosition = __instance.transform.position;
                PlayerAirborneData airData = Main.ServerPlayerAirborneStates.ContainsKey(netId) ? Main.ServerPlayerAirborneStates[netId] : new PlayerAirborneData();

                if (!Main.ServerJumpCheckCooldowns.ContainsKey(netId) || Time.time > Main.ServerJumpCheckCooldowns[netId])
                {
                    if (__instance._maxJumps >= Main.JumpThreshold.Value)
                    {
	                    if (!player._isHostPlayer || !Main.DisableForHost.Value)
	                    {
		                    Main.LogInfraction(__instance, "Stat Manipulation (Max Jumps)", $"Client reported _maxJumps of {__instance._maxJumps}. Reverting.");
		                    __instance._maxJumps = 2;
		                    Main.ServerJumpCheckCooldowns[netId] = Time.time + Main.JumpHackDetectionCooldown.Value;
	                    }
                    }
                }

                if (__instance.RayGroundCheck())
                {
                    airData.AirTime = 0f;
                    airData.ServerSideJumpCount = 0;
                    airData.LastGroundedPosition = currentPosition;
                    airData.VerticalStallTime = 0f;
                }
                else
                {
                    airData.AirTime += Time.deltaTime;
                    if (Mathf.Abs(currentPosition.y - airData.LastVerticalPosition) < VERTICAL_STALL_TOLERANCE) airData.VerticalStallTime += Time.deltaTime;
                    else airData.VerticalStallTime = 0f;
                }
                airData.LastVerticalPosition = currentPosition.y;
                
                bool isExemptHost = Main.DisableForHost.Value && player._isHostPlayer;
                if (currentPosition.y > MAX_FLIGHT_HEIGHT && !isExemptHost)
                {
                    Main.LogInfraction(__instance, "Movement Hack (Fly)", $"Exceeded max height. Reverting.");
                    __instance.transform.position = airData.LastGroundedPosition;
                    return;
                }
                
                if (airData.AirTime > MAX_ALLOWED_AIR_TIME && airData.VerticalStallTime < VERTICAL_STALL_GRACE_PERIOD)
                {
	                if (!Main.ServerAirborneCheckCooldowns.ContainsKey(netId) || Time.time > Main.ServerAirborneCheckCooldowns[netId])
	                {
		                if (!isExemptHost) 
		                {
			                Main.LogInfraction(__instance, "Movement Hack (Fly)", $"Airborne for {airData.AirTime:F1}s. Reverting.");
			                Main.ServerAirborneCheckCooldowns[netId] = Time.time + Main.AirborneHackDetectionCooldown.Value;
		                }
	                }
                    __instance.transform.position = airData.LastGroundedPosition;
                    airData.AirTime = 0f;
                }
                Main.ServerPlayerAirborneStates[netId] = airData;
            }
        }
    }
	#endregion
    
    #region Stat Manipulation Protection
	[HarmonyPatch]
	public static class ExperienceValidationPatch
	{
		[HarmonyPatch(typeof(PlayerStats), "set_Network_currentExp")]
		[HarmonyPrefix]
		public static bool ValidateExperienceChange(PlayerStats __instance, ref int value)
		{
			Player player = __instance.GetComponent<Player>();
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
				if (Main.DisableForHost.Value && player._isHostPlayer) return true;
				Main.LogInfraction(__instance, "Stat Manipulation (Experience)", $"Attempted to gain {experienceGain} XP. Blocked.");
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
			Player player = __instance.GetComponent<Player>();
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
				if (Main.DisableForHost.Value && player._isHostPlayer) { return true; }
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

    #region Skill Cooldown Protection
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
                    if (newRemainingTime <= 0) skillsToRemove.Add(skillName);
                    else playerSkills[skillName] = newRemainingTime;
                }
                foreach (var skillName in skillsToRemove) playerSkills.Remove(skillName);
            }
        }
        
        [HarmonyPatch(typeof(PlayerCasting), "Server_CastSkill")]
		[HarmonyPrefix]
		public static bool UnifiedCooldownValidation(PlayerCasting __instance)
		{
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCooldownChecks.Value) return true;

            ScriptableSkill skillToCast = __instance._currentCastSkill;
            if (skillToCast == null) return true; 

			uint netId = __instance.netId;
			
			if (Main.ServerRemainingCooldowns.TryGetValue(netId, out var playerSkills) && playerSkills.ContainsKey(skillToCast.name))
			{
				if (Main.DisableForHost.Value && player._isHostPlayer) { return true; }
                __instance.Server_InterruptCast();
                Main.LogInfraction(__instance, "Skill Cooldown Bypass", $"Attempted to cast {skillToCast.name} while on cooldown.");
				return false; 
			}

            if (!Main.ServerRemainingCooldowns.ContainsKey(netId)) Main.ServerRemainingCooldowns[netId] = new Dictionary<string, float>();
			Main.ServerRemainingCooldowns[netId][skillToCast.name] = skillToCast._skillRankParams._baseCooldown;
			return true;
		}
	}
    #endregion

    #region Network Connection Management
    [HarmonyPatch(typeof(AtlyssNetworkManager), "OnStartServer")]
    public static class ServerStartPatch
    {
        public static void Postfix()
        {
            if (Main.EnableClientVerification.Value)
            {
                // The host is connection ID 0. We automatically verify them so they can't be kicked.
                NetworkConnectionToClient localConn = NetworkServer.localConnection;
                if (localConn != null)
                {
                    Main.VerifiedConnections.Add(localConn.connectionId);
                    Main.Log.LogInfo($"Host connection (ID: {localConn.connectionId}) has been automatically verified.");
                }
            }
        }
    }

	[HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerConnect")]
	public static class ServerConnectPatch
	{
		public static void Postfix(NetworkConnectionToClient _conn)
		{
            if (!Main.EnableClientVerification.Value) return;
            // The host is already auto-verified by the ServerStartPatch, but this prevents a broadcast for the host connection.
            if (_conn.connectionId == 0 || _conn is NetworkConnectionToServer) return;

            Main.Log.LogInfo($"New connection from ID: {_conn.connectionId}. Broadcasting verification request...");
            CodeTalkerNetwork.SendNetworkPacket(new AntiCheatHandshakeRequest());
            var kickCoroutine = Main.Instance.StartCoroutine(Main.Instance.KickClientAfterDelay(_conn));
            Main.PendingVerification.Add(_conn.connectionId, kickCoroutine);
		}
	}

	[HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerDisconnect")]
	public static class PlayerDisconnectPatch
	{
		public static void Postfix(NetworkConnectionToClient _conn)
		{
			if (_conn?.identity != null)
			{
				uint netId = _conn.identity.netId;
                Main.ClearAllPlayerData(netId, _conn.connectionId);
                Main.Log.LogInfo($"Player with netId {netId} disconnected. Cleared tracking data.");
			}
            else
            {
                Main.ClearAllPlayerData(0, _conn.connectionId);
                Main.Log.LogInfo($"Connection ID {_conn.connectionId} disconnected before authenticating. Cleared verification data.");
            }
		}
	}
    #endregion
}