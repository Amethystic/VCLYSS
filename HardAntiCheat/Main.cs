using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeTalker.Networking;
using CodeTalker.Packets;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace HardAntiCheat
{
    #region Custom Network Packets (CodeYapper) - PATCHED
    public class HAC_HandshakeRequest : PacketBase
    {
        public override string PacketSourceGUID => "HardAntiCheat";
        [JsonProperty] public ulong TargetSteamID;
        [JsonProperty] public string ChallengeToken;
        // PATCH: Added ChallengeType to instruct the client on what kind of hash to generate.
        // This makes the system extensible for future integrity checks. 0 = standard DLL hash.
        [JsonProperty] public int ChallengeType;
    }

    public class HAC_HandshakeResponse : PacketBase
    {
        public override string PacketSourceGUID => "HardAntiCheat";
        // PATCH: Changed from sending the token back to sending a computed hash.
        [JsonProperty] public string ChallengeHash;
        // NEW: Added the client's loaded mod list to the handshake for server-side validation.
        [JsonProperty] public List<string> ModList;
    }
    #endregion

    #region Data Structures
    public struct PlayerPositionData { public Vector3 Position; public float Timestamp; }
	public struct PlayerStatsData { public int Level; public int Experience; }
	public struct PlayerAirborneData { public float AirTime; public Vector3 LastGroundedPosition; public int ServerSideJumpCount; public float LastVerticalPosition; public float VerticalStallTime; }
    #endregion

    #region Integrity Hashing Logic - NEW
    // NEW: This static class handles the client-side and server-side logic for generating and verifying code integrity hashes.
    public static class HACIntegrity
    {
        private static byte[] _selfHashCache;

        // Computes a hash of this assembly's file bytes.
        private static byte[] GetSelfHash()
        {
            if (_selfHashCache != null) return _selfHashCache;
            
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(Assembly.GetExecutingAssembly().Location))
                {
                    _selfHashCache = sha256.ComputeHash(stream);
                    return _selfHashCache;
                }
            }
        }

        // Client-side method to generate the hash response.
        public static string GenerateClientHash(string token, int type)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;

            try
            {
                // This is where different challenge types would be handled. For now, we only have one.
                switch (type)
                {
                    case 0:
                    default:
                        byte[] selfHash = GetSelfHash();
                        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
                        byte[] combined = new byte[selfHash.Length + tokenBytes.Length];
                        Buffer.BlockCopy(selfHash, 0, combined, 0, selfHash.Length);
                        Buffer.BlockCopy(tokenBytes, 0, combined, selfHash.Length, tokenBytes.Length);

                        using (var sha256 = SHA256.Create())
                        {
                            return Convert.ToBase64String(sha256.ComputeHash(combined));
                        }
                }
            }
            catch
            {
                // If hashing fails for any reason, return an invalid hash.
                return "HASH_GENERATION_FAILED";
            }
        }
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
        public static ConfigEntry<string> TrustedSteamIDs;
        // PATCH: Changed description to reflect new hashing mechanism.
        public static ConfigEntry<bool> EnableIntegrityChecks;
        public static ConfigEntry<bool> HACEnforce;
        public static ConfigEntry<string> HACListType;
        public static ConfigEntry<string> HACHandshakeFailAction;
        public static ConfigEntry<string> HACModList;
        public static ConfigEntry<float> VerificationTimeout;
        public static ConfigEntry<int> MaxLogFileSizeMB;
        public static ConfigEntry<bool> EnableMovementChecks;
		public static ConfigEntry<float> MaxEffectiveSpeed;
        public static ConfigEntry<float> MovementGraceBuffer;
        public static ConfigEntry<float> MovementTimeThreshold;
        public static ConfigEntry<float> TeleportDistanceThreshold;
		public static ConfigEntry<bool> EnableAirborneChecks;
		public static ConfigEntry<bool> EnableSpeedChecks;
        public static ConfigEntry<float> SpeedHackDetectionCooldown;
        public static ConfigEntry<float> JumpHackDetectionCooldown;
        public static ConfigEntry<float> AirborneHackDetectionCooldown;
		public static ConfigEntry<bool> EnableExperienceChecks;
		public static ConfigEntry<int> JumpThreshold;
        public static ConfigEntry<int> MaxPlausibleXPGain;
        public static ConfigEntry<int> MaxXPGainPerWindow;
        public static ConfigEntry<float> XPGainWindowSeconds;
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
        // NEW: Configuration for specific mod detections.
        public static ConfigEntry<bool> EnableFluffUtilitiesCheck;
        public static ConfigEntry<string> FluffUtilitiesAction;

		// --- WHITELIST & MOD LISTS ---
        private static readonly HashSet<ulong> WhitelistedUsers = new HashSet<ulong>();
        private static readonly HashSet<string> ManagedMods = new HashSet<string>();
        
		// --- SERVER-SIDE DATA DICTIONARIES ---
		public static readonly Dictionary<uint, Dictionary<string, float>> ServerRemainingCooldowns = new Dictionary<uint, Dictionary<string, float>>();
        public static readonly Dictionary<uint, PlayerPositionData> ServerPlayerPositions = new Dictionary<uint, PlayerPositionData>();
		public static readonly Dictionary<uint, PlayerStatsData> ServerPlayerStats = new Dictionary<uint, PlayerStatsData>();
        public static readonly Dictionary<uint, List<(float Timestamp, int Amount)>> XpGainHistory = new Dictionary<uint, List<(float, int)>>();
		public static readonly Dictionary<uint, PlayerAirborneData> ServerPlayerAirborneStates = new Dictionary<uint, PlayerAirborneData>();
        public static readonly Dictionary<uint, float> ServerPlayerInitialSpeeds = new Dictionary<uint, float>();
		public static readonly Dictionary<uint, int> ServerPlayerInfractionCount = new Dictionary<uint, int>();
		public static readonly Dictionary<uint, float> ServerPlayerGracePeriod = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerPunishmentCooldown = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerSpeedCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerJumpCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> ServerAirborneCheckCooldowns = new Dictionary<uint, float>();
        public static readonly Dictionary<uint, float> AuthorizedSelfRevives = new Dictionary<uint, float>();
        
        // --- CLIENT VERIFICATION DICTIONARIES ---
        internal static readonly Dictionary<ulong, (string ExpectedHash, Coroutine KickCoroutine)> PendingVerification = new Dictionary<ulong, (string, Coroutine)>();
        internal static readonly HashSet<ulong> VerifiedSteamIDs = new HashSet<ulong>();
		
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
            TrustedSteamIDs = Config.Bind("1. General", "Trusted SteamIDs", "", "A comma-separated list of 64-bit SteamIDs for users who should be exempt from all anti-cheat checks.");
			MaxLogFileSizeMB = Config.Bind("1. General", "Max Log File Size (MB)", 5, "If the infraction log exceeds this size, it will be archived on startup.");
			
			EnableIntegrityChecks = Config.Bind("1. General", "Enable Client Integrity Checks", true, "If true, kicks players who fail a cryptographic challenge to prove they have the unmodified mod installed.");
			VerificationTimeout = Config.Bind("1. General", "Verification Timeout", 25.0f, "How many seconds the server will wait for a client to verify before kicking them.");
            
            HACEnforce = Config.Bind("1. General", "Enable Mod List Enforcement", false, "If true, the server will check connecting clients' mod lists against the rules below.");
            HACListType = Config.Bind("1. General", "Mod List Type", "blacklist", new ConfigDescription("Determines how the mod list is used.", new AcceptableValueList<string>("blacklist", "whitelist")));
            HACHandshakeFailAction = Config.Bind("1. General", "Handshake Fail Action", "kick", new ConfigDescription("The action to take if a client fails the integrity check or mod list check.", new AcceptableValueList<string>("kick", "ban")));
            HACModList = Config.Bind("1. General", "Mod List (GUIDs)", "some.banned.mod.guid,another.bad.mod", "Comma-separated list of BepInEx plugin GUIDs to use for the blacklist/whitelist.");

            EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Distance Checks", true, "Checks the final result of player movement to catch physics-based speed hacks and teleports.");
            MaxEffectiveSpeed = Config.Bind("2. Movement Detections", "Max Effective Speed", 100f, "The maximum plausible speed (units per second) a player can move.");
            MovementGraceBuffer = Config.Bind("2. Movement Detections", "Movement Grace Buffer", 10.0f, "A flat distance buffer added to the calculation to account for dashes and small lag spikes.");
            MovementTimeThreshold = Config.Bind("2. Movement Detections", "Movement Time Threshold", 5.5f, "The time (in seconds) between position checks. Higher values are more lenient on lag but less precise.");
            TeleportDistanceThreshold = Config.Bind("2. Movement Detections", "Teleport Distance Threshold", 50f, "Any movement faster than plausible that also covers more than this distance is logged as a 'Teleport' instead of a 'Speed Hack'.");
			EnableAirborneChecks = Config.Bind("2. Movement Detections", "Enable Fly/Infinite Jump Checks", true, "Checks if players are airborne for an impossibly long time and have an invalid number of max jumps.");
			EnableSpeedChecks = Config.Bind("2. Movement Detections", "Enable Base Speed Stat Audits", true, "Continuously checks if a player's base movement speed stat has been illegally modified and reverts it.");
			JumpThreshold = Config.Bind("2. Movement Detections", "Jump threshold", 8, "The maximum number of jumps a player is allowed to perform before returning to the ground.");
            SpeedHackDetectionCooldown = Config.Bind("2. Movement Detections", "Speed Hack Detection Cooldown", 2.0f, "How long (in seconds) the anti-cheat will wait before logging another speed stat infraction for the same player.");
            JumpHackDetectionCooldown = Config.Bind("2. Movement Detections", "Jump Hack Detection Cooldown", 2.0f, "How long (in seconds) the anti-cheat will wait before logging another jump stat infraction for the same player.");
            AirborneHackDetectionCooldown = Config.Bind("2. Movement Detections", "Airborne Hack Detection Cooldown", 10.0f, "How long (in seconds) the anti-cheat will wait before logging another airborne infraction for the same player.");

			EnableExperienceChecks = Config.Bind("3. Stat Detections", "Enable Experience/Level Checks", true, "Prevents players from gaining huge amounts of XP or multiple levels at once.");
            MaxPlausibleXPGain = Config.Bind("3. Stat Detections", "Max Plausible XP Gain (Single Transaction)", 77000, "The maximum amount of XP a player can gain in a single transaction.");
            MaxXPGainPerWindow = Config.Bind("3. Stat Detections", "Max XP Gain Rate", 150000, "The maximum amount of XP a player can gain within the time window specified below.");
            XPGainWindowSeconds = Config.Bind("3. Stat Detections", "XP Gain Time Window (Seconds)", 30f, "The time window for the XP gain rate limit.");

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
            
            // NEW: Bindings for the FluffUtilities check.
            EnableFluffUtilitiesCheck = Config.Bind("7. Specific Mod Detections", "Enable FluffUtilities Check", true, "Enables a specific check for the FluffUtilities mod using Steam lobby data.");
            FluffUtilitiesAction = Config.Bind("7. Specific Mod Detections", "FluffUtilities Detected Action", "Log", new ConfigDescription("Action to take when FluffUtilities is detected.", new AcceptableValueList<string>("Log", "Kick", "Ban")));
            
            CheckAndArchiveLogFile();
            ParseWhitelist();
            ParseHACModList();
            TrustedSteamIDs.SettingChanged += (s, e) => ParseWhitelist();
            HACModList.SettingChanged += (s, e) => ParseHACModList();

            CodeTalkerNetwork.RegisterListener<HAC_HandshakeRequest>(OnClientReceivedHandshakeRequest);
            CodeTalkerNetwork.RegisterListener<HAC_HandshakeResponse>(OnServerReceivedHandshakeResponse);
            
			harmony.PatchAll();
			Log.LogInfo($"[{ModInfo.NAME}] has been loaded. Infractions will be logged to: {InfractionLogPath}");
		}
        
        public static void OnClientReceivedHandshakeRequest(PacketHeader header, PacketBase packet)
        {
            if (packet is HAC_HandshakeRequest request)
            {
                if (ulong.TryParse(Player._mainPlayer?._steamID, out ulong mySteamId) && request.TargetSteamID == mySteamId)
                {
                    Log.LogInfo("Received verification request from server. Computing integrity hash and gathering mod list...");
                    string clientHash = HACIntegrity.GenerateClientHash(request.ChallengeToken, request.ChallengeType);
                    var modList = Chainloader.PluginInfos.Keys.ToList();
                    CodeTalkerNetwork.SendNetworkPacket(new HAC_HandshakeResponse { ChallengeHash = clientHash, ModList = modList });
                }
            }
        }

        public static void OnServerReceivedHandshakeResponse(PacketHeader header, PacketBase packet)
        {
            if (packet is HAC_HandshakeResponse response)
            {
                ulong senderSteamId = header.SenderID;
                if (PendingVerification.TryGetValue(senderSteamId, out var verificationData))
                {
                    if (HACEnforce.Value)
                    {
                        var clientModList = response.ModList ?? new List<string>();
                        string validationMode = HACListType.Value.ToLower();
                        bool failed = false;
                        string reason = "";

                        if (validationMode == "blacklist")
                        {
                            string bannedMod = clientModList.FirstOrDefault(guid => ManagedMods.Contains(guid));
                            if (bannedMod != null)
                            {
                                failed = true;
                                reason = $"Handshake failed: Client has a banned mod ({bannedMod}).";
                            }
                        }
                        else // Whitelist mode
                        {
                            string unlistedMod = clientModList.FirstOrDefault(guid => guid != ModInfo.GUID && !ManagedMods.Contains(guid));
                            if (unlistedMod != null)
                            {
                                failed = true;
                                reason = $"Handshake failed: Client has a non-whitelisted mod ({unlistedMod}).";
                            }
                        }

                        if (failed)
                        {
                            Log.LogWarning(reason);
                            PunishHandshakeFailure(senderSteamId, reason);
                            return;
                        }
                    }

                    if (verificationData.ExpectedHash == response.ChallengeHash)
                    {
                        Instance.StopCoroutine(verificationData.KickCoroutine);
                        PendingVerification.Remove(senderSteamId);
                        VerifiedSteamIDs.Add(senderSteamId);
                        Log.LogInfo($"SteamID {senderSteamId} has been successfully verified (Integrity and Mod List checks passed).");
                    }
                    else
                    {
                        string reason = "Failed integrity check. Expected hash did not match received hash.";
                        Log.LogWarning($"SteamID {senderSteamId} " + reason);
                        PunishHandshakeFailure(senderSteamId, reason);
                    }
                }
            }
        }

        public IEnumerator KickClientAfterDelay(Player player)
        {
            if (player == null || !ulong.TryParse(player._steamID, out ulong steamId)) yield break;
            
            yield return new WaitForSeconds(VerificationTimeout.Value);

            if (player != null && !VerifiedSteamIDs.Contains(steamId))
            {
                string reason = $"Disconnecting player {player._nickname} (SteamID: {steamId}) for failing to respond to client integrity check.";
                Log.LogWarning(reason);
                PunishHandshakeFailure(steamId, reason);
            }
            PendingVerification.Remove(steamId);
        }

        private static void ParseWhitelist()
        {
            WhitelistedUsers.Clear();
            string idString = TrustedSteamIDs.Value;
            if (string.IsNullOrWhiteSpace(idString))
            {
                Log.LogInfo("Whitelist is empty. Only the host may be exempt from checks.");
                return;
            }

            string[] ids = idString.Split(',');
            int count = 0;
            foreach (string id in ids)
            {
                if (ulong.TryParse(id.Trim(), out ulong steamId))
                {
                    WhitelistedUsers.Add(steamId);
                    count++;
                }
                else
                {
                    Log.LogWarning($"Could not parse '{id.Trim()}' as a valid SteamID. Please ensure it is a 64-bit numerical ID.");
                }
            }
            Log.LogInfo($"Successfully loaded {count} user(s) into the anti-cheat whitelist.");
        }

        private static void ParseHACModList()
        {
            ManagedMods.Clear();
            string guidString = HACModList.Value;
            if (string.IsNullOrWhiteSpace(guidString))
            {
                Log.LogInfo("HAC Mod List is empty.");
                return;
            }

            string[] guids = guidString.Split(',');
            int count = 0;
            foreach (string guid in guids)
            {
                string trimmedGuid = guid.Trim();
                if (!string.IsNullOrEmpty(trimmedGuid))
                {
                    ManagedMods.Add(trimmedGuid);
                    count++;
                }
            }
            Log.LogInfo($"Successfully loaded {count} GUID(s) into the HAC Mod List for {HACListType.Value} enforcement.");
        }
        
        private static void PunishHandshakeFailure(ulong steamId, string reason)
        {
            if (PendingVerification.TryGetValue(steamId, out var verificationData))
            {
                if(Instance != null) Instance.StopCoroutine(verificationData.KickCoroutine);
                PendingVerification.Remove(steamId);
            }

            Player playerToPunish = null;
            if (AtlyssNetworkManager._current != null)
            {
                foreach (var p in FindObjectsOfType<Player>())
                {
                    if (ulong.TryParse(p._steamID, out ulong id) && id == steamId)
                    {
                        playerToPunish = p;
                        break;
                    }
                }
            }

            if (playerToPunish == null || playerToPunish.connectionToClient == null)
            {
                Log.LogError($"Could not find player with SteamID {steamId} to enforce handshake punishment.");
                return;
            }
            
            string action = HACHandshakeFailAction.Value.ToLower();
            string punishmentDetails = $"Player {playerToPunish._nickname} (ID: {steamId}) was automatically {action.ToUpper()}ed. Reason: {reason}";

            Log.LogWarning(punishmentDetails);
            try { File.AppendAllText(InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [HANDSHAKE_FAIL] " + punishmentDetails + Environment.NewLine); }
            catch (Exception ex) { Log.LogError($"Failed to write punishment to log: {ex.Message}"); }

            if (HostConsole._current != null)
            {
                HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails);
            }

            if (action == "ban")
            {
                HC_PeerListEntry targetPeer = null;
                if (HostConsole._current != null)
                {
                    foreach(var entry in HostConsole._current._peerListEntries)
                    {
                        if (entry._netId != null && entry._netId.netId == playerToPunish.netId) { targetPeer = entry; break; }
                    }
                }
                        
                if (targetPeer != null)
                {
                    HostConsole._current._selectedPeerEntry = targetPeer;
                    HostConsole._current.Ban_Peer();
                }
                else
                {
                    playerToPunish.connectionToClient.Disconnect();
                    Log.LogError($"Could not find PeerListEntry for player {playerToPunish._nickname} (netId: {playerToPunish.netId}) to BAN, kicked instead.");
                }
            }
            else
            {
                playerToPunish.connectionToClient.Disconnect();
            }
        }

        public static bool IsPlayerExempt(Player player)
        {
            if (player == null) return false;
            if (DisableForHost.Value && player._isHostPlayer) return true;
            if (!string.IsNullOrEmpty(player._steamID) && ulong.TryParse(player._steamID, out ulong steamId))
            {
                if (WhitelistedUsers.Contains(steamId)) return true;
            }
            return false;
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
                        string archivePath = Path.Combine(Path.GetDirectoryName(InfractionLogPath), $"{Path.GetFileNameWithoutExtension(InfractionLogPath)}_ARCHIVED_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                        File.Move(InfractionLogPath, archivePath);
                        Log.LogInfo($"Infraction log exceeded {MaxLogFileSizeMB.Value}MB and was archived.");
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
                    
                    if (HostConsole._current != null)
                    {
                        HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails);
                    }

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
            else
            {
				return;
            }

			Log.LogWarning(logMessage);
			try { File.AppendAllText(InfractionLogPath, logMessage + Environment.NewLine); }
			catch (Exception ex) { Log.LogError($"Failed to write to infraction log: {ex.Message}"); }
		}

        public static void ClearAllPlayerData(uint netId, ulong steamId)
        {
            ServerRemainingCooldowns.Remove(netId);
            ServerPlayerPositions.Remove(netId);
            ServerPlayerStats.Remove(netId);
            XpGainHistory.Remove(netId);
            ServerPlayerAirborneStates.Remove(netId);
            ServerPlayerInitialSpeeds.Remove(netId);
            ServerPlayerInfractionCount.Remove(netId);
            ServerPlayerGracePeriod.Remove(netId);
            ServerPunishmentCooldown.Remove(netId);
            ServerSpeedCheckCooldowns.Remove(netId);
            ServerJumpCheckCooldowns.Remove(netId);
            ServerAirborneCheckCooldowns.Remove(netId);

            if (PendingVerification.TryGetValue(steamId, out var verificationData))
            {
                if (Instance != null) Instance.StopCoroutine(verificationData.KickCoroutine);
                PendingVerification.Remove(steamId);
            }
            VerifiedSteamIDs.Remove(steamId);
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
            if (NetworkServer.active)
            {
                if (!hasServerInitialized)
                {
                    Main.Log.LogInfo("First player has spawned. HardAntiCheat server-side modules are now active.");
                    hasServerInitialized = true;
                }

                uint netId = __instance.netId;
                Player player = __instance.GetComponent<Player>();

                Main.ServerPlayerGracePeriod[netId] = Time.time + GRACE_PERIOD_SECONDS;
                if (!Main.ServerPlayerInitialSpeeds.ContainsKey(netId))
                {
                    Main.ServerPlayerInitialSpeeds[netId] = __instance.Network_movSpeed;
                }
                Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = __instance.transform.position, Timestamp = Time.time };

                // --- INTEGRITY HANDSHAKE INITIATION ---
                if (Main.EnableIntegrityChecks.Value)
                {
                    if (player != null && !player.isLocalPlayer && ulong.TryParse(player._steamID, out ulong steamId))
                    {
                        if (Main.IsPlayerExempt(player))
                        {
                            Main.Log.LogInfo($"Player {player._nickname} is exempt from anti-cheat checks. Skipping verification.");
                            Main.VerifiedSteamIDs.Add(steamId);
                        }
                        else if (!Main.PendingVerification.ContainsKey(steamId) && !Main.VerifiedSteamIDs.Contains(steamId))
                        {
                            Main.Log.LogInfo($"Player {player._nickname} spawned on server. Sending integrity challenge...");
                            string token = Guid.NewGuid().ToString();
                            int challengeType = 0;
                            string expectedHash = HACIntegrity.GenerateClientHash(token, challengeType);
                            var requestPacket = new HAC_HandshakeRequest { TargetSteamID = steamId, ChallengeToken = token, ChallengeType = challengeType };
                            CodeTalkerNetwork.SendNetworkPacket(requestPacket);
                            var kickCoroutine = Main.Instance.StartCoroutine(Main.Instance.KickClientAfterDelay(player));
                            Main.PendingVerification[steamId] = (expectedHash, kickCoroutine);
                        }
                    }
                }
                
                // --- NEW: FLUFFUTILITIES STEAM LOBBY DATA CHECK ---
                if (player != null && !Main.IsPlayerExempt(player) && Main.EnableFluffUtilitiesCheck.Value)
                {
                    if (ulong.TryParse(player._steamID, out ulong steamId))
                    {
                        // FIX: A ulong must be passed to the CSteamID constructor. Direct assignment is not allowed.
                        CSteamID steamCId = new CSteamID(steamId);
                        CSteamID lobbyId = new CSteamID(SteamLobby._current._currentLobbyID);
                        string fluffKey = "cc8615a747a44321be7911e36887b64a.ver";

                        string fluffVersion = SteamMatchmaking.GetLobbyMemberData(lobbyId, steamCId, fluffKey);

                        if (!string.IsNullOrEmpty(fluffVersion))
                        {
                            string action = Main.FluffUtilitiesAction.Value.ToLower();
                            string reason = $"Player {player._nickname} detected with FluffUtilities (Version: {fluffVersion}). Action: {action.ToUpper()}.";
                            Main.Log.LogWarning(reason);
                            try { File.AppendAllText(Main.InfractionLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [MOD_DETECT] " + reason + Environment.NewLine); } catch {}

                            if (action == "kick")
                            {
                                player.connectionToClient.Disconnect();
                            }
                            else if (action == "ban")
                            {
                                HC_PeerListEntry targetPeer = null;
                                if (HostConsole._current != null)
                                {
                                    foreach(var entry in HostConsole._current._peerListEntries)
                                    {
                                        if (entry._netId != null && entry._netId.netId == player.netId) { targetPeer = entry; break; }
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
                                    Main.Log.LogError($"Could not find PeerListEntry to BAN for FluffUtilities detection, kicked instead.");
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    #endregion
	
    #region UI Button Watcher
    [HarmonyPatch]
    public static class ButtonPressValidationPatch
    {
        private static DeathPromptManager _deathPromptManagerInstance;

        [HarmonyPatch(typeof(UnityEngine.UI.Button), "Press")]
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
	[HarmonyPatch]
	public static class ItemUsageValidationPatch
	{
		[HarmonyPatch(typeof(PlayerInventory), "UserCode_Cmd_UseConsumable__ItemData")]
		[HarmonyPrefix]
		public static void GrantTokenOnTearUsage(PlayerInventory __instance, ItemData _itemData)
		{
			if (!NetworkServer.active) return;

			if (_itemData != null && _itemData._itemName == "Angela's Tear")
			{
				Main.AuthorizedSelfRevives[__instance.netId] = Time.time;
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
		public static bool ValidateRevive(StatusEntity __instance, StatusEntity _statusEntity)
		{
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			Player playerToRevive = _statusEntity.GetComponent<Player>();
			if (playerToRevive == null) return true; 
			if (Main.IsPlayerExempt(playerToRevive)) return true;
			if (__instance.netId == _statusEntity.netId)
			{
				Main.LogInfraction(__instance, "Unauthorized Action (Direct Self-Revive)", "Blocked direct call to self-revive.");
				return false;
			}
			return true;
		}

		[HarmonyPatch(typeof(StatusEntity), "Cmd_ReplenishAll")]
		[HarmonyPrefix]
		public static bool ValidateReplenish(StatusEntity __instance)
		{
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableReviveChecks.Value) return true;
			if (Main.IsPlayerExempt(player)) return true;

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
    		if (Main.IsPlayerExempt(player)) return true;
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
    		if (Main.IsPlayerExempt(player)) return true;
    		Main.LogInfraction(__instance, "Movement Hack (Illegal Teleport Command)", "Player directly called a Teleport command.");
    		return false;
    	}
    }
    #endregion
	
	#endregion

	#region Movement/Airborne Protection
	[HarmonyPatch(typeof(PlayerMove), "Init_Jump")]
	public static class JumpValidationPatch
	{
		public static bool Prefix(PlayerMove __instance)
		{
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableAirborneChecks.Value) return true;
			if (Main.IsPlayerExempt(player)) return true;
            
			uint netId = __instance.netId;
			if (!Main.ServerPlayerAirborneStates.ContainsKey(netId))
				Main.ServerPlayerAirborneStates[netId] = new PlayerAirborneData { AirTime = 0f, LastGroundedPosition = __instance.transform.position, ServerSideJumpCount = 0, LastVerticalPosition = __instance.transform.position.y, VerticalStallTime = 0f };
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
        private const float VERTICAL_STALL_TOLERANCE = 0.05f;
        private const float VERTICAL_STALL_GRACE_PERIOD = 0.5f;
        private const float MAX_FLIGHT_HEIGHT = 4240f;

        public static void Postfix(PlayerMove __instance)
        {
            if (!NetworkServer.active || !Main.EnableAntiCheat.Value || AtlyssNetworkManager._current._soloMode) return;
            Player player = __instance.GetComponent<Player>();
            if (Main.IsPlayerExempt(player)) return;
            
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
                        if (__instance._movSpeed > initialSpeed * 1.5f) // Allow for some buffer
                        {
                            Main.LogInfraction(__instance, "Stat Manipulation (Move Speed)", $"Detected illegal move speed of {__instance._movSpeed}. Reverting to initial speed of {initialSpeed}.");
                            __instance.Reset_MoveSpeed();
	                        Main.ServerSpeedCheckCooldowns[netId] = Time.time + Main.SpeedHackDetectionCooldown.Value;
                        }
						else if (__instance._movSpeed < 20)
						{
                            __instance.Reset_MoveSpeed();
						}
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
                            
                            float serverSideMoveSpeed = Main.ServerPlayerInitialSpeeds.TryGetValue(netId, out float speed) ? speed : 50f;
                            float maxPossibleDistance = (serverSideMoveSpeed * 1.5f * timeElapsed) + Main.MovementGraceBuffer.Value;
                            
                            if (distanceTraveled > maxPossibleDistance)
                            {
                                string cheatType = distanceTraveled > Main.TeleportDistanceThreshold.Value ? "Movement Hack (Teleport)" : "Movement Hack (Speed Mismatch)";
                                string details = $"Moved {distanceTraveled:F1} units in {timeElapsed:F2}s (Expected max: {maxPossibleDistance:F1}). Reverting position.";

                                foreach (var conn in NetworkServer.connections.Values)
                                {
                                    if (conn?.identity == null || conn.identity.netId == netId) continue;
                                    if (Vector3.Distance(currentPosition, conn.identity.GetComponent<Player>().transform.position) < 1.0f)
                                    {
                                        details += " Teleported directly to another player.";
                                        cheatType = "Movement Hack (Teleport)";
                                        break;
                                    }
                                }
                                Main.LogInfraction(__instance, cheatType, details);
                                player.transform.position = lastPositionData.Position;
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
                PlayerAirborneData airData = Main.ServerPlayerAirborneStates.ContainsKey(netId)
                    ? Main.ServerPlayerAirborneStates[netId]
                    : new PlayerAirborneData { AirTime = 0f, LastGroundedPosition = currentPosition, ServerSideJumpCount = 0, LastVerticalPosition = currentPosition.y, VerticalStallTime = 0f };

                if (!Main.ServerJumpCheckCooldowns.ContainsKey(netId) || Time.time > Main.ServerJumpCheckCooldowns[netId])
                {
                    if (__instance._maxJumps >= Main.JumpThreshold.Value)
                    {
	                    Main.LogInfraction(__instance, "Stat Manipulation (Overboarded Jumps)", $"Client reported _maxJumps of {__instance._maxJumps}. Reverting to 2.");
	                    __instance._maxJumps = 2;
	                    Main.ServerJumpCheckCooldowns[netId] = Time.time + Main.JumpHackDetectionCooldown.Value;
                    }
                }

                bool isGrounded = __instance.RayGroundCheck();

                if (isGrounded)
                {
                    airData.AirTime = 0f;
                    airData.ServerSideJumpCount = 0;
                    airData.LastGroundedPosition = currentPosition;
                    airData.VerticalStallTime = 0f;
                }
                else
                {
                    airData.AirTime += Time.deltaTime;

                    if (Mathf.Abs(currentPosition.y - airData.LastVerticalPosition) < VERTICAL_STALL_TOLERANCE)
                    {
                        airData.VerticalStallTime += Time.deltaTime;
                    }
                    else
                    {
                        airData.VerticalStallTime = 0f;
                    }
                }
                
                airData.LastVerticalPosition = currentPosition.y;
                
                if (currentPosition.y > MAX_FLIGHT_HEIGHT)
                {
                    Main.LogInfraction(__instance, "Movement Hack (Fly)", $"Exceeded maximum height limit of {MAX_FLIGHT_HEIGHT}. Reverting to last ground position.");
                    __instance.transform.position = airData.LastGroundedPosition;
                    return;
                }
                
                if (airData.AirTime > MAX_ALLOWED_AIR_TIME)
                {
	                if (airData.VerticalStallTime < VERTICAL_STALL_GRACE_PERIOD)
	                {
		                if (!Main.ServerAirborneCheckCooldowns.ContainsKey(netId) || Time.time > Main.ServerAirborneCheckCooldowns[netId])
		                {
			                Main.LogInfraction(__instance, "Movement Hack (Fly)", $"Airborne for {airData.AirTime:F1} seconds. Reverting to last ground position.");
			                Main.ServerAirborneCheckCooldowns[netId] = Time.time + Main.AirborneHackDetectionCooldown.Value;
		                }
	                }
                    
                    __instance.transform.position = airData.LastGroundedPosition;
                    airData.AirTime = 0f;
                    airData.ServerSideJumpCount = 0;
                    airData.VerticalStallTime = 0f;
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
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;
			if (Main.IsPlayerExempt(player)) return true;
			
            uint netId = __instance.netId;
			if (!Main.ServerPlayerStats.TryGetValue(netId, out PlayerStatsData stat))
			{
				Main.ServerPlayerStats[netId] = new PlayerStatsData { Experience = value, Level = __instance.Network_currentLevel };
				return true;
			}
			int oldExperience = stat.Experience;
			int experienceGain = value - oldExperience;
            
            if (experienceGain <= 0)
            {
                PlayerStatsData currentStatsOnLoss = Main.ServerPlayerStats[netId];
                currentStatsOnLoss.Experience = value;
                Main.ServerPlayerStats[netId] = currentStatsOnLoss;
                return true;
            }

			if (experienceGain > Main.MaxPlausibleXPGain.Value)
			{
				Main.LogInfraction(__instance, "Stat Manipulation (Experience)", $"Attempted to gain {experienceGain} XP at once (Limit: {Main.MaxPlausibleXPGain.Value}). Blocked.");
				value = oldExperience;
                return false;
			}
            
            if (IsXpGainRateExceeded(netId, experienceGain))
            {
                Main.LogInfraction(__instance, "Stat Manipulation (XP Rate)", $"Attempted to gain XP too quickly. Blocked.");
                value = oldExperience;
                return false;
            }

			PlayerStatsData currentStats = Main.ServerPlayerStats[netId];
			currentStats.Experience = value;
			Main.ServerPlayerStats[netId] = currentStats;
			return true;
		}

        private static bool IsXpGainRateExceeded(uint netId, int gain)
        {
            if (!Main.XpGainHistory.ContainsKey(netId))
            {
                Main.XpGainHistory[netId] = new List<(float Timestamp, int Amount)>();
            }

            var history = Main.XpGainHistory[netId];
            float currentTime = Time.time;
            float window = Main.XPGainWindowSeconds.Value;

            history.RemoveAll(entry => currentTime - entry.Timestamp > window);

            int sumInWindow = history.Sum(entry => entry.Amount);
            if (sumInWindow + gain > Main.MaxXPGainPerWindow.Value)
            {
                return true;
            }
            
            history.Add((currentTime, gain));
            return false;
        }

		[HarmonyPatch(typeof(PlayerStats), "set_Network_currentLevel")]
		[HarmonyPrefix]
		public static bool ValidateLevelChange(PlayerStats __instance, ref int value)
		{
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableExperienceChecks.Value) return true;
            if (Main.IsPlayerExempt(player)) return true;

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
			Player player = __instance.GetComponent<Player>();
			if (!NetworkServer.active || !Main.EnableAntiCheat.Value || !Main.EnableCooldownChecks.Value) return true;
            if (Main.IsPlayerExempt(player)) return true;

            ScriptableSkill skillToCast = __instance._currentCastSkill;
            if (skillToCast == null) return true; 

			uint netId = __instance.netId;
			
			if (Main.ServerRemainingCooldowns.TryGetValue(netId, out var playerSkills) && playerSkills.ContainsKey(skillToCast.name))
			{
                __instance.Server_InterruptCast();
                Main.LogInfraction(__instance, "Skill Manipulation", $"Attempted to cast {skillToCast.name} while on cooldown.");
				return false; 
			}

            if (!Main.ServerRemainingCooldowns.ContainsKey(netId)) Main.ServerRemainingCooldowns[netId] = new Dictionary<string, float>();
			Main.ServerRemainingCooldowns[netId][skillToCast.name] = skillToCast._skillRankParams._baseCooldown;

			return true;
		}
	}
    #endregion

    #region Network Connection Management
	[HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerDisconnect")]
	public static class PlayerDisconnectPatch
	{
		public static void Postfix(NetworkConnectionToClient _conn)
		{
			if (_conn != null && _conn.identity != null)
			{
				uint netId = _conn.identity.netId;
                if(ulong.TryParse(_conn.identity.GetComponent<Player>()?._steamID, out ulong steamId))
                {
                    Main.ClearAllPlayerData(netId, steamId);
                }
			}
		}
	}

    [HarmonyPatch(typeof(AtlyssNetworkManager), "OnStartServer")]
    public static class ServerStartPatch
    {
        public static void Postfix()
        {
            if (Main.EnableIntegrityChecks.Value)
            {
                CodeTalkerNetwork.RegisterListener<HAC_HandshakeResponse>(Main.OnServerReceivedHandshakeResponse);
                Main.Log.LogInfo("HAC Server-side integrity check handler registered.");

                if (SteamUser.GetSteamID() is CSteamID steamId && steamId.IsValid())
                {
                    Main.VerifiedSteamIDs.Add(steamId.m_SteamID);
                    Main.Log.LogInfo($"Host (SteamID: {steamId.m_SteamID}) has been automatically verified.");
                }
            }
        }
    }
    #endregion
}