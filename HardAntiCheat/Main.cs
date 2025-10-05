using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace HardAntiCheat
{
    // These are the messages we will send over the network.
    public struct HandshakeRequestMessage : NetworkMessage { }
    public struct HandshakeResponseMessage : NetworkMessage { }

    // This class provides the manual serialization methods that Mirror needs because the weaver doesn't run on mods.
    public static class HandshakeMessageExtensions
    {
        public static void WriteHandshakeRequestMessage(this NetworkWriter writer, HandshakeRequestMessage msg) { }
        public static HandshakeRequestMessage ReadHandshakeRequestMessage(this NetworkReader reader) { return new HandshakeRequestMessage(); }
        public static void WriteHandshakeResponseMessage(this NetworkWriter writer, HandshakeResponseMessage msg) { }
        public static HandshakeResponseMessage ReadHandshakeResponseMessage(this NetworkReader reader) { return new HandshakeResponseMessage(); }
    }


    #region Data & Packet Structures
    public class FluffDetectionState
    {
        public float SparkleTimestamp = 0f;
    }
    public struct PlayerPositionData { public Vector3 Position; public float Timestamp; }
    public struct PlayerStatsData { public int Level; public int Experience; }
    public struct PlayerAirborneData { public float AirTime; public Vector3 LastGroundedPosition; public int ServerSideJumpCount; public float LastVerticalPosition; public float VerticalStallTime; }
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
        // (All config entries are correct and remain unchanged)
        #region Config and Data
        public static ConfigEntry<bool> EnableAntiCheat;
        public static ConfigEntry<bool> DisableForHost;
        public static ConfigEntry<bool> EnableClientVerification;
        public static ConfigEntry<float> VerificationTimeout;
        public static ConfigEntry<int> MaxLogFileSizeMB;
        public static ConfigEntry<bool> EnableMovementChecks;
        public static ConfigEntry<bool> EnableNoclipChecks;
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
        #endregion

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            // (Path and Config binding logic is correct)
            #region Config Binding
            string pluginsPath = Directory.GetParent(Path.GetDirectoryName(Info.Location)).FullName;
            string targetLogDirectory = Path.Combine(pluginsPath, "HardAntiCheat");
            Directory.CreateDirectory(targetLogDirectory);
            InfractionLogPath = Path.Combine(targetLogDirectory, $"{ModInfo.NAME}_InfractionLog.txt");
            EnableAntiCheat = Config.Bind("1. General", "Enable AntiCheat", true, "Master switch to enable or disable all anti-cheat modules.");
            DisableForHost = Config.Bind("1. General", "Disable Detections for Host", true, "If true, the player hosting the server will not be checked for infractions.");
            EnableNoclipChecks = Config.Bind("1. General", "Enable Noclip Checks", true, "Detects if a player is moving while their standard movement controller is disabled.");
            EnableClientVerification = Config.Bind("1. General", "Enable Client Verification", false, "OPTIONAL & NOT RECOMMENDED: If true, kicks players who don't have HardAntiCheat installed. This can be spoofed by cheaters and may block friends.");
            VerificationTimeout = Config.Bind("1. General", "Verification Timeout", 10.0f, "How many seconds the server will wait for a client to verify before kicking them (if Client Verification is enabled).");
            MaxLogFileSizeMB = Config.Bind("1. General", "Max Log File Size (MB)", 5, "If the infraction log exceeds this size, it will be archived on startup.");
            EnableMovementChecks = Config.Bind("2. Movement Detections", "Enable Teleport/Speed Checks", true, "Checks incoming player movement packets for impossibly fast travel and blocks them.");
            MaxEffectiveSpeed = Config.Bind("2. Movement Detections", "Max Effective Speed", 100f, "The maximum plausible speed (units per second) a player can move.");
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
            #endregion

            CheckAndArchiveLogFile();
            harmony.PatchAll();
            Log.LogInfo($"[{ModInfo.NAME}] has been loaded.");
        }

        // All other methods in Main (Handshake handlers, Kick coroutine, LogInfraction, etc.) are correct and remain unchanged.
        // ...
        #region Other Methods
        public static void OnClientReceivedHandshakeRequest(HandshakeRequestMessage msg)
        {
            Log.LogInfo("Received verification request from server. Sending response...");
            NetworkClient.Send(new HandshakeResponseMessage());
        }

        public static void OnServerReceivedHandshakeResponse(NetworkConnectionToClient conn, HandshakeResponseMessage msg)
        {
            int senderConnectionId = conn.connectionId;
            if (PendingVerification.TryGetValue(senderConnectionId, out Coroutine kickCoroutine))
            {
                Instance.StopCoroutine(kickCoroutine);
                PendingVerification.Remove(senderConnectionId);
                VerifiedConnections.Add(senderConnectionId);
                Log.LogInfo($"Connection ID {senderConnectionId} has been successfully verified.");
            }
        }

        public IEnumerator KickClientAfterDelay(NetworkConnectionToClient conn)
        {
            if (conn == null) yield break;
            yield return new WaitForSeconds(VerificationTimeout.Value);
            if (conn != null && !VerifiedConnections.Contains(conn.connectionId))
            {
                Log.LogWarning($"Disconnecting connection ID {conn.connectionId} for failing to verify presence.");
                conn.Disconnect();
            }
            if (conn != null) PendingVerification.Remove(conn.connectionId);
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

                    if (HostConsole._current != null) HostConsole._current.Init_ServerMessage("[HAC]: " + punishmentDetails);

                    if (action == "kick") player.connectionToClient.Disconnect();
                    else // Ban
                    {
                        HC_PeerListEntry targetPeer = null;
                        if (HostConsole._current != null)
                        {
                            foreach (var entry in HostConsole._current._peerListEntries)
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

            if (PendingVerification.TryGetValue(connectionId, out Coroutine kickCoroutine))
            {
                if (Instance != null) Instance.StopCoroutine(kickCoroutine);
                PendingVerification.Remove(connectionId);
            }
            VerifiedConnections.Remove(connectionId);
        }
        #endregion
    }

    #region Player Connection & Initialization
    [HarmonyPatch(typeof(PlayerMove), "Start")]
    public static class PlayerSpawnPatch
    {
        private const float GRACE_PERIOD_SECONDS = 3.0f;
        private static bool clientHandshakeSetupDone = false;

        public static void Postfix(PlayerMove __instance)
        {
            // --- SERVER-SIDE LOGIC ---
            // This block runs on the server for every player that spawns.
            if (NetworkServer.active)
            {
                uint netId = __instance.netId;
                Main.ServerPlayerGracePeriod[netId] = Time.time + GRACE_PERIOD_SECONDS;
                if (!Main.ServerPlayerInitialSpeeds.ContainsKey(netId))
                {
                    Main.ServerPlayerInitialSpeeds[netId] = __instance.Network_movSpeed;
                }
                Main.ServerPlayerPositions[netId] = new PlayerPositionData { Position = __instance.transform.position, Timestamp = Time.time };

                // This is the correct time to SEND the request.
                if (Main.EnableClientVerification.Value)
                {
                    Player player = __instance.GetComponent<Player>();
                    if (player != null && !player.isLocalPlayer)
                    {
                        NetworkConnectionToClient conn = player.connectionToClient;
                        if (conn != null && !Main.PendingVerification.ContainsKey(conn.connectionId) && !Main.VerifiedConnections.Contains(conn.connectionId))
                        {
                            Main.Log.LogInfo($"Player object for connection ID {conn.connectionId} spawned on server. Sending verification request...");
                            conn.Send(new HandshakeRequestMessage());
                            var kickCoroutine = Main.Instance.StartCoroutine(Main.Instance.KickClientAfterDelay(conn));
                            Main.PendingVerification.Add(conn.connectionId, kickCoroutine);
                        }
                    }
                }
            }

            // --- CLIENT-SIDE LOGIC ---
            // This block runs on the client when ANY player object spawns.
            // This is the correct time to REGISTER the handler, as the client is fully loaded.
            if (NetworkClient.active && !clientHandshakeSetupDone)
            {
                // Manually register the serializers for our custom messages.
                Writer<HandshakeRequestMessage>.write = HandshakeMessageExtensions.WriteHandshakeRequestMessage;
                Writer<HandshakeResponseMessage>.write = HandshakeMessageExtensions.WriteHandshakeResponseMessage;
                Reader<HandshakeRequestMessage>.read = HandshakeMessageExtensions.ReadHandshakeRequestMessage;
                Reader<HandshakeResponseMessage>.read = HandshakeMessageExtensions.ReadHandshakeResponseMessage;

                // Register the handler to listen for the server's request.
                NetworkClient.RegisterHandler<HandshakeRequestMessage>(Main.OnClientReceivedHandshakeRequest, false);
                clientHandshakeSetupDone = true;
                Main.Log.LogInfo("HAC Client-side handshake handler and serializers registered.");
            }
        }
    }
    #endregion

    // All other game mechanic patches are correct and remain unchanged...
    #region Game Patches
    // ...
    #endregion

    #region Network Connection Management
    [HarmonyPatch(typeof(AtlyssNetworkManager), "OnStartServer")]
    public static class ServerStartPatch
    {
        public static void Postfix()
        {
            // Manually register the serializers for our custom messages.
            Writer<HandshakeRequestMessage>.write = HandshakeMessageExtensions.WriteHandshakeRequestMessage;
            Writer<HandshakeResponseMessage>.write = HandshakeMessageExtensions.WriteHandshakeResponseMessage;
            Reader<HandshakeRequestMessage>.read = HandshakeMessageExtensions.ReadHandshakeRequestMessage;
            Reader<HandshakeResponseMessage>.read = HandshakeMessageExtensions.ReadHandshakeResponseMessage;

            // Register the server-side handler for receiving responses.
            NetworkServer.RegisterHandler<HandshakeResponseMessage>(Main.OnServerReceivedHandshakeResponse, false);
            Main.Log.LogInfo("HAC Server-side handshake handlers and serializers registered.");

            if (Main.EnableClientVerification.Value)
            {
                NetworkConnectionToClient localConn = NetworkServer.localConnection;
                if (localConn != null)
                {
                    Main.VerifiedConnections.Add(localConn.connectionId);
                    Main.Log.LogInfo($"Host connection (ID: {localConn.connectionId}) has been automatically verified.");
                }
            }
        }
    }

    // ClientStartPatch is no longer needed, as its logic is now correctly timed in PlayerSpawnPatch.

    [HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerConnect")]
    public static class ServerConnectPatch
    {
        public static void Postfix(NetworkConnectionToClient _conn)
        {
            // This is intentionally blank.
        }
    }

    [HarmonyPatch(typeof(AtlyssNetworkManager), "OnServerDisconnect")]
    public static class PlayerDisconnectPatch
    {
        public static void Postfix(NetworkConnectionToClient _conn)
        {
            if (_conn == null) return;

            if (_conn.identity != null)
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