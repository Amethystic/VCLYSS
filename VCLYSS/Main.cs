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
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
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
            Settings.OnApplySettings.AddListener(() => { Config.Save(); UpdateAudioSettings(); });

            _harmony = new Harmony(ModInfo.GUID);
            _harmony.PatchAll();

            // Delay registration to ensure CodeTalker is ready
            StartCoroutine(RegisterPacketDelayed());

            var go = new GameObject("VCLYSS_Manager");
            DontDestroyOnLoad(go);
            go.AddComponent<VoiceManager>();
            go.AddComponent<VoiceOverlay>();

            Logger.LogInfo($"[{ModInfo.NAME}] Loaded. Voice Chat Ready.");
        }

        private IEnumerator RegisterPacketDelayed()
        {
            yield return new WaitForSeconds(2f);
            CodeTalkerNetwork.RegisterBinaryListener<VoicePacket>(OnVoicePacketReceived);
            Main.Log.LogInfo("Voice packet listener registered");
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

        private void UpdateAudioSettings()
        {
            if (VoiceManager.Instance != null)
            {
                VoiceManager.Instance.ApplySettingsToAll();
            }
        }

        private void OnVoicePacketReceived(PacketHeader header, BinaryPacketBase packet)
        {
            Main.Log.LogInfo($"VOICE PACKET RECEIVED from {header.SenderID}");
            
            if (packet is VoicePacket voicePkt)
            {
                Main.Log.LogInfo($"Voice packet received, data length: {voicePkt.VoiceData?.Length ?? 0}");
                VoiceManager.Instance.ReceiveVoiceData(header.SenderID, voicePkt.VoiceData);
            }
        }
    }

    // SIMPLIFIED VoicePacket - Let CodeTalker handle serialization
    public class VoicePacket : BinaryPacketBase
    {
        public override string PacketSignature => "VCLYSS_VOICE";
        public byte[] VoiceData;

        public VoicePacket() { }
        public VoicePacket(byte[] data)
        {
            VoiceData = data;
        }
        
        public override byte[] Serialize()
        {
            // Simple implementation - CodeTalker expects this
            try
            {
                if (VoiceData == null)
                    return new byte[0];
                
                return VoiceData;
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error in Serialize: {e.Message}");
                return new byte[0];
            }
        }

        public override void Deserialize(byte[] data)
        {
            try
            {
                VoiceData = data;
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error in Deserialize: {e.Message}");
                VoiceData = new byte[0];
            }
        }
    }

    // -----------------------------------------------------------
    // LIP SYNC COMPONENT - FIXED USING PUBLIC METHODS
    // -----------------------------------------------------------
    public class LipSync : MonoBehaviour
    {
        private ulong _playerSteamID;
        private float _lastTalkTime;
        private PlayerRaceModel _playerRaceModel;
        private bool _initialized = false;
        private Coroutine _mouthResetCoroutine;
        
        // Define the enum types from the decompiled code
        public enum MouthCondition { Closed = 0, Open = 1 }
        public enum EyeCondition { Center = 0, Up = 1, Down = 2, Left = 3, Right = 4, Hurt = 5, Pissed = 6, Closed = 7 }

        public void Initialize(Player player)
        {
            try
            {
                if (player == null)
                {
                    Main.Log.LogWarning("LipSync: Player is null");
                    return;
                }

                // Try to find PlayerRaceModel in parent hierarchy
                _playerRaceModel = player.GetComponentInChildren<PlayerRaceModel>(true);
                
                if (_playerRaceModel == null && player._pVisual != null)
                {
                    _playerRaceModel = player._pVisual._playerRaceModel;
                }

                if (_playerRaceModel == null)
                {
                    Main.Log.LogWarning($"LipSync: Could not find PlayerRaceModel for player {player._nickname}");
                    return;
                }

                if (!string.IsNullOrEmpty(player._steamID) && ulong.TryParse(player._steamID, out ulong steamID))
                {
                    _playerSteamID = steamID;
                }

                _initialized = true;
                Main.Log.LogInfo($"LipSync initialized for player {player._nickname} (SteamID: {_playerSteamID})");
            }
            catch (Exception e)
            {
                Main.Log.LogError($"LipSync initialization error: {e.Message}");
            }
        }

        public void SetSpeaking()
        {
            if (!Main.CfgLipSync.Value || !_initialized || _playerRaceModel == null) return;
            
            _lastTalkTime = Time.time;
            
            try
            {
                // Use the public method Set_MouthCondition instead of reflection!
                _playerRaceModel.Set_MouthCondition((global::MouthCondition)MouthCondition.Open, 0.15f); // Open for 0.15 seconds
                
                Main.Log.LogInfo($"Set mouth to Open for player {_playerSteamID}");
                
                // Cancel any existing reset coroutine
                if (_mouthResetCoroutine != null)
                {
                    StopCoroutine(_mouthResetCoroutine);
                }
                
                // Start a coroutine to reset mouth after talking stops
                _mouthResetCoroutine = StartCoroutine(ResetMouthAfterDelay());
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error setting lip sync: {e.Message}");
            }
        }

        private IEnumerator ResetMouthAfterDelay()
        {
            yield return new WaitForSeconds(0.2f); // Wait a bit longer than the talk time
            
            if (_playerRaceModel != null)
            {
                try
                {
                    _playerRaceModel.Set_MouthCondition(global::MouthCondition.Closed, 0f); // Closed
                }
                catch (Exception e)
                {
                    Main.Log.LogError($"Error resetting mouth: {e.Message}");
                }
            }
        }

        private void Update()
        {
            // Backup update logic in case coroutine fails
            if (!Main.CfgLipSync.Value || !_initialized || _playerRaceModel == null) return;

            bool isTalking = (Time.time - _lastTalkTime < 0.2f);

            if (!isTalking)
            {
                // Double-check mouth is closed
                try
                {
                    // Get the current mouth condition via reflection as backup
                    var mouthConditionField = typeof(PlayerRaceModel).GetField("_currentMouthCondition", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (mouthConditionField != null)
                    {
                        var currentMouth = (MouthCondition)mouthConditionField.GetValue(_playerRaceModel);
                        if (currentMouth != MouthCondition.Closed)
                        {
                            _playerRaceModel.Set_MouthCondition(global::MouthCondition.Closed, 0f); // Fixed: Should be Closed
                        }
                    }
                }
                catch (Exception e)
                {
                    // Silent fail - this is just backup logic
                }
            }
        }

        private void OnDestroy()
        {
            if (_mouthResetCoroutine != null)
            {
                StopCoroutine(_mouthResetCoroutine);
            }
        }
    }

    // -----------------------------------------------------------
    // VOICE INDICATOR (Visual Bubble)
    // -----------------------------------------------------------
    public class VoiceIndicator : MonoBehaviour
    {
        private GameObject _iconObject;
        private SpriteRenderer _renderer;
        private float _lastTalkTime;
        private Transform _cameraTransform;
        private static Sprite _cachedSprite;

        public void Initialize()
        {
            try
            {
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
                
                Main.Log.LogInfo($"Voice indicator initialized");
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Voice indicator initialization error: {e.Message}");
            }
        }

        public void SetSpeaking()
        {
            if (!Main.CfgShowHeadIcons.Value) return;
            _lastTalkTime = Time.time;
        }

        private void Update()
        {
            if (!Main.CfgShowHeadIcons.Value)
            {
                if (_renderer != null) _renderer.enabled = false;
                return;
            }

            bool isTalking = (Time.time - _lastTalkTime < 0.25f);
            
            if (_renderer != null) 
            {
                _renderer.enabled = isTalking;

                if (isTalking)
                {
                    if (_cameraTransform == null)
                    {
                        if (Camera.main != null) _cameraTransform = Camera.main.transform;
                        else return;
                    }
                    _iconObject.transform.LookAt(
                        transform.position + _cameraTransform.rotation * Vector3.forward, 
                        _cameraTransform.rotation * Vector3.up
                    );
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
            float outlineWidth = 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center);
                    
                    if (dist < radius - outlineWidth)
                    {
                        pixels[y * size + x] = white;
                    }
                    else if (dist < radius)
                    {
                        pixels[y * size + x] = outline;
                    }
                    else if (dist < radius + 1)
                    {
                        float alpha = 1f - (dist - radius);
                        pixels[y * size + x] = new Color(0.15f, 0.15f, 0.15f, alpha * 0.6f);
                    }
                    else
                    {
                        pixels[y * size + x] = clear;
                    }
                }
            }
            
            tex.SetPixels(pixels);
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }

    // -----------------------------------------------------------
    // VOICE OVERLAY (Top Right)
    // -----------------------------------------------------------
    public class VoiceOverlay : MonoBehaviour
    {
        private GUIStyle _style;
        private GUIStyle _boxStyle;

        private void OnGUI()
        {
            if (!Main.CfgShowOverlay.Value || VoiceManager.Instance == null) return;

            if (VoiceManager.Instance.IsLoopbackActive())
            {
                var warningStyle = new GUIStyle { 
                    fontSize = 20, 
                    alignment = TextAnchor.MiddleRight, 
                    fontStyle = FontStyle.Bold 
                };
                warningStyle.normal.textColor = Color.yellow;
                
                GUI.Label(new Rect(Screen.width - 320, 100, 300, 40), 
                    "MIC TEST ACTIVE", warningStyle);
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 20; 
                _style.normal.textColor = Color.white;
                _style.alignment = TextAnchor.MiddleRight;
                _style.fontStyle = FontStyle.Bold;

                _boxStyle = new GUIStyle(GUI.skin.box);
                _boxStyle.normal.background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.5f));
            }

            var speakers = VoiceManager.Instance.GetActiveSpeakers();
            if (speakers.Count == 0) return;

            float width = 250f;
            float heightPerLine = 30f;
            float totalHeight = speakers.Count * heightPerLine;
            
            float xPos = Screen.width - width - 20f;
            float yPos = 20f; 

            GUILayout.BeginArea(new Rect(xPos, yPos, width, totalHeight));
            foreach (var name in speakers)
            {
                GUILayout.Box(name, _style, GUILayout.Height(heightPerLine));
            }
            GUILayout.EndArea();
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; ++i) pix[i] = col;
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }
    }

    // -----------------------------------------------------------
    // VOICE MANAGER - SIMPLIFIED VERSION
    // -----------------------------------------------------------
    public class VoiceManager : MonoBehaviour
    {
        public static VoiceManager Instance;

        private const uint CompressedBufferSize = 8192;
        private const uint DecompressedBufferSize = 22050;

        private byte[] _compressedBuffer = new byte[CompressedBufferSize];
        private byte[] _decompressedBuffer = new byte[DecompressedBufferSize];
        private byte[] _networkDecompressBuffer = new byte[DecompressedBufferSize];

        private List<byte> _outgoingBuffer = new List<byte>();
        private const int MAX_PACKET_SIZE = 1200;
        private const int IDEAL_BATCH_SIZE = 512;

        private class PlayerAudioStream
        {
            public AudioSource source;
            public AudioClip streamClip;
            public Queue<float[]> audioQueue = new Queue<float[]>();
            public object queueLock = new object();
            public int bufferSize = 44100 * 2;
            public bool isPlaying = false;
            public float[] readBuffer;
            public int readPosition = 0;
            public float volume = 1.0f;
            public GameObject voiceObject;
        }

        private Dictionary<ulong, PlayerAudioStream> _playerAudioStreams = new Dictionary<ulong, PlayerAudioStream>();
        private Dictionary<ulong, float> _speakingTimers = new Dictionary<ulong, float>(); 
        private Dictionary<ulong, VoiceIndicator> _playerIndicators = new Dictionary<ulong, VoiceIndicator>();
        private Dictionary<ulong, LipSync> _playerLipSyncs = new Dictionary<ulong, LipSync>(); 
        private HashSet<ulong> _mutedPlayers = new HashSet<ulong>();
        
        private PlayerAudioStream _localLoopbackStream;
        private AudioSource _localLoopbackSource;

        public bool IsVoiceEnabled { get; set; } = false;
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;

        void Awake() 
        { 
            Instance = this;
            
            // Initialize loopback for mic test
            _localLoopbackStream = new PlayerAudioStream();
            _localLoopbackStream.bufferSize = (int)DecompressedBufferSize * 10;
            SetupAudioStream(_localLoopbackStream, "VCLYSS_Loopback");
            
            _localLoopbackSource = gameObject.AddComponent<AudioSource>();
            _localLoopbackSource.spatialBlend = 0f;
            _localLoopbackSource.playOnAwake = true;
            _localLoopbackSource.clip = _localLoopbackStream.streamClip;
            _localLoopbackSource.loop = true;
            _localLoopbackSource.volume = Main.CfgMasterVolume.Value;
            _localLoopbackSource.Play();
            
            Main.Log.LogInfo($"VoiceManager initialized");
        }

        void Start()
        {
            // Debug audio system
            InvokeRepeating("DebugAudioSystem", 5f, 10f);
        }

        private void SetupAudioStream(PlayerAudioStream stream, string name)
        {
            try
            {
                stream.streamClip = AudioClip.Create(
                    name,
                    stream.bufferSize,
                    1,
                    (int)GetOptimalSampleRate(),
                    false
                );
                
                stream.readBuffer = new float[stream.bufferSize];
                for (int i = 0; i < stream.readBuffer.Length; i++)
                {
                    stream.readBuffer[i] = 0f;
                }
                
                stream.streamClip.SetData(stream.readBuffer, 0);
                
                Main.Log.LogInfo($"Created audio clip: {name} with {stream.bufferSize} samples, sample rate: {GetOptimalSampleRate()}");
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error setting up audio stream: {e.Message}");
            }
        }

        private void UpdateAudioStream(PlayerAudioStream stream)
        {
            if (stream == null || stream.source == null) 
            {
                return;
            }
            
            lock (stream.queueLock)
            {
                if (stream.audioQueue.Count > 0)
                {
                    float[] audioData = stream.audioQueue.Dequeue();
                    
                    int writeStart = stream.readPosition;
                    int samplesToWrite = Mathf.Min(audioData.Length, stream.readBuffer.Length - writeStart);
                    
                    Array.Copy(audioData, 0, stream.readBuffer, writeStart, samplesToWrite);
                    
                    if (samplesToWrite < audioData.Length)
                    {
                        int remaining = audioData.Length - samplesToWrite;
                        Array.Copy(audioData, samplesToWrite, stream.readBuffer, 0, remaining);
                        stream.readPosition = remaining;
                    }
                    else
                    {
                        stream.readPosition = (writeStart + samplesToWrite) % stream.readBuffer.Length;
                    }
                    
                    stream.streamClip.SetData(stream.readBuffer, 0);
                    
                    if (!stream.isPlaying)
                    {
                        stream.source.Play();
                        stream.isPlaying = true;
                    }
                }
            }
        }

        void Update()
        {
            if (!Main.CfgEnabled.Value) return;
            if (!IsVoiceEnabled || !SteamManager.Initialized) return;

            bool isTransmitting = false;

            if (Main.CfgMicTest.Value) 
            {
                isTransmitting = true;
            }
            else
            {
                if (Main.CfgMicMode.Value == Main.MicMode.AlwaysOn) isTransmitting = true;
                else if (Main.CfgMicMode.Value == Main.MicMode.PushToTalk) isTransmitting = Input.GetKey(Main.CfgPushToTalk.Value);
                else if (Main.CfgMicMode.Value == Main.MicMode.Toggle)
                {
                    bool isKeyDown = Input.GetKey(Main.CfgPushToTalk.Value);
                    if (isKeyDown && !_wasKeyDown) _isToggleOn = !_isToggleOn;
                    _wasKeyDown = isKeyDown;
                    isTransmitting = _isToggleOn;
                }
            }

            if (isTransmitting)
            {
                uint availableSize;
                SteamUser.GetAvailableVoice(out availableSize);

                if (availableSize > 0)
                {
                    uint bytesWritten;
                    EVoiceResult result = SteamUser.GetVoice(true, _compressedBuffer, CompressedBufferSize, out bytesWritten);

                    if (result == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
                    {
                        if (_outgoingBuffer.Count + bytesWritten >= MAX_PACKET_SIZE) 
                        {
                            FlushOutbox();
                        }

                        byte[] chunk = new byte[bytesWritten];
                        Array.Copy(_compressedBuffer, chunk, bytesWritten);
                        _outgoingBuffer.AddRange(chunk);
                        
                        ShowMyIcon(); 
                    }
                }
            }
            else
            {
                if (_outgoingBuffer.Count > 0) 
                {
                    FlushOutbox();
                }
            }

            if (_outgoingBuffer.Count >= IDEAL_BATCH_SIZE) 
            {
                FlushOutbox();
            }
            
            // Update all audio streams
            if (Main.CfgMicTest.Value)
            {
                UpdateAudioStream(_localLoopbackStream);
            }
            
            foreach (var stream in _playerAudioStreams.Values.ToList())
            {
                UpdateAudioStream(stream);
            }
        }

        private void FlushOutbox()
        {
            if (_outgoingBuffer.Count == 0) return;

            byte[] dataToSend = _outgoingBuffer.ToArray();
            _outgoingBuffer.Clear();

            Main.Log.LogInfo($"Flushing outbox: {dataToSend.Length} bytes");

            if (Main.CfgMicTest.Value)
            {
                // Test decompression locally
                uint loopbackBytes;
                EVoiceResult result = SteamUser.DecompressVoice(
                    dataToSend, (uint)dataToSend.Length, 
                    _decompressedBuffer, (uint)_decompressedBuffer.Length, 
                    out loopbackBytes, GetOptimalSampleRate()
                );
                
                Main.Log.LogInfo($"Loopback: Result={result}, Bytes={loopbackBytes}");
                
                if (result == EVoiceResult.k_EVoiceResultOK && loopbackBytes > 0)
                {
                    QueueAudioData(_localLoopbackStream, _decompressedBuffer, (int)loopbackBytes);
                    
                    // Force sync logic for loopback
                    if (_localLoopbackSource != null)
                    {
                        if (!_localLoopbackSource.isPlaying) _localLoopbackSource.Play();

                        int writePos = _localLoopbackStream.readPosition;
                        int playPos = _localLoopbackSource.timeSamples;
                        int bufferLen = _localLoopbackStream.bufferSize;

                        // Calculate distance: how far behind is the playhead?
                        int dist = (writePos - playPos + bufferLen) % bufferLen;
                        
                        // If the playhead is more than ~0.25s behind (assuming 48k sample rate, 12000 samples)
                        if (dist > 12000) 
                        {
                            // Move playhead to 2000 samples behind write position
                            int newPos = writePos - 2000; 
                            if (newPos < 0) newPos += bufferLen;
                            
                            _localLoopbackSource.timeSamples = newPos;
                        }
                    }
                }
                else
                {
                    Main.Log.LogWarning($"Loopback decompress failed: {result}");
                }
                
                TrackTalking(SteamUser.GetSteamID().m_SteamID);
            }
            else
            {
                // Send the raw voice data - CodeTalker will handle serialization
                var packet = new VoicePacket(dataToSend);
                CodeTalkerNetwork.SendBinaryNetworkPacket(packet);
                
                TrackTalking(SteamUser.GetSteamID().m_SteamID);
            }
        }

        private void QueueAudioData(PlayerAudioStream stream, byte[] rawBytes, int length)
        {
            if (length <= 0) return;

            int sampleCount = length / 2;
            if (sampleCount == 0) return;
            
            float[] floatData = new float[sampleCount];
            bool hasAudio = false;
            
            for (int i = 0; i < sampleCount; i++)
            {
                int byteIndex = i * 2;
                if (byteIndex + 1 < length)
                {
                    short val = BitConverter.ToInt16(rawBytes, byteIndex);
                    floatData[i] = val / 32768.0f;
                    if (Mathf.Abs(floatData[i]) > 0.01f) hasAudio = true;
                }
                else
                {
                    floatData[i] = 0f;
                }
            }

            if (hasAudio)
            {
                lock (stream.queueLock)
                {
                    stream.audioQueue.Enqueue(floatData);
                    
                    // Limit queue size
                    while (stream.audioQueue.Count > 50)
                    {
                        stream.audioQueue.Dequeue();
                    }
                }
            }
        }

        public void TrackTalking(ulong steamID)
        {
            _speakingTimers[steamID] = Time.time;
            
            // Update lip sync for this player
            if (_playerLipSyncs.TryGetValue(steamID, out var lipSync))
            {
                lipSync.SetSpeaking();
            }
        }

        public bool IsLoopbackActive() => Main.CfgMicTest.Value;

        public List<string> GetActiveSpeakers()
        {
            List<string> activeNames = new List<string>();
            List<ulong> staleKeys = new List<ulong>();

            foreach(var kvp in _speakingTimers)
            {
                if (Time.time - kvp.Value < 0.25f)
                {
                    activeNames.Add(GetPlayerName(kvp.Key));
                }
                else if (Time.time - kvp.Value > 5.0f) staleKeys.Add(kvp.Key);
            }
            foreach(var key in staleKeys) _speakingTimers.Remove(key);
            return activeNames;
        }

        private string GetPlayerName(ulong steamID)
        {
            if (steamID == SteamUser.GetSteamID().m_SteamID) 
                return Player._mainPlayer != null ? Player._mainPlayer._nickname : "Me";

            GameObject obj = FindPlayerObject(steamID);
            if (obj != null)
            {
                Player p = obj.GetComponent<Player>();
                if (p != null) return string.IsNullOrEmpty(p._nickname) ? p._globalNickname : p._nickname;
            }
            return $"Unknown ({steamID})";
        }

        public void ApplySettingsToAll()
        {
            foreach(var stream in _playerAudioStreams.Values)
            {
                if(stream.source != null) UpdateSourceSettings(stream.source);
            }
            if (_localLoopbackSource != null)
            {
                UpdateSourceSettings(_localLoopbackSource);
            }
        }

        private void UpdateSourceSettings(AudioSource source)
        {
            source.volume = Main.CfgMasterVolume.Value;
            source.spatialBlend = Main.CfgSpatialBlending.Value ? 1.0f : 0.0f; 
            source.minDistance = Main.CfgMinDistance.Value;
            source.maxDistance = Main.CfgMaxDistance.Value;
            source.rolloffMode = AudioRolloffMode.Logarithmic; 
        }

        public void MutePlayer(ulong steamID)
        {
            if (!_mutedPlayers.Contains(steamID))
            {
                _mutedPlayers.Add(steamID);
                Main.Log.LogInfo($"Muted player {steamID}");
                if (_playerAudioStreams.TryGetValue(steamID, out var stream) && stream.source != null) 
                    stream.source.Stop();
            }
        }

        public void UnmutePlayer(ulong steamID)
        {
            if (_mutedPlayers.Contains(steamID)) _mutedPlayers.Remove(steamID);
        }

        public bool IsPlayerMuted(ulong steamID) => _mutedPlayers.Contains(steamID);

        public void ReceiveVoiceData(ulong senderSteamID, byte[] data)
        {
            if (!Main.CfgEnabled.Value || Main.CfgMuteAll.Value || _mutedPlayers.Contains(senderSteamID)) return;
            if (!Main.CfgMicTest.Value && senderSteamID == SteamUser.GetSteamID().m_SteamID) return;
    
            Main.Log.LogInfo($"Received voice data from {senderSteamID}, length: {data.Length}");
            
            if (data.Length == 0)
            {
                Main.Log.LogWarning($"Empty voice data from {senderSteamID}");
                return;
            }
            
            // Decompress the Steam voice data
            uint bytesWritten;
            EVoiceResult result = SteamUser.DecompressVoice(
                data, (uint)data.Length,
                _networkDecompressBuffer, (uint)_networkDecompressBuffer.Length,
                out bytesWritten, GetOptimalSampleRate()
            );

            Main.Log.LogInfo($"DecompressVoice result: {result}, bytesWritten: {bytesWritten}");
    
            if (result != EVoiceResult.k_EVoiceResultOK || bytesWritten == 0)
            {
                Main.Log.LogWarning($"Failed to decompress voice data from {senderSteamID}: {result}");
                return;
            }
    
            byte[] audioDataToPlay = new byte[bytesWritten];
            Array.Copy(_networkDecompressBuffer, 0, audioDataToPlay, 0, (int)bytesWritten);

            float amplitude = CalculateAmplitude(audioDataToPlay, (int)bytesWritten);
            Main.Log.LogInfo($"Audio amplitude: {amplitude}, threshold: {Main.CfgMicThreshold.Value}");
    
            if (amplitude >= Main.CfgMicThreshold.Value)
            {
                PlayerAudioStream stream = GetAudioStream(senderSteamID);
                if (stream != null)
                {
                    QueueAudioData(stream, audioDataToPlay, (int)bytesWritten);
            
                    // Update visual indicators
                    if (_playerIndicators.TryGetValue(senderSteamID, out var indicator)) 
                        indicator.SetSpeaking();
            
                    TrackTalking(senderSteamID);
                }
            }
        }

        private float CalculateAmplitude(byte[] pcmData, int length)
        {
            float sum = 0;
            int sampleCount = length / 2;
            if (sampleCount == 0) return 0f;
            
            for (int i = 0; i < sampleCount; i++)
            {
                int byteIndex = i * 2;
                if (byteIndex + 1 < length)
                {
                    short val = BitConverter.ToInt16(pcmData, byteIndex);
                    sum += Mathf.Abs(val / 32768.0f);
                }
            }
            return sum / sampleCount;
        }

        private PlayerAudioStream GetAudioStream(ulong steamID)
        {
            if (_playerAudioStreams.TryGetValue(steamID, out PlayerAudioStream existing))
            {
                if (existing.source == null || existing.voiceObject == null) 
                { 
                    // Clean up broken stream
                    _playerAudioStreams.Remove(steamID);
                    _playerIndicators.Remove(steamID);
                    _playerLipSyncs.Remove(steamID);
                    
                    return CreateNewAudioStream(steamID);
                }
                else 
                {
                    return existing;
                }
            }

            return CreateNewAudioStream(steamID);
        }

        private PlayerAudioStream CreateNewAudioStream(ulong steamID)
        {
            GameObject playerObj = FindPlayerObject(steamID);
            if (playerObj == null)
            {
                Main.Log.LogError($"Could not find player object for {steamID}");
                return null;
            }

            try
            {
                Player player = playerObj.GetComponent<Player>();
                if (player == null)
                {
                    Main.Log.LogError($"Player component not found for {steamID}");
                    return null;
                }

                // Create voice object
                GameObject voiceObj = new GameObject($"VCLYSS_Emitter_{steamID}");
                voiceObj.transform.SetParent(playerObj.transform, false);
                voiceObj.transform.localPosition = new Vector3(0, 1.6f, 0);

                // Create audio stream
                var stream = new PlayerAudioStream();
                stream.bufferSize = (int)DecompressedBufferSize * 10;
                SetupAudioStream(stream, $"VCLYSS_Voice_{steamID}");
                stream.voiceObject = voiceObj;
                
                // Create audio source
                var source = voiceObj.AddComponent<AudioSource>();
                source.clip = stream.streamClip;
                source.loop = true;
                source.playOnAwake = true;
                stream.source = source;
                stream.isPlaying = false;
                
                // Apply settings
                UpdateSourceSettings(source);
                _playerAudioStreams[steamID] = stream;

                // Create voice indicator
                var indicator = voiceObj.AddComponent<VoiceIndicator>();
                indicator.Initialize();
                _playerIndicators[steamID] = indicator;

                // Create lip sync
                var lipSync = voiceObj.AddComponent<LipSync>();
                lipSync.Initialize(player);
                _playerLipSyncs[steamID] = lipSync;

                return stream;
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error creating audio stream for {steamID}: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        private GameObject FindPlayerObject(ulong targetID)
        {
            try
            {
                Player[] allPlayers = UnityEngine.Object.FindObjectsOfType<Player>();
                
                foreach (Player p in allPlayers)
                {
                    // Try direct string comparison first
                    if (p._steamID == targetID.ToString())
                    {
                        return p.gameObject;
                    }
                    
                    // Try parsing
                    if (!string.IsNullOrEmpty(p._steamID) && ulong.TryParse(p._steamID, out ulong pSteamID))
                    {
                        if (pSteamID == targetID) 
                        {
                            return p.gameObject;
                        }
                    }
                }
                
                return null;
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Error finding player object: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        private void ShowMyIcon()
        {
            if (Player._mainPlayer == null) return;
            ulong myID = SteamUser.GetSteamID().m_SteamID;
            
            if (!_playerIndicators.ContainsKey(myID)) 
                CreateLocalVoiceObject(Player._mainPlayer);
            
            if (_playerIndicators.TryGetValue(myID, out var ind)) ind.SetSpeaking();
            if (_playerLipSyncs.TryGetValue(myID, out var lip)) lip.SetSpeaking(); 
        }

        private void CreateLocalVoiceObject(Player p)
        {
            if (p == null) return;
            ulong id = SteamUser.GetSteamID().m_SteamID;
            
            GameObject voiceObj = new GameObject("VCLYSS_Local_Voice");
            voiceObj.transform.SetParent(p.transform, false);
            voiceObj.transform.localPosition = new Vector3(0, 1.6f, 0);

            var indicator = voiceObj.AddComponent<VoiceIndicator>();
            indicator.Initialize();
            _playerIndicators[id] = indicator;

            var lipSync = voiceObj.AddComponent<LipSync>();
            lipSync.Initialize(p);
            _playerLipSyncs[id] = lipSync;
        }

        public void DebugAudioSystem()
        {
            Main.Log.LogInfo($"=== AUDIO SYSTEM DEBUG ===");
            Main.Log.LogInfo($"Player streams: {_playerAudioStreams.Count}");
            Main.Log.LogInfo($"Speaking timers: {_speakingTimers.Count}");
            Main.Log.LogInfo($"Player indicators: {_playerIndicators.Count}");
            Main.Log.LogInfo($"Player lip syncs: {_playerLipSyncs.Count}");
            
            foreach (var kvp in _playerAudioStreams)
            {
                Main.Log.LogInfo($"Stream {kvp.Key}: playing={kvp.Value.isPlaying}, source exists={kvp.Value.source != null}, queue={kvp.Value.audioQueue.Count}");
            }
        }

        private void OnDestroy()
        {
            List<ulong> keysToRemove = new List<ulong>();
            
            foreach (var kvp in _playerAudioStreams)
            {
                if (kvp.Value.source == null || FindPlayerObject(kvp.Key) == null)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            
            foreach (var key in keysToRemove)
            {
                _playerAudioStreams.Remove(key);
                _playerIndicators.Remove(key);
                _playerLipSyncs.Remove(key);
                _speakingTimers.Remove(key);
            }
        }

        // Add this method to get optimal sample rate from Steam
        private uint GetOptimalSampleRate()
        {
            uint optimalSampleRate = SteamUser.GetVoiceOptimalSampleRate();
            Main.Log.LogInfo($"Optimal sample rate from Steam: {optimalSampleRate}");
            return optimalSampleRate;
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
                VoiceManager.Instance.IsVoiceEnabled = true;
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
                VoiceManager.Instance.IsVoiceEnabled = true;
                SteamUser.StartVoiceRecording();
                Main.Log.LogInfo($"Joined Modded Lobby: Voice Chat ENABLED.");
            }
            else
            {
                VoiceManager.Instance.IsVoiceEnabled = false;
                SteamUser.StopVoiceRecording();
                Main.Log.LogInfo("Voice Chat DISABLED (Host missing mod OR disabled in settings).");
            }
        }
    }
}