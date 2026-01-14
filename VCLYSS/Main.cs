using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using CodeTalker.Networking;
using CodeTalker.Packets;
using Steamworks;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Nessie.ATLYSS.EasySettings;

namespace VCLYSS
{
    [BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
    [BepInDependency("CodeTalker")]
    public class Main : BaseUnityPlugin
    {
        public static Main Instance;
        internal static ManualLogSource Log;
        private Harmony _harmony;

        // --- CONFIG ---
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
        public static ConfigEntry<bool> CfgLipSync; 
        public enum MicMode { PushToTalk, Toggle, AlwaysOn }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            InitConfig();
            Settings.OnInitialized.AddListener(AddSettings);
            Settings.OnApplySettings.AddListener(() => { Config.Save(); VoiceRouter.ApplySettingsToAll(); });

            _harmony = new Harmony(ModInfo.GUID);
            _harmony.PatchAll();

            StartCoroutine(RegisterPacketDelayed());

            // We need ONE global object to route packets to the players
            var go = new GameObject("VCLYSS_GlobalRouter");
            DontDestroyOnLoad(go);
            go.AddComponent<VoiceRouter>();
            go.AddComponent<VoiceOverlay>();

            Logger.LogInfo($"[{ModInfo.NAME}] Loaded. Voice Chat Ready.");
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
                // Send the data to the correct player's VoiceManager
                VoiceRouter.Instance.RoutePacket(header.SenderID, voicePkt.VoiceData);
            }
        }

        private void InitConfig()
        {
            CfgEnabled = Config.Bind("1. General", "Voice Chat Active", true, "Master Switch");
            CfgMasterVolume = Config.Bind("1. General", "Master Volume", 1.5f, new ConfigDescription("Incoming Voice Volume", new AcceptableValueRange<float>(0.1f, 5.0f)));
            CfgMuteAll = Config.Bind("1. General", "Mute Everyone (Panic)", false, "Panic button to silence all incoming voice");
            CfgMicMode = Config.Bind("2. Input", "Input Mode", MicMode.PushToTalk, "Input Method");
            CfgPushToTalk = Config.Bind("2. Input", "Push To Talk Key", KeyCode.V, "Bind for PTT/Toggle");
            CfgMicThreshold = Config.Bind("2. Input", "Mic Activation Threshold", 0.05f, new ConfigDescription("Gate threshold", new AcceptableValueRange<float>(0.0f, 0.5f)));
            CfgMicTest = Config.Bind("2. Input", "Mic Test (Loopback)", false, "Hear your own voice to test settings");
            CfgMinDistance = Config.Bind("3. Spatial", "Min Distance", 1.0f, new ConfigDescription("Loud Zone", new AcceptableValueRange<float>(0.1f, 10.0f)));
            CfgMaxDistance = Config.Bind("3. Spatial", "Max Distance", 25.0f, new ConfigDescription("Falloff Zone", new AcceptableValueRange<float>(5.0f, 100.0f)));
            CfgSpatialBlending = Config.Bind("3. Spatial", "3D Spatial Audio", true, "Enable 3D Directional Audio");
            CfgShowOverlay = Config.Bind("4. Visuals", "Show Voice Overlay", true, "Show list of speakers in top right");
            CfgShowHeadIcons = Config.Bind("4. Visuals", "Show Head Icons", true, "Show bubbles above players heads");
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
            tab.AddHeader("Visuals");
            tab.AddToggle(CfgShowOverlay);
            tab.AddToggle(CfgShowHeadIcons);
            tab.AddToggle(CfgLipSync);
        }
    }

    // -----------------------------------------------------------
    // PACKET
    // -----------------------------------------------------------
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
    // GLOBAL ROUTER (Finds the Player, gives them the data)
    // -----------------------------------------------------------
    public class VoiceRouter : MonoBehaviour
    {
        public static VoiceRouter Instance;
        public static List<VoiceManager> ActiveManagers = new List<VoiceManager>();
        private HashSet<ulong> _mutedPlayers = new HashSet<ulong>();

        void Awake() 
        { 
            Instance = this; 
            StartCoroutine(EnsureVoiceComponents());
        }

        // Safety loop to ensure every player gets a VoiceManager
        private IEnumerator EnsureVoiceComponents()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);
                try
                {
                    Player[] allPlayers = FindObjectsOfType<Player>();
                    foreach (var p in allPlayers)
                    {
                        if (p != null && p.GetComponent<VoiceManager>() == null)
                        {
                            p.gameObject.AddComponent<VoiceManager>();
                        }
                    }
                }
                catch { }
            }
        }

        public void RoutePacket(ulong senderID, byte[] data)
        {
            if (!Main.CfgEnabled.Value || Main.CfgMuteAll.Value || _mutedPlayers.Contains(senderID)) return;

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
            // Fallback
            Player[] players = FindObjectsOfType<Player>();
            foreach(var p in players) {
                if (GetSteamIDFromPlayer(p) == steamID) return p.GetComponent<VoiceManager>();
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

    // -----------------------------------------------------------
    // VOICE MANAGER (THE SCRIPT ON THE PLAYER)
    // -----------------------------------------------------------
    public class VoiceManager : MonoBehaviour
    {
        public Player AttachedPlayer;
        public ulong OwnerID;
        public bool IsLocalPlayer = false;

        private AudioSource _audioSource;
        private VoiceIndicator _indicator;
        private LipSync _lipSync;
        
        // Buffers
        private const uint CompressedBufferSize = 8192;
        private const uint DecompressedBufferSize = 22050;
        private byte[] _compressedBuffer = new byte[CompressedBufferSize];
        private byte[] _decompressedBuffer = new byte[DecompressedBufferSize];
        private byte[] _netDecompressBuffer = new byte[DecompressedBufferSize];

        // Ring Buffer Logic
        private Queue<float[]> _audioQueue = new Queue<float[]>();
        private object _queueLock = new object();
        private float[] _readBuffer;
        private int _readPosition = 0;
        private AudioClip _streamingClip;
        private bool _isPlaying = false;
        private float _lastSpeakingTime;

        // Input
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;

        void Awake()
        {
            AttachedPlayer = GetComponent<Player>();
            if (AttachedPlayer == null) 
            { 
                Destroy(this); 
                return; 
            }

            OwnerID = VoiceRouter.GetSteamIDFromPlayer(AttachedPlayer);
            IsLocalPlayer = (AttachedPlayer == Player._mainPlayer);

            VoiceRouter.ActiveManagers.Add(this);
            InitializeAudio();
            
            Main.Log.LogInfo($"VoiceManager attached to {AttachedPlayer._nickname} (Local: {IsLocalPlayer})");
        }

        void OnDestroy()
        {
            VoiceRouter.ActiveManagers.Remove(this);
        }

        private void InitializeAudio()
        {
            GameObject emitter = new GameObject("VoiceEmitter");
            emitter.transform.SetParent(transform, false);
            emitter.transform.localPosition = new Vector3(0, 1.6f, 0);

            _audioSource = emitter.AddComponent<AudioSource>();
            
            // Setup Audio Clip
            int bufferLen = 44100 * 2; 
            _readBuffer = new float[bufferLen];
            _streamingClip = AudioClip.Create($"Voice_{OwnerID}", bufferLen, 1, (int)SteamUser.GetVoiceOptimalSampleRate(), false);
            _streamingClip.SetData(_readBuffer, 0);

            _audioSource.clip = _streamingClip;
            _audioSource.loop = true;
            _audioSource.playOnAwake = true;
            
            ApplyAudioSettings();
            
            _indicator = emitter.AddComponent<VoiceIndicator>();
            _indicator.Initialize();
            
            _lipSync = emitter.AddComponent<LipSync>();
            _lipSync.Initialize(AttachedPlayer);
        }

        public void ApplyAudioSettings()
        {
            if (_audioSource == null) return;
            
            _audioSource.volume = Main.CfgMasterVolume.Value;
            
            if (IsLocalPlayer)
            {
                _audioSource.spatialBlend = 0f; // 2D for self/loopback
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
            if (!Main.CfgEnabled.Value) return;

            // If this is ME, check my mic and send data
            if (IsLocalPlayer) HandleMicInput();

            // Always process audio queue (play sound)
            ProcessAudioQueue();
        }

        private void HandleMicInput()
        {
            bool isTransmitting = CheckInputKeys();

            if (isTransmitting)
            {
                while (true)
                {
                    uint available;
                    SteamUser.GetAvailableVoice(out available);
                    if (available == 0) break;

                    uint bytesWritten;
                    EVoiceResult result = SteamUser.GetVoice(true, _compressedBuffer, CompressedBufferSize, out bytesWritten);

                    if (result == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
                    {
                        byte[] chunk = new byte[bytesWritten];
                        Array.Copy(_compressedBuffer, chunk, bytesWritten);

                        if (Main.CfgMicTest.Value)
                        {
                            ProcessLoopback(chunk);
                        }
                        else
                        {
                            // Send to network
                            var packet = new VoicePacket(chunk);
                            CodeTalkerNetwork.SendNetworkPacket(AttachedPlayer, packet);
                            SetSpeaking();
                        }
                    }
                    else break;
                }
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

        // Called by VoiceRouter when data arrives for THIS player
        public void ReceiveNetworkData(byte[] data)
        {
            if (IsLocalPlayer) return; // Ignore own packets

            uint bytesWritten;
            EVoiceResult result = SteamUser.DecompressVoice(
                data, (uint)data.Length,
                _netDecompressBuffer, (uint)_netDecompressBuffer.Length,
                out bytesWritten, SteamUser.GetVoiceOptimalSampleRate()
            );

            if (result == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
            {
                byte[] pcm = new byte[bytesWritten];
                Array.Copy(_netDecompressBuffer, pcm, bytesWritten);
                
                if (CalculateAmplitude(pcm, (int)bytesWritten) >= Main.CfgMicThreshold.Value)
                {
                    AddToAudioQueue(pcm, (int)bytesWritten);
                    SetSpeaking();
                }
            }
        }

        private void ProcessLoopback(byte[] compressedData)
        {
            uint bytesWritten;
            SteamUser.DecompressVoice(compressedData, (uint)compressedData.Length, _decompressedBuffer, (uint)_decompressedBuffer.Length, out bytesWritten, SteamUser.GetVoiceOptimalSampleRate());
            
            if (bytesWritten > 0)
            {
                byte[] pcm = new byte[bytesWritten];
                Array.Copy(_decompressedBuffer, pcm, bytesWritten);
                AddToAudioQueue(pcm, (int)bytesWritten);
                SetSpeaking();
                
                if (!_audioSource.isPlaying) _audioSource.Play();
                int dist = (_readPosition - _audioSource.timeSamples + _readBuffer.Length) % _readBuffer.Length;
                if (dist > 12000) _audioSource.timeSamples = (_readPosition - 2000 + _readBuffer.Length) % _readBuffer.Length;
            }
        }

        private void AddToAudioQueue(byte[] rawBytes, int length)
        {
            int sampleCount = length / 2;
            float[] floatData = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                floatData[i] = val / 32768.0f;
            }

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

                    if (!_isPlaying)
                    {
                        _audioSource.Play();
                        _isPlaying = true;
                    }
                }
            }
        }

        private void SetSpeaking()
        {
            _lastSpeakingTime = Time.time;
            if (_indicator != null) _indicator.SetSpeaking();
            if (_lipSync != null) _lipSync.SetSpeaking();
        }

        public bool IsSpeaking() => (Time.time - _lastSpeakingTime < 0.25f);

        private float CalculateAmplitude(byte[] pcmData, int length)
        {
            float sum = 0;
            int count = length / 2;
            for (int i = 0; i < count; i++) sum += Mathf.Abs(BitConverter.ToInt16(pcmData, i * 2) / 32768.0f);
            return count == 0 ? 0 : sum / count;
        }
    }

    // -----------------------------------------------------------
    // HARMONY: ATTACH VOICE MANAGER TO PLAYERS
    // -----------------------------------------------------------
    [HarmonyPatch(typeof(Player), "Start")]
    public class PlayerStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance.GetComponent<VoiceManager>() == null)
            {
                __instance.gameObject.AddComponent<VoiceManager>();
            }
        }
    }

    [HarmonyPatch]
    public class LobbyPatches
    {
        private const string LOBBY_KEY = "vclyss_enabled";

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
                SteamMatchmaking.SetLobbyData(currentLobby, LOBBY_KEY, "true");
                SteamUser.StartVoiceRecording();
                Main.Log.LogInfo("Host Lobby Created: Voice Chat ENABLED.");
            }
        }

        private static IEnumerator CheckLobbyStatus(CSteamID lobbyId)
        {
            yield return new WaitForSeconds(2.0f);
            string value = SteamMatchmaking.GetLobbyData(lobbyId, LOBBY_KEY);

            if (!string.IsNullOrEmpty(value) && value == "true" && Main.CfgEnabled.Value)
            {
                SteamUser.StartVoiceRecording();
                Main.Log.LogInfo($"Joined Modded Lobby: Voice Chat ENABLED.");
            }
            else
            {
                SteamUser.StopVoiceRecording();
                Main.Log.LogInfo("Voice Chat DISABLED.");
            }
        }
    }

    // -----------------------------------------------------------
    // VISUALS
    // -----------------------------------------------------------
    public class LipSync : MonoBehaviour
    {
        private float _lastTalkTime;
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
            _lastTalkTime = Time.time;
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

    public class VoiceIndicator : MonoBehaviour
    {
        private GameObject _iconObject;
        private SpriteRenderer _renderer;
        private float _lastTalkTime;
        private Transform _cameraTransform;
        private static Sprite _cachedSprite;

        public void Initialize()
        {
            try {
                _iconObject = new GameObject("VoiceIcon");
                _iconObject.transform.SetParent(this.transform, false);
                _iconObject.transform.localPosition = new Vector3(0, 3.14f, 0); 
                _iconObject.transform.localScale = Vector3.one * 0.5f;
                _renderer = _iconObject.AddComponent<SpriteRenderer>();
                if (_cachedSprite == null) _cachedSprite = CreateBubbleSprite();
                _renderer.sprite = _cachedSprite;
                _renderer.color = new Color(1f, 0.6f, 0f, 0.9f);
                _renderer.sortingLayerName = "Default";
                _renderer.sortingOrder = 100;
                _renderer.enabled = false;
            } catch (Exception e) { Main.Log.LogError($"Indicator error: {e.Message}"); }
        }

        public void SetSpeaking() { if (Main.CfgShowHeadIcons.Value) _lastTalkTime = Time.time; }

        private void Update()
        {
            if (!Main.CfgShowHeadIcons.Value) { if (_renderer != null) _renderer.enabled = false; return; }
            bool isTalking = (Time.time - _lastTalkTime < 0.25f);
            if (_renderer != null) {
                _renderer.enabled = isTalking;
                if (isTalking) {
                    if (_cameraTransform == null) { if (Camera.main != null) _cameraTransform = Camera.main.transform; else return; }
                    _iconObject.transform.LookAt(transform.position + _cameraTransform.rotation * Vector3.forward, _cameraTransform.rotation * Vector3.up);
                }
            }
        }

        private Sprite CreateBubbleSprite()
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            Color white = new Color(1f, 1f, 1f, 0.95f);
            Color outline = new Color(0.15f, 0.15f, 0.15f, 0.9f);
            Color clear = Color.clear;
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f - 4;
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    float dist = Vector2.Distance(new Vector2(x, y), center);
                    if (dist < radius - 2f) pixels[y * size + x] = white;
                    else if (dist < radius) pixels[y * size + x] = outline;
                    else pixels[y * size + x] = clear;
                }
            }
            tex.SetPixels(pixels); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
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
            foreach (var vm in VoiceRouter.ActiveManagers)
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