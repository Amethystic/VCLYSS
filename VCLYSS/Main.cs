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
using System.IO;
using System.Reflection;
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
        public static ConfigEntry<bool> CfgShowRangeVisual; // New: Toggle the red ring
        public static ConfigEntry<bool> CfgLipSync; 
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
            yield return new WaitForSeconds(2f);
            CodeTalkerNetwork.RegisterBinaryListener<VoicePacket>(OnVoicePacketReceived);
            Main.Log.LogInfo("Voice packet listener registered");
        }

        private void OnVoicePacketReceived(PacketHeader header, BinaryPacketBase packet)
        {
            if (packet is VoicePacket voicePkt)
            {
                VoiceSystem.Instance.RoutePacket(header.SenderID, voicePkt.VoiceData);
            }
        }

        private void InitConfig()
        {
            CfgEnabled = Config.Bind("1. General", "Voice Chat Active", true, "Master Switch");
            
            // UPDATED: Default 1.0, Range up to 5.0 for boost
            CfgMasterVolume = Config.Bind("1. General", "Master Volume", 1.0f, new ConfigDescription("Incoming Voice Volume Boost (0 = Mute)", new AcceptableValueRange<float>(0.0f, 5.0f)));
            
            CfgMuteAll = Config.Bind("1. General", "Mute Everyone (Panic)", false, "Panic button to silence all incoming voice");
            CfgMicMode = Config.Bind("2. Input", "Input Mode", MicMode.PushToTalk, "Input Method");
            
            // UPDATED: Default Key 'T'
            CfgPushToTalk = Config.Bind("2. Input", "Push To Talk Key", KeyCode.T, "Bind for PTT/Toggle");
            
            CfgMicThreshold = Config.Bind("2. Input", "Mic Activation Threshold", 0.05f, new ConfigDescription("Gate threshold", new AcceptableValueRange<float>(0.0f, 0.5f)));
            CfgMicTest = Config.Bind("2. Input", "Mic Test (Loopback)", false, "Hear your own voice to test settings");
            
            // UPDATED: Max Range increased to 256
            CfgMinDistance = Config.Bind("3. Spatial", "Min Distance", 5.0f, new ConfigDescription("Distance where audio is 100% volume", new AcceptableValueRange<float>(1.0f, 50.0f)));
            CfgMaxDistance = Config.Bind("3. Spatial", "Max Distance", 40.0f, new ConfigDescription("Distance where audio becomes silent", new AcceptableValueRange<float>(10.0f, 256.0f)));
            
            CfgSpatialBlending = Config.Bind("3. Spatial", "3D Spatial Audio", true, "Enable 3D Directional Audio");
            
            CfgShowOverlay = Config.Bind("4. Visuals", "Show Voice Overlay", true, "Show list of speakers in top right");
            CfgShowHeadIcons = Config.Bind("4. Visuals", "Show Head Icons (Bubble)", true, "Show GMod-style bubble");
            CfgShowRangeVisual = Config.Bind("4. Visuals", "Show Earmuff Visual", false, "Shows a red ring indicating hearing range");
            CfgLipSync = Config.Bind("4. Visuals", "Enable Lip Sync", true, "Animate mouths when talking");
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
            tab.AddToggle(CfgShowRangeVisual); // Added visual toggle
            tab.AddHeader("Visuals");
            tab.AddToggle(CfgShowOverlay);
            tab.AddToggle(CfgShowHeadIcons);
            tab.AddToggle(CfgLipSync);
        }
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

    // -----------------------------------------------------------
    // GLOBAL SYSTEM (Scanner & Router)
    // -----------------------------------------------------------
    public class VoiceSystem : MonoBehaviour
    {
        public static VoiceSystem Instance;
        public static List<VoiceManager> ActiveManagers = new List<VoiceManager>();
        
        // HOST CHECK FLAG
        public static bool IsVoiceAllowedInLobby = false;

        void Awake() 
        { 
            Instance = this; 
            StartCoroutine(PlayerScanner());
        }

        private IEnumerator PlayerScanner()
        {
            WaitForSeconds wait = new WaitForSeconds(2f);
            while (true)
            {
                try
                {
                    Player[] allPlayers = FindObjectsOfType<Player>();
                    foreach (var p in allPlayers)
                    {
                        if (p != null && p.GetComponent<VoiceManager>() == null)
                        {
                            Main.Log.LogInfo($"[Scanner] Attaching VoiceManager to {p._nickname}");
                            p.gameObject.AddComponent<VoiceManager>();
                        }
                    }
                }
                catch (Exception e) { Main.Log.LogWarning($"Scanner error: {e.Message}"); }
                yield return wait;
            }
        }

        public void RoutePacket(ulong senderID, byte[] data)
        {
            // Check Host Permission
            if (!Main.CfgEnabled.Value || Main.CfgMuteAll.Value || !IsVoiceAllowedInLobby) return;

            VoiceManager target = FindManager(senderID);
            if (target != null)
            {
                target.ReceiveNetworkData(data);
            }
        }

        private VoiceManager FindManager(ulong steamID)
        {
            for (int i = 0; i < ActiveManagers.Count; i++)
            {
                if (ActiveManagers[i].OwnerID == steamID) return ActiveManagers[i];
            }
            // Fallback: Lazy Load
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
            EarmuffVisualizer.UpdateVisuals();
        }
    }

    // -----------------------------------------------------------
    // LOBBY PATCHES (Host Requirement)
    // -----------------------------------------------------------
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

    // -----------------------------------------------------------
    // VOICE MANAGER
    // -----------------------------------------------------------
    public class VoiceManager : MonoBehaviour
    {
        public Player AttachedPlayer;
        public ulong OwnerID;
        public bool IsLocalPlayer = false;

        // --- AUDIO COMPONENTS ---
        private AudioSource _audioSource;
        private GameObject _bubbleObject;
        private SpriteRenderer _bubbleRenderer;
        private LipSync _lipSync;
        private EarmuffVisualizer _earmuffVis;
        
        private static AudioMixerGroup _cachedVoiceMixer;

        // --- BUFFERS ---
        private byte[] _compressedBuffer = new byte[8192]; 
        private byte[] _decompressedBuffer = new byte[65536]; 
        private byte[] _netDecompressBuffer = new byte[65536]; // Buffer for network receive

        // --- RING BUFFER ---
        private Queue<float[]> _audioQueue = new Queue<float[]>();
        private object _queueLock = new object();
        private float[] _readBuffer;
        private int _readPosition = 0;
        private AudioClip _streamingClip;
        private bool _isPlaying = false;
        private int _sampleRate;

        // --- STATE ---
        private bool _isRecording = false;
        private float _lastVolume = 0f;
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;
        private float _lastPacketTime = 0f;
        private bool _audioInitialized = false;

        void Awake()
        {
            AttachedPlayer = GetComponent<Player>();
            if (AttachedPlayer == null) { Destroy(this); return; }

            // Don't initialize audio yet, wait for SteamID
            StartCoroutine(WaitForSteamID());
        }

        private IEnumerator WaitForSteamID()
        {
            // Wait until SteamID is populated
            while (VoiceSystem.GetSteamIDFromPlayer(AttachedPlayer) == 0)
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
            Main.Log.LogInfo($"[VoiceManager] Initialized on {AttachedPlayer._nickname} (ID: {OwnerID})");
            
            CheckLocalPlayerStatus();
        }

        void Start()
        {
            if (_audioInitialized) CheckLocalPlayerStatus();
        }

        void OnDestroy()
        {
            VoiceSystem.ActiveManagers.Remove(this);
            if (IsLocalPlayer && _isRecording)
            {
                SteamUser.StopVoiceRecording();
            }
        }

        // --- SETUP ---

        private void InitializeAudio()
        {
            GameObject emitter = new GameObject("VoiceEmitter");
            emitter.transform.SetParent(transform, false);
            emitter.transform.localPosition = new Vector3(0, 1.7f, 0); 

            _audioSource = emitter.AddComponent<AudioSource>();
            _audioSource.loop = true; 
            _audioSource.playOnAwake = true;
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
            _readBuffer = new float[bufferLen];
            
            _streamingClip = AudioClip.Create($"Voice_{OwnerID}", bufferLen, 1, _sampleRate, false);
            _streamingClip.SetData(_readBuffer, 0);
            _audioSource.clip = _streamingClip;
            _audioSource.Play();
            
            ApplyAudioSettings();
        }

        private void InitializeBubble()
        {
            _bubbleObject = new GameObject("VoiceBubble");
            _bubbleObject.transform.SetParent(transform, false);
            _bubbleObject.transform.localPosition = new Vector3(0, 3.4f, 0); 
            
            _bubbleRenderer = _bubbleObject.AddComponent<SpriteRenderer>();
            _bubbleRenderer.sprite = CreateCircleSprite(); 
            _bubbleRenderer.color = new Color(1f, 0.5f, 0f, 0.9f); 
            _bubbleObject.SetActive(false);
        }

        public void ApplyAudioSettings()
        {
            if (_audioSource == null) return;
            
            // Set base volume to 1.0 (Boost happens in ProcessPCM)
            _audioSource.volume = 1.0f; 
            
            if (IsLocalPlayer) 
            {
                _audioSource.spatialBlend = 0f; 
                // Add Earmuff Visualizer to local player
                if (_earmuffVis == null) _earmuffVis = gameObject.AddComponent<EarmuffVisualizer>();
            }
            else
            {
                _audioSource.spatialBlend = Main.CfgSpatialBlending.Value ? 1.0f : 0.0f;
                _audioSource.minDistance = Main.CfgMinDistance.Value;
                _audioSource.maxDistance = Main.CfgMaxDistance.Value;
                _audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
            }
        }

        void Update()
        {
            if (!Main.CfgEnabled.Value || !_audioInitialized) return;

            if (!IsLocalPlayer) CheckLocalPlayerStatus();

            // Host Requirement Check
            if (!VoiceSystem.IsVoiceAllowedInLobby && !Main.CfgMicTest.Value) return;

            if (IsLocalPlayer) 
            {
                HandleMicInput();
            }

            // Timeout Check
            if (_isPlaying && Time.time - _lastPacketTime > 0.3f)
            {
                _isPlaying = false;
                _lastVolume = 0f;
            }

            ProcessAudioQueue();
            UpdateVisuals();
        }

        private void HandleMicInput()
        {
            bool wantsToTalk = CheckInputKeys();

            if (wantsToTalk && !_isRecording)
            {
                SteamUser.StartVoiceRecording();
                _isRecording = true;
                Main.Log.LogDebug("Mic Recording Started");
            }
            else if (!wantsToTalk && _isRecording)
            {
                SteamUser.StopVoiceRecording();
                _isRecording = false;
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
                            
                            // --- P2P SEND ---
                            CodeTalkerNetwork.SendNetworkPacket(AttachedPlayer, packet, Compressors.CompressionType.GZip, CompressionLevel.Fastest);
                            
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
            if (IsLocalPlayer && !Main.CfgMicTest.Value) return; 

            uint bytesWritten;
            EVoiceResult res = SteamUser.DecompressVoice(
                compressedData, (uint)compressedData.Length,
                _netDecompressBuffer, (uint)_netDecompressBuffer.Length,
                out bytesWritten, (uint)_sampleRate
            );

            if (res == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
            {
                byte[] pcm = new byte[bytesWritten];
                Array.Copy(_netDecompressBuffer, pcm, bytesWritten);
                AddToAudioQueue(pcm, (int)bytesWritten);
                
                if(_lipSync != null) _lipSync.SetSpeaking();
                _lastPacketTime = Time.time; 
                if (!_isPlaying) { _isPlaying = true; }
            }
        }

        private void AddToAudioQueue(byte[] rawBytes, int length)
        {
            int sampleCount = length / 2;
            float[] floatData = new float[sampleCount];
            float gain = Main.CfgMasterVolume.Value; // Volume Boost

            for (int i = 0; i < sampleCount; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                // Apply Gain Here
                float sample = (val / 32768.0f) * gain;
                // Clamp
                if (sample > 1f) sample = 1f;
                if (sample < -1f) sample = -1f;
                
                floatData[i] = sample;
            }

            // Simple visual volume calc
            float maxVol = 0;
            foreach(var f in floatData) if (Mathf.Abs(f) > maxVol) maxVol = Mathf.Abs(f);
            _lastVolume = Mathf.Lerp(_lastVolume, maxVol, Time.deltaTime * 10f);

            lock (_queueLock)
            {
                _audioQueue.Enqueue(floatData);
                if (_audioQueue.Count > 50) _audioQueue.Dequeue(); 
            }
        }

        private void ProcessAudioQueue()
        {
            lock (_queueLock)
            {
                if (_audioQueue.Count > 0)
                {
                    float[] data = _audioQueue.Dequeue();
                    int writeStart = _readPosition;
                    int len = data.Length;
                    int bufferLen = _readBuffer.Length;

                    for (int i = 0; i < len; i++)
                    {
                        _readBuffer[(writeStart + i) % bufferLen] = data[i];
                    }
                    
                    _readPosition = (writeStart + len) % bufferLen;
                    _streamingClip.SetData(_readBuffer, 0);

                    if (!_audioSource.isPlaying) _audioSource.Play();
                }
            }
        }

        private void UpdateVisuals()
        {
            if (_bubbleObject == null) return;
            
            if (!Main.CfgShowHeadIcons.Value) 
            {
                _bubbleObject.SetActive(false);
                return;
            }

            if (_lastVolume > 0.01f)
            {
                _bubbleObject.SetActive(true);
                
                if (Camera.main != null)
                    _bubbleObject.transform.LookAt(Camera.main.transform);

                float scale = 0.5f + (_lastVolume * 0.5f);
                _bubbleObject.transform.localScale = Vector3.one * scale;
                
                _lastVolume = Mathf.Lerp(_lastVolume, 0f, Time.deltaTime * 5f);
            }
            else
            {
                _bubbleObject.SetActive(false);
            }
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
            
            if (Main.CfgMicMode.Value == Main.MicMode.PushToTalk) 
                return Input.GetKey(Main.CfgPushToTalk.Value);
            
            if (Main.CfgMicMode.Value == Main.MicMode.Toggle)
            {
                bool isKeyDown = Input.GetKey(Main.CfgPushToTalk.Value);
                if (isKeyDown && !_wasKeyDown) 
                {
                    _isToggleOn = !_isToggleOn;
                }
                _wasKeyDown = isKeyDown;
                return _isToggleOn;
            }
                
            return false;
        }

        private Sprite CreateCircleSprite()
        {
            int res = 64;
            Texture2D tex = new Texture2D(res, res);
            Color[] cols = new Color[res*res];
            float r = res/2f;
            Vector2 c = new Vector2(r,r);
            for(int y=0;y<res;y++){
                for(int x=0;x<res;x++){
                    float d = Vector2.Distance(new Vector2(x,y), c);
                    cols[y*res+x] = d < r ? Color.white : Color.clear;
                }
            }
            tex.SetPixels(cols); tex.Apply();
            return Sprite.Create(tex, new Rect(0,0,res,res), new Vector2(0.5f,0.5f));
        }
    }

    // -----------------------------------------------------------
    // EARMUFF VISUALIZER
    // -----------------------------------------------------------
    public class EarmuffVisualizer : MonoBehaviour
    {
        private LineRenderer _line;
        private static List<EarmuffVisualizer> _all = new List<EarmuffVisualizer>();

        void Awake()
        {
            _all.Add(this);
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = false;
            _line.loop = true;
            _line.positionCount = 50;
            _line.startWidth = 0.05f;
            _line.endWidth = 0.05f;
            _line.material = new Material(Shader.Find("Sprites/Default"));
            _line.startColor = new Color(1f, 0f, 0f, 0.5f); 
            _line.endColor = new Color(1f, 0f, 0f, 0.5f);
            
            UpdateState();
        }

        void OnDestroy() => _all.Remove(this);

        public void UpdateState()
        {
            if (_line == null) return;
            // Only show if config enabled AND we are in a lobby
            bool show = Main.CfgShowRangeVisual.Value && VoiceSystem.IsVoiceAllowedInLobby;
            _line.enabled = show;
            
            if (show)
            {
                float radius = Main.CfgMaxDistance.Value;
                for (int i = 0; i < 50; i++)
                {
                    float angle = i * (2f * Mathf.PI / 50f);
                    _line.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, 0.1f, Mathf.Sin(angle) * radius));
                }
            }
        }

        public static void UpdateVisuals()
        {
            foreach(var v in _all) v.UpdateState();
        }
    }

    // -----------------------------------------------------------
    // LIP SYNC
    // -----------------------------------------------------------
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
            } catch (Exception e) { Main.Log.LogError($"LipSync error: {e.Message}"); }
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