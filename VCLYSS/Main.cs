using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using CodeTalker.Networking;
using CodeTalker.Packets;
using Steamworks;
using UnityEngine;
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
        public static ConfigEntry<bool> CfgLipSync; 
        public enum MicMode { PushToTalk, Toggle, AlwaysOn }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            InitConfig();
            Settings.OnInitialized.AddListener(AddSettings);
            Settings.OnApplySettings.AddListener(() => { Config.Save(); VoiceSystem.ApplySettingsToAll(); });

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
            CfgShowHeadIcons = Config.Bind("4. Visuals", "Show Head Icons (Bubble)", true, "Show GMod-style bubble");
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
        private HashSet<ulong> _mutedPlayers = new HashSet<ulong>();

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

        // --- BUFFERS ---
        private byte[] _compressedBuffer = new byte[8192]; 
        private byte[] _decompressedBuffer = new byte[65536]; 

        // --- RING BUFFER ---
        private float[] _floatBuffer; 
        private int _writePos = 0;
        private int _bufferLength; 
        private AudioClip _streamingClip;
        private int _sampleRate;

        // --- STATE ---
        private bool _isRecording = false;
        private float _lastVolume = 0f;
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;
        
        // --- TIMEOUT TRACKING (Fixes Looping) ---
        private float _lastPacketTime = 0f;
        private bool _isPlaying = false;
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
            _audioSource.loop = true; // Ring buffer requires loop
            _audioSource.playOnAwake = false;
            
            _sampleRate = (int)SteamUser.GetVoiceOptimalSampleRate();
            _bufferLength = _sampleRate * 2; // 2 Second Buffer
            _floatBuffer = new float[_bufferLength];
            
            _streamingClip = AudioClip.Create($"Voice_{OwnerID}", _bufferLength, 1, _sampleRate, false);
            _audioSource.clip = _streamingClip;
            
            ApplyAudioSettings();
        }

        private void InitializeBubble()
        {
            _bubbleObject = new GameObject("VoiceBubble");
            _bubbleObject.transform.SetParent(transform, false);
            
            // Raised to 3.8f (Requested adjustment)
            _bubbleObject.transform.localPosition = new Vector3(0, 3.8f, 0); 
            
            _bubbleRenderer = _bubbleObject.AddComponent<SpriteRenderer>();
            
            // --- LOAD EMBEDDED RESOURCE ---
            _bubbleRenderer.sprite = LoadVoiceSprite(); 
            _bubbleRenderer.color = Color.white; 
            _bubbleObject.SetActive(false);
        }

        // --- EMBEDDED RESOURCE LOADER ---
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
                        if (tex.LoadImage(buffer))
                        {
                            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                        }
                    }
                    else
                    {
                        Main.Log.LogWarning($"Resource '{resourceName}' not found. Check Embedded Resource settings.");
                    }
                }
            }
            catch (Exception e)
            {
                Main.Log.LogError($"Failed to load embedded Voice.png: {e.Message}");
            }

            // Fallback: Procedural Circle
            return CreateCircleSprite();
        }

        public void ApplyAudioSettings()
        {
            if (_audioSource == null) return;
            
            _audioSource.volume = Main.CfgMasterVolume.Value;
            
            if (IsLocalPlayer) 
            {
                _audioSource.spatialBlend = 0f; 
            }
            else
            {
                // STRICTLY SUPPORTED API ONLY
                _audioSource.spatialBlend = Main.CfgSpatialBlending.Value ? 1.0f : 0.0f;
                _audioSource.minDistance = Main.CfgMinDistance.Value;
                _audioSource.maxDistance = Main.CfgMaxDistance.Value;
                _audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
            }
        }

        // --- THE LOOP ---

        void Update()
        {
            if (!Main.CfgEnabled.Value || !_audioInitialized) return;

            if (!IsLocalPlayer) CheckLocalPlayerStatus();

            if (IsLocalPlayer) 
            {
                HandleMicInput();
            }

            // --- TIMEOUT CHECK (The Anti-Loop Fix) ---
            if (_isPlaying && Time.time - _lastPacketTime > 0.3f)
            {
                _audioSource.Stop();
                FlushBuffer(); // Wipe the buffer so next play is fresh
                _isPlaying = false;
                _lastVolume = 0f;
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
                Main.Log.LogInfo("Mic Recording Started");
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
                            
                            // --- MULTICAST FIX: Send to ALL OTHER players individually ---
                            foreach(var vm in VoiceSystem.ActiveManagers)
                            {
                                if (vm != this && vm.AttachedPlayer != null)
                                {
                                    CodeTalkerNetwork.SendNetworkPacket(
                                        vm.AttachedPlayer, 
                                        packet, 
                                        Compressors.CompressionType.GZip, 
                                        CompressionLevel.Fastest
                                    );
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

        // --- THE RECEIVER ---

        public void ReceiveNetworkData(byte[] compressedData)
        {
            if (IsLocalPlayer && !Main.CfgMicTest.Value) return; 

            uint bytesWritten;
            EVoiceResult res = SteamUser.DecompressVoice(
                compressedData, (uint)compressedData.Length,
                _decompressedBuffer, (uint)_decompressedBuffer.Length,
                out bytesWritten, (uint)_sampleRate
            );

            if (res == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
            {
                ProcessPCMData(_decompressedBuffer, (int)bytesWritten);
                if(_lipSync != null) _lipSync.SetSpeaking();
                
                _lastPacketTime = Time.time; 
            }
        }

        private void ProcessPCMData(byte[] rawBytes, int length)
        {
            int sampleCount = length / 2; 
            float maxVol = 0f;
            
            // 1. Write Audio Data
            for (int i = 0; i < sampleCount; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                float floatVal = val / 32768.0f;
                
                _floatBuffer[_writePos] = floatVal;
                _writePos = (_writePos + 1) % _bufferLength;

                float absVol = Mathf.Abs(floatVal);
                if (absVol > maxVol) maxVol = absVol;
            }

            // 2. Clear Ahead (Safety Silence)
            int silenceSamples = _sampleRate / 2; 
            int clearStart = _writePos;
            for (int i = 0; i < silenceSamples; i++)
            {
                _floatBuffer[(clearStart + i) % _bufferLength] = 0f;
            }

            _lastVolume = Mathf.Lerp(_lastVolume, maxVol, Time.deltaTime * 10f);

            // 3. Update Clip
            _streamingClip.SetData(_floatBuffer, 0);

            // 4. Play / Sync
            if (!_audioSource.isPlaying) 
            {
                _audioSource.Play();
                _isPlaying = true;
            }
            
            int playPos = _audioSource.timeSamples;
            int dist = (_writePos - playPos + _bufferLength) % _bufferLength;
            
            // If lag > 0.2s, snap closer
            if (dist > (_sampleRate / 5)) 
            {
                int newPos = _writePos - (_sampleRate / 20); // Snap to 0.05s behind
                if (newPos < 0) newPos += _bufferLength;
                _audioSource.timeSamples = newPos;
            }
        }

        // --- VISUALS ---

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

                // SPINNING
                _bubbleObject.transform.Rotate(Vector3.up, 180f * Time.deltaTime);

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

        // --- UTILS ---

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