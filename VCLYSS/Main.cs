using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using CodeTalker.Networking;
using CodeTalker.Packets;
using Steamworks;
using UnityEngine;
using UnityEngine.Audio;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using CodeTalker; 
using Nessie.ATLYSS.EasySettings;
using CompressionLevel = System.IO.Compression.CompressionLevel; 

namespace VCLYSS
{
    [BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
    [BepInDependency("CodeTalker")]
    public class Main : BaseUnityPlugin
    {
        public static Main Instance;
        internal static ManualLogSource Log;
        private Harmony _harmony;

        // --- CONFIGURATION ---
        public static ConfigEntry<bool> CfgEnabled;
        public static ConfigEntry<float> CfgMasterVolume;
        public static ConfigEntry<bool> CfgMuteAll;
        public static ConfigEntry<MicMode> CfgMicMode;
        public static ConfigEntry<KeyCode> CfgPushToTalk;
        public static ConfigEntry<float> CfgMicThreshold;
        public static ConfigEntry<bool> CfgMicTest; 
        public static ConfigEntry<float> CfgMinDistance; 
        public static ConfigEntry<float> CfgMaxDistance; 
        public static ConfigEntry<bool> CfgSpatialBlending; 
        public static ConfigEntry<bool> CfgShowOverlay;
        public static ConfigEntry<bool> CfgShowHeadIcons;
        public static ConfigEntry<float> CfgBubbleScale; 
        public static ConfigEntry<bool> CfgLipSync; 
        public static ConfigEntry<bool> CfgDebugMode; 

        public enum MicMode { PushToTalk, Toggle, AlwaysOn }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            InitConfig();
            Settings.OnInitialized.AddListener(AddSettings);
            Settings.OnApplySettings.AddListener(() => { Config.Save(); VoiceSystem.ApplySettingsToAll(); });

            _harmony = new Harmony(ModInfo.GUID);
            _harmony.PatchAll();

            StartCoroutine(RegisterPacketDelayed());

            var go = new GameObject("VCLYSS_System");
            DontDestroyOnLoad(go);
            go.AddComponent<VoiceSystem>();
            go.AddComponent<VoiceOverlay>();

            Logger.LogInfo($"[{ModInfo.NAME}] Loaded. Voice System Started.");
        }

        private IEnumerator RegisterPacketDelayed()
        {
            // [FIX] Wait until player is fully loaded IN_GAME before registering listener
            // This prevents packet spam from blocking threads during the initial loading screen
            while (Player._mainPlayer == null || Player._mainPlayer._currentGameCondition != GameCondition.IN_GAME)
            {
                yield return new WaitForSeconds(1f);
            }

            // Extra safety buffer for assets to initialize
            yield return new WaitForSeconds(2f);

            CodeTalkerNetwork.RegisterBinaryListener<VoicePacket>(OnVoicePacketReceived);
            if (CfgDebugMode.Value) Main.Log.LogDebug("Voice packet listener registered");
        }

        private void OnVoicePacketReceived(PacketHeader header, BinaryPacketBase packet)
        {
            if (!VoiceSystem.Instance || !VoiceSystem.Instance.IsSessionReady) return;

            if (packet is VoicePacket voicePkt)
            {
                if (voicePkt.VoiceData == null || voicePkt.VoiceData.Length == 0) return;
                
                if (voicePkt.VoiceData.Length > 65535) 
                {
                    if (CfgDebugMode.Value) Main.Log.LogWarning($"[Security] Dropped oversized packet from {header.SenderID}");
                    return;
                }

                if (CfgDebugMode.Value)
                {
                    Main.Log.LogDebug($"[Flow] Recv Packet | Sender: {header.SenderID} | Size: {voicePkt.VoiceData.Length}");
                }

                VoiceSystem.Instance.RoutePacket(header.SenderID, voicePkt.VoiceData);
            }
        }

        private void InitConfig()
        {
            CfgEnabled = Config.Bind("1. General", "Voice Chat Active", true, "Master Switch");
            CfgMasterVolume = Config.Bind("1. General", "Master Volume", 1.0f, new ConfigDescription("Incoming Voice Volume Boost (0 = Mute)", new AcceptableValueRange<float>(0.0f, 5.0f)));
            CfgMuteAll = Config.Bind("1. General", "Mute Everyone (Panic)", false, "Panic button to silence all incoming voice");
            
            CfgMicMode = Config.Bind("2. Input", "Input Mode", MicMode.PushToTalk, "Input Method");
            CfgPushToTalk = Config.Bind("2. Input", "Push To Talk Key", KeyCode.T, "Bind for PTT/Toggle");
            CfgMicThreshold = Config.Bind("2. Input", "Mic Activation Threshold", 0.05f, new ConfigDescription("Gate threshold", new AcceptableValueRange<float>(0.0f, 0.5f)));
            CfgMicTest = Config.Bind("2. Input", "Mic Test (Loopback)", false, "Hear your own voice to test settings");
            
            CfgMinDistance = Config.Bind("3. Spatial", "Min Distance", 5.0f, new ConfigDescription("Distance where audio is 100% volume", new AcceptableValueRange<float>(1.0f, 50.0f)));
            CfgMaxDistance = Config.Bind("3. Spatial", "Max Distance", 40.0f, new ConfigDescription("Distance where audio becomes silent", new AcceptableValueRange<float>(10.0f, 256.0f)));
            CfgSpatialBlending = Config.Bind("3. Spatial", "3D Spatial Audio", true, "Enable 3D Directional Audio");
            
            CfgShowOverlay = Config.Bind("4. Visuals", "Show Voice Overlay", true, "Show list of speakers in top right");
            CfgShowHeadIcons = Config.Bind("4. Visuals", "Show Head Icons (Bubble)", true, "Show GMod-style bubble");
            CfgBubbleScale = Config.Bind("4. Visuals", "Bubble Scale", 0.2f, new ConfigDescription("Size of the speech bubble", new AcceptableValueRange<float>(0.05f, 2.0f)));
            CfgLipSync = Config.Bind("4. Visuals", "Enable Lip Sync", true, "Animate mouths when talking");

            CfgDebugMode = Config.Bind("5. Advanced", "Debug Mode (Verbose)", false, "Enable traffic flow logs. Warning: Spams console.");
        }

        private void AddSettings()
        {
            SettingsTab tab = Settings.ModTab;
            tab.AddHeader("General");
            tab.AddToggle(CfgEnabled);
            tab.AddToggle(CfgMuteAll);
            tab.AddSlider(CfgMasterVolume);
            tab.AddHeader("Microphone");
            tab.AddDropdown(CfgMicMode);
            tab.AddKeyButton(CfgPushToTalk);
            tab.AddSlider(CfgMicThreshold); 
            tab.AddToggle(CfgMicTest);
            tab.AddHeader("Spatial / Earmuffs");
            tab.AddToggle(CfgSpatialBlending);
            tab.AddSlider(CfgMinDistance);
            tab.AddSlider(CfgMaxDistance);
            tab.AddHeader("Visuals");
            tab.AddToggle(CfgShowOverlay);
            tab.AddToggle(CfgShowHeadIcons);
            tab.AddSlider(CfgBubbleScale);
            tab.AddToggle(CfgLipSync);
            tab.AddHeader("Advanced");
            tab.AddToggle(CfgDebugMode);
        }
    }

    public static class VoiceAPI
    {
        public static event Action<Player> OnPlayerStartSpeaking;
        public static event Action<Player> OnPlayerStopSpeaking;

        public static bool IsPlayerSpeaking(Player player)
        {
            var vm = VoiceSystem.Instance.GetManagerForPlayer(player);
            return vm != null && vm.IsSpeaking();
        }

        public static void SetPlayerMute(Player player, bool isMuted)
        {
            var vm = VoiceSystem.Instance.GetManagerForPlayer(player);
            if (vm != null) vm.SetExternalMute(isMuted);
        }

        public static void SetPlayerVolume(Player player, float volumeMultiplier)
        {
            var vm = VoiceSystem.Instance.GetManagerForPlayer(player);
            if (vm != null) vm.SetExternalGain(volumeMultiplier);
        }

        public static void SetPlayerSpatialOverride(Player player, bool? forceSpatial)
        {
            var vm = VoiceSystem.Instance.GetManagerForPlayer(player);
            if (vm != null) vm.SetSpatialOverride(forceSpatial);
        }

        internal static void TriggerStartSpeaking(Player p) => OnPlayerStartSpeaking?.Invoke(p);
        internal static void TriggerStopSpeaking(Player p) => OnPlayerStopSpeaking?.Invoke(p);
    }

    public class VoicePacket : BinaryPacketBase
    {
        public override string PacketSignature => "VCLYSS_VOICE";
        public byte[] VoiceData;
        public VoicePacket() { }
        public VoicePacket(byte[] data) { VoiceData = data; }
        public override byte[] Serialize() { return VoiceData ?? new byte[0]; }
        public override void Deserialize(byte[] data) { VoiceData = data; }
    }

    public class VoiceSystem : MonoBehaviour
    {
        public static VoiceSystem Instance;
        public static List<VoiceManager> ActiveManagers = new List<VoiceManager>();
        public static bool IsVoiceAllowedInLobby = false;
        public bool IsSessionReady = false;

        void Awake() 
        { 
            Instance = this; 
            StartCoroutine(SessionMonitor());
            StartCoroutine(PlayerScanner());
        }

        private IEnumerator SessionMonitor()
        {
            WaitForSeconds wait = new WaitForSeconds(0.5f);
            while (true)
            {
                bool isMainPlayerReady = Player._mainPlayer != null && Player._mainPlayer._currentGameCondition == GameCondition.IN_GAME;

                if (isMainPlayerReady && !IsSessionReady)
                {
                    // [FIX] Added safety buffer after IN_GAME detection
                    // This ensures the player model and assets are fully loaded before we start processing packets
                    if (Main.CfgDebugMode.Value) Main.Log.LogDebug("Player In-Game detected. Waiting for assets...");
                    yield return new WaitForSeconds(2.0f);

                    IsSessionReady = true;
                    if (Main.CfgDebugMode.Value) Main.Log.LogDebug("VCLYSS Ready: Player is IN_GAME.");
                }
                else if (!isMainPlayerReady && IsSessionReady)
                {
                    IsSessionReady = false;
                    ActiveManagers.Clear();
                    if (Main.CfgDebugMode.Value) Main.Log.LogDebug("VCLYSS Paused: Player is not IN_GAME.");
                }
                yield return wait;
            }
        }

        private IEnumerator PlayerScanner()
        {
            WaitForSeconds wait = new WaitForSeconds(2f);
            while (true)
            {
                try
                {
                    if (IsSessionReady)
                    {
                        Player[] allPlayers = FindObjectsOfType<Player>();
                        foreach (var p in allPlayers)
                        {
                            if (p != null && p.GetComponent<VoiceManager>() == null)
                            {
                                if (Main.CfgDebugMode.Value) Main.Log.LogDebug($"[Scanner] Attaching VoiceManager to {p._nickname}");
                                p.gameObject.AddComponent<VoiceManager>();
                            }
                        }
                    }
                }
                catch (Exception e) { Main.Log.LogWarning($"Scanner error: {e.Message}"); }
                yield return wait;
            }
        }

        public void RoutePacket(ulong senderID, byte[] data)
        {
            if (!Main.CfgEnabled.Value || Main.CfgMuteAll.Value || !IsVoiceAllowedInLobby) return;
            if (senderID == GetSteamIDFromPlayer(Player._mainPlayer)) return;

            VoiceManager target = FindManager(senderID);
            if (target != null && target.OwnerID == senderID)
            {
                target.ReceiveNetworkData(data);
            }
        }

        public VoiceManager GetManagerForPlayer(Player p)
        {
            if (p == null) return null;
            return p.GetComponent<VoiceManager>();
        }

        private VoiceManager FindManager(ulong steamID)
        {
            for (int i = 0; i < ActiveManagers.Count; i++)
            {
                if (ActiveManagers[i].OwnerID == steamID) return ActiveManagers[i];
            }
            Player[] players = FindObjectsOfType<Player>();
            foreach(var p in players) 
            {
                if (GetSteamIDFromPlayer(p) == steamID)
                {
                    var vm = p.GetComponent<VoiceManager>();
                    if (vm == null) vm = p.gameObject.AddComponent<VoiceManager>();
                    return vm;
                }
            }
            return null;
        }

        public static ulong GetSteamIDFromPlayer(Player p)
        {
            if (p == null) return 0;
            if (ulong.TryParse(p._steamID, out ulong id)) return id;
            return 0;
        }

        public static void ApplySettingsToAll()
        {
            foreach(var vm in ActiveManagers) vm.ApplyAudioSettings();
        }
    }

    [HarmonyPatch]
    public class LobbyPatches
    {
        private const string LOBBY_KEY = "vclyss_version";

        [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.JoinLobby))]
        [HarmonyPostfix]
        public static void OnJoinLobby(CSteamID steamIDLobby)
        {
            Main.Instance.StartCoroutine(CheckLobbyStatus(steamIDLobby));
        }

        [HarmonyPatch(typeof(SteamMatchmaking), nameof(SteamMatchmaking.CreateLobby))]
        [HarmonyPostfix]
        public static void OnCreateLobby()
        {
            Main.Instance.StartCoroutine(SetLobbyStatus());
        }

        private static IEnumerator SetLobbyStatus()
        {
            yield return new WaitForSeconds(1.0f);
            CSteamID currentLobby = new CSteamID(SteamLobby._current._currentLobbyID);
            
            if (currentLobby.m_SteamID != 0 && Main.CfgEnabled.Value)
            {
                SteamMatchmaking.SetLobbyData(currentLobby, LOBBY_KEY, ModInfo.VERSION);
                VoiceSystem.IsVoiceAllowedInLobby = true;
                Main.Log.LogInfo("Host Lobby Created: Voice Chat ENABLED.");
            }
        }

        private static IEnumerator CheckLobbyStatus(CSteamID lobbyId)
        {
            yield return new WaitForSeconds(2.0f);
            string value = SteamMatchmaking.GetLobbyData(lobbyId, LOBBY_KEY);

            if (!string.IsNullOrEmpty(value) && Main.CfgEnabled.Value)
            {
                VoiceSystem.IsVoiceAllowedInLobby = true;
                Main.Log.LogInfo($"Joined Modded Lobby (Host Ver: {value}): Voice Chat ENABLED.");
            }
            else
            {
                VoiceSystem.IsVoiceAllowedInLobby = false;
                SteamUser.StopVoiceRecording();
                Main.Log.LogInfo("Voice Chat DISABLED (Host does not have the mod).");
            }
        }
    }

    public class VoiceManager : MonoBehaviour
    {
        public Player AttachedPlayer;
        public ulong OwnerID;
        public bool IsLocalPlayer = false;

        private float _externalGain = 1.0f;
        private bool _externalMute = false;
        private bool? _spatialOverride = null; 

        private AudioSource _audioSource;
        private GameObject _bubbleObject;
        private SpriteRenderer _bubbleRenderer;
        private LipSync _lipSync;
        
        private Vector3 _baseScale = Vector3.one;
        private static AudioMixerGroup _cachedVoiceMixer;

        private byte[] _compressedBuffer = new byte[8192]; 
        private byte[] _decompressedBuffer = new byte[65536]; 

        private float[] _floatBuffer; 
        private int _writePos = 0;
        private int _bufferLength; 
        private AudioClip _streamingClip;
        private int _sampleRate;

        private bool _isRecording = false;
        private float _lastVolume = 0f;
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;
        private float _lastPacketTime = 0f;
        private bool _isPlaying = false;
        private bool _audioInitialized = false;

        private bool _eventIsSpeaking = false;

        void Awake()
        {
            AttachedPlayer = GetComponent<Player>();
            if (AttachedPlayer == null) { Destroy(this); return; }
            StartCoroutine(WaitForSteamID());
        }

        private IEnumerator WaitForSteamID()
        {
            while (VoiceSystem.GetSteamIDFromPlayer(AttachedPlayer) == 0) yield return new WaitForSeconds(0.5f);

            while (Player._mainPlayer == null || 
                   Player._mainPlayer._currentGameCondition != GameCondition.IN_GAME ||
                   AttachedPlayer == null)
            {
                yield return new WaitForSeconds(0.5f);
            }

            OwnerID = VoiceSystem.GetSteamIDFromPlayer(AttachedPlayer);
            VoiceSystem.ActiveManagers.Add(this);

            InitializeAudio();
            InitializeBubble();
            
            _lipSync = gameObject.AddComponent<LipSync>();
            _lipSync.Initialize(AttachedPlayer);
            
            _audioInitialized = true;
            CheckLocalPlayerStatus();
            
            if (Main.CfgDebugMode.Value) Main.Log.LogDebug($"[Manager] Initialized for {AttachedPlayer._nickname}");
        }

        void Start() { if (_audioInitialized) CheckLocalPlayerStatus(); }

        void OnDestroy()
        {
            VoiceSystem.ActiveManagers.Remove(this);
            if (IsLocalPlayer && _isRecording) SteamUser.StopVoiceRecording();
        }

        public void SetExternalGain(float gain) { _externalGain = gain; }
        public void SetExternalMute(bool mute) { _externalMute = mute; }
        public void SetSpatialOverride(bool? spatial) 
        { 
            _spatialOverride = spatial; 
            ApplyAudioSettings(); 
        }

        private void InitializeAudio()
        {
            GameObject emitter = new GameObject("VoiceEmitter");
            emitter.transform.SetParent(transform, false);
            emitter.transform.localPosition = new Vector3(0, 1.7f, 0); 

            _audioSource = emitter.AddComponent<AudioSource>();
            _audioSource.loop = false; 
            _audioSource.playOnAwake = false;
            _audioSource.dopplerLevel = 0f; 

            if (_cachedVoiceMixer == null)
            {
                AudioMixerGroup[] groups = Resources.FindObjectsOfTypeAll<AudioMixerGroup>();
                foreach (var g in groups)
                {
                    if (g.name.IndexOf("Voice", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cachedVoiceMixer = g;
                        break;
                    }
                }
            }
            if (_cachedVoiceMixer != null) _audioSource.outputAudioMixerGroup = _cachedVoiceMixer;
            
            _sampleRate = (int)SteamUser.GetVoiceOptimalSampleRate();
            int bufferLen = 44100 * 2; 
            _floatBuffer = new float[bufferLen];
            _bufferLength = bufferLen; 
            
            _streamingClip = AudioClip.Create($"Voice_{OwnerID}", bufferLen, 1, _sampleRate, false);
            _streamingClip.SetData(_floatBuffer, 0);
            _audioSource.clip = _streamingClip;
            _audioSource.Play();
            
            ApplyAudioSettings();
        }

        private void InitializeBubble()
        {
            _bubbleObject = new GameObject("VoiceBubble");
            
            Transform playerEffects = AttachedPlayer.transform.Find("_playerEffects");
            Transform nativeBubble = AttachedPlayer.transform.Find("_playerEffects/_effect_chatBubble");
            
            if (playerEffects != null && nativeBubble != null)
            {
                _bubbleObject.transform.SetParent(playerEffects, false);
                _bubbleObject.transform.localPosition = nativeBubble.localPosition;
            }
            else
            {
                _bubbleObject.transform.SetParent(transform, false);
                _bubbleObject.transform.localPosition = new Vector3(0, 4.5f, 0); 
            }
            
            _bubbleRenderer = _bubbleObject.AddComponent<SpriteRenderer>();
            _bubbleRenderer.sprite = LoadVoiceSprite(); 
            _bubbleRenderer.color = Color.white; 
            
            var rot = _bubbleObject.AddComponent<RotateObject>();
            Traverse.Create(rot).Field("_rotY").SetValue(3f);
            Traverse.Create(rot).Field("_alwaysRun").SetValue(true);

            _bubbleObject.SetActive(false);
        }

        private Sprite LoadVoiceSprite()
        {
            try 
            {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = "VCLYSS.Icons.Voice.png";
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        byte[] buffer = new byte[stream.Length];
                        stream.Read(buffer, 0, buffer.Length);
                        Texture2D tex = new Texture2D(2, 2);
                        if (tex.LoadImage(buffer)) return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    }
                }
            }
            catch (Exception) { }
            return CreateCircleSprite();
        }

        public void ApplyAudioSettings()
        {
            if (_audioSource == null) return;
            
            _audioSource.volume = 1.0f; 
            
            if (IsLocalPlayer) 
            {
                _audioSource.spatialBlend = 0f; 
            }
            else
            {
                bool useSpatial = _spatialOverride.HasValue ? _spatialOverride.Value : Main.CfgSpatialBlending.Value;
                
                _audioSource.spatialBlend = useSpatial ? 1.0f : 0.0f;
                _audioSource.minDistance = Main.CfgMinDistance.Value;
                _audioSource.maxDistance = Main.CfgMaxDistance.Value;
                _audioSource.rolloffMode = AudioRolloffMode.Linear; 
            }
        }

        void Update()
        {
            if (!Main.CfgEnabled.Value || !_audioInitialized) return;
            if (!VoiceSystem.Instance.IsSessionReady) return;

            if (!IsLocalPlayer) CheckLocalPlayerStatus();
            if (!VoiceSystem.IsVoiceAllowedInLobby && !Main.CfgMicTest.Value) return;

            if (IsLocalPlayer) HandleMicInput();

            if (_isPlaying && Time.time - _lastPacketTime > 0.3f)
            {
                _audioSource.Stop();
                FlushBuffer(); 
                _isPlaying = false;
                _lastVolume = 0f;
            }

            bool currentlySpeaking = IsSpeaking();
            if (currentlySpeaking != _eventIsSpeaking)
            {
                _eventIsSpeaking = currentlySpeaking;
                if (currentlySpeaking) VoiceAPI.TriggerStartSpeaking(AttachedPlayer);
                else VoiceAPI.TriggerStopSpeaking(AttachedPlayer);
            }

            UpdateVisuals();
        }

        private void FlushBuffer()
        {
            Array.Clear(_floatBuffer, 0, _floatBuffer.Length);
            _streamingClip.SetData(_floatBuffer, 0);
            _writePos = 0;
        }

        private void HandleMicInput()
        {
            bool wantsToTalk = CheckInputKeys();

            if (wantsToTalk && !_isRecording)
            {
                SteamUser.StartVoiceRecording();
                _isRecording = true;
                if (Main.CfgDebugMode.Value) Main.Log.LogDebug("Mic Recording Started");
            }
            else if (!wantsToTalk && _isRecording)
            {
                SteamUser.StopVoiceRecording();
                _isRecording = false;
                _lastVolume = 0f; 
            }

            if (_isRecording)
            {
                while (true)
                {
                    uint available;
                    SteamUser.GetAvailableVoice(out available);
                    if (available == 0) break;

                    uint bytesWritten;
                    EVoiceResult res = SteamUser.GetVoice(true, _compressedBuffer, (uint)_compressedBuffer.Length, out bytesWritten);

                    if (res == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
                    {
                        byte[] packetData = new byte[bytesWritten];
                        Array.Copy(_compressedBuffer, packetData, bytesWritten);

                        if (Main.CfgMicTest.Value)
                        {
                            ReceiveNetworkData(packetData); 
                        }
                        else
                        {
                            var packet = new VoicePacket(packetData);
                            foreach(var vm in VoiceSystem.ActiveManagers)
                            {
                                if (vm != this && vm.AttachedPlayer != null)
                                {
                                    CodeTalkerNetwork.SendNetworkPacket(vm.AttachedPlayer, packet, Compressors.CompressionType.GZip, CompressionLevel.Fastest);
                                }
                            }
                            
                            _lastVolume = Mathf.Lerp(_lastVolume, 0.8f, Time.deltaTime * 20f); 
                            if(_lipSync != null) _lipSync.SetSpeaking();
                            _lastPacketTime = Time.time; 
                            if (!_isPlaying) { _isPlaying = true; } 
                        }
                    }
                }
            }
        }

        public void ReceiveNetworkData(byte[] compressedData)
        {
            if (_externalMute) return; 

            if (IsLocalPlayer && !Main.CfgMicTest.Value) return; 

            try 
            {
                uint bytesWritten;
                EVoiceResult res = SteamUser.DecompressVoice(compressedData, (uint)compressedData.Length, _decompressedBuffer, (uint)_decompressedBuffer.Length, out bytesWritten, (uint)_sampleRate);

                if (res == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
                {
                    ProcessPCMData(_decompressedBuffer, (int)bytesWritten);
                    if(_lipSync != null) _lipSync.SetSpeaking();
                    _lastPacketTime = Time.time; 
                }
            }
            catch (Exception ex)
            {
                if (Main.CfgDebugMode.Value) Main.Log.LogWarning($"[Security] Error processing voice packet for {OwnerID}: {ex.Message}");
            }
        }

        private void ProcessPCMData(byte[] rawBytes, int length)
        {
            int sampleCount = length / 2; 
            float maxVol = 0f;
            float gain = Main.CfgMasterVolume.Value * _externalGain; 

            for (int i = 0; i < sampleCount; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                float floatVal = (val / 32768.0f) * gain; 
                
                if (floatVal > 1f) floatVal = 1f;
                if (floatVal < -1f) floatVal = -1f;
                
                _floatBuffer[_writePos] = floatVal;
                _writePos = (_writePos + 1) % _bufferLength;

                float absVol = Mathf.Abs(floatVal);
                if (absVol > maxVol) maxVol = absVol;
            }

            int silenceSamples = _sampleRate / 2; 
            int clearStart = _writePos;
            for (int i = 0; i < silenceSamples; i++) _floatBuffer[(clearStart + i) % _bufferLength] = 0f;

            _lastVolume = Mathf.Lerp(_lastVolume, maxVol, Time.deltaTime * 10f);
            
            _streamingClip.SetData(_floatBuffer, 0);

            _audioSource.loop = false;

            if (!_audioSource.isPlaying) 
            {
                _audioSource.Play();
                _isPlaying = true;
            }
            
            int playPos = _audioSource.timeSamples;
            int dist = (_writePos - playPos + _bufferLength) % _bufferLength;
            if (dist > (_sampleRate / 5)) 
            {
                int newPos = _writePos - (_sampleRate / 20); 
                if (newPos < 0) newPos += _bufferLength;
                _audioSource.timeSamples = newPos;
            }
        }

        private void UpdateVisuals()
        {
            if (_bubbleObject == null) return;
            if (!Main.CfgShowHeadIcons.Value) { _bubbleObject.SetActive(false); return; }

            _lastVolume = Mathf.Lerp(_lastVolume, 0f, Time.deltaTime * 5f);
            bool showBubble = _lastVolume > 0.01f;

            if (showBubble)
            {
                _bubbleObject.SetActive(true);
                float baseSize = Main.CfgBubbleScale.Value;
                float animScale = baseSize + (baseSize * _lastVolume * 0.5f);
                _bubbleObject.transform.localScale = Vector3.one * animScale;
            }
            else _bubbleObject.SetActive(false);
        }

        public bool IsSpeaking() => _lastVolume > 0.01f;

        private void CheckLocalPlayerStatus()
        {
            if (IsLocalPlayer) return;
            if (Player._mainPlayer != null && AttachedPlayer == Player._mainPlayer)
            {
                IsLocalPlayer = true;
                ApplyAudioSettings();
            }
        }

        private bool CheckInputKeys()
        {
            if (Main.CfgMicTest.Value) return true;
            if (Main.CfgMicMode.Value == Main.MicMode.AlwaysOn) return true;
            if (Main.CfgMicMode.Value == Main.MicMode.PushToTalk) return Input.GetKey(Main.CfgPushToTalk.Value);
            if (Main.CfgMicMode.Value == Main.MicMode.Toggle)
            {
                bool isKeyDown = Input.GetKey(Main.CfgPushToTalk.Value);
                if (isKeyDown && !_wasKeyDown) _isToggleOn = !_isToggleOn;
                _wasKeyDown = isKeyDown;
                return _isToggleOn;
            }
            return false;
        }

        private Sprite CreateCircleSprite()
        {
            int res = 64; Texture2D tex = new Texture2D(res, res); Color[] cols = new Color[res*res];
            float r = res/2f; Vector2 c = new Vector2(r,r);
            for(int y=0;y<res;y++) for(int x=0;x<res;x++) { float d = Vector2.Distance(new Vector2(x,y), c); cols[y*res+x] = d < r ? Color.white : Color.clear; }
            tex.SetPixels(cols); tex.Apply();
            return Sprite.Create(tex, new Rect(0,0,res,res), new Vector2(0.5f,0.5f));
        }
    }

    public class LipSync : MonoBehaviour
    {
        private PlayerRaceModel _playerRaceModel;
        private bool _initialized = false;
        private Coroutine _mouthResetCoroutine;
        public enum MouthCondition { Closed = 0, Open = 1 }

        public void Initialize(Player player)
        {
            try {
                if (player == null) return;
                _playerRaceModel = player.GetComponentInChildren<PlayerRaceModel>(true);
                if (_playerRaceModel == null && player._pVisual != null) _playerRaceModel = player._pVisual._playerRaceModel;
                if (_playerRaceModel == null) return;
                _initialized = true;
            } catch (Exception) { }
        }

        public void SetSpeaking()
        {
            if (!Main.CfgLipSync.Value || !_initialized || _playerRaceModel == null) return;
            try {
                _playerRaceModel.Set_MouthCondition((global::MouthCondition)MouthCondition.Open, 0.15f);
                if (_mouthResetCoroutine != null) StopCoroutine(_mouthResetCoroutine);
                _mouthResetCoroutine = StartCoroutine(ResetMouthAfterDelay());
            } catch {}
        }

        private IEnumerator ResetMouthAfterDelay()
        {
            yield return new WaitForSeconds(0.2f);
            if (_playerRaceModel != null) _playerRaceModel.Set_MouthCondition(global::MouthCondition.Closed, 0f);
        }
    }

    public class VoiceOverlay : MonoBehaviour
    {
        private GUIStyle _style;
        private void OnGUI()
        {
            if (!Main.CfgShowOverlay.Value) return;

            if (Main.CfgMicTest.Value)
            {
                var warningStyle = new GUIStyle { fontSize = 20, alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
                warningStyle.normal.textColor = Color.yellow;
                GUI.Label(new Rect(Screen.width - 320, 100, 300, 40), "MIC TEST ACTIVE", warningStyle);
            }

            if (_style == null) {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 20; _style.normal.textColor = Color.white; _style.alignment = TextAnchor.MiddleRight; _style.fontStyle = FontStyle.Bold;
            }

            float heightPerLine = 30f;
            float xPos = Screen.width - 270f;
            float yPos = 20f; 

            GUILayout.BeginArea(new Rect(xPos, yPos, 250f, Screen.height));
            foreach (var vm in VoiceSystem.ActiveManagers)
            {
                if (vm.IsSpeaking())
                {
                    string name = vm.IsLocalPlayer ? "Me" : (vm.AttachedPlayer != null ? vm.AttachedPlayer._nickname : "Unknown");
                    GUILayout.Box(name, _style, GUILayout.Height(heightPerLine));
                }
            }
            GUILayout.EndArea();
        }
    }
}