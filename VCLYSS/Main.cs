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
using System.IO.Compression;
using Nessie.ATLYSS.EasySettings;

namespace VCLYSS
{
    [BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
    [BepInDependency("CodeTalker")]
    [BepInDependency("com.nessie.easysettings")]
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
        
        public static ConfigEntry<float> CfgMinDistance; 
        public static ConfigEntry<float> CfgMaxDistance; 
        public static ConfigEntry<bool> CfgSpatialBlending; 

        public static ConfigEntry<IconVisibility> CfgIconMode;

        public enum MicMode { PushToTalk, Toggle, AlwaysOn }
        public enum IconVisibility { Always, OnActivity, Never }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            InitConfig();
            Settings.OnInitialized.AddListener(AddSettings);
            Settings.OnApplySettings.AddListener(() => { Config.Save(); UpdateAudioSettings(); });

            _harmony = new Harmony(ModInfo.GUID);
            _harmony.PatchAll();

            CodeTalkerNetwork.RegisterBinaryListener<VoicePacket>(OnVoicePacketReceived);

            var go = new GameObject("VCLYSS_Manager");
            DontDestroyOnLoad(go);
            go.AddComponent<VoiceManager>();

            Logger.LogInfo($"[{ModInfo.NAME}] Loaded. API Ready.");
        }

        private void InitConfig()
        {
            // Define Ranges HERE so AddSlider works correctly without extra arguments
            CfgEnabled = Config.Bind("1. General", "Voice Chat Active", true, "Master Switch");
            CfgMasterVolume = Config.Bind("1. General", "Master Volume", 1.0f, new ConfigDescription("Incoming Voice Volume", new AcceptableValueRange<float>(0.0f, 2.0f)));
            CfgMuteAll = Config.Bind("1. General", "Mute Everyone (Panic)", false, "Panic button to silence all incoming voice");

            CfgMicMode = Config.Bind("2. Input", "Input Mode", MicMode.PushToTalk, "Input Method");
            CfgPushToTalk = Config.Bind("2. Input", "Push To Talk Key", KeyCode.V, "Bind for PTT/Toggle");
            CfgMicThreshold = Config.Bind("2. Input", "Mic Activation Threshold", 0.05f, new ConfigDescription("Gate threshold", new AcceptableValueRange<float>(0.0f, 0.5f)));

            CfgMinDistance = Config.Bind("3. Spatial", "Min Distance", 1.0f, new ConfigDescription("Loud Zone", new AcceptableValueRange<float>(0.1f, 10.0f)));
            CfgMaxDistance = Config.Bind("3. Spatial", "Max Distance", 25.0f, new ConfigDescription("Falloff Zone", new AcceptableValueRange<float>(5.0f, 100.0f)));
            CfgSpatialBlending = Config.Bind("3. Spatial", "3D Spatial Audio", true, "Enable 3D Directional Audio");

            CfgIconMode = Config.Bind("4. Visuals", "Icon Visibility", IconVisibility.OnActivity, "When to show the bubble");
        }

        private void AddSettings()
        {
            SettingsTab tab = Settings.ModTab;

            tab.AddHeader("General");
            tab.AddToggle("Voice Chat Active", CfgEnabled);
            tab.AddToggle("Mute Everyone (Panic)", CfgMuteAll);
            tab.AddSlider("Master Volume", CfgMasterVolume); // Now works because Range is in InitConfig

            tab.AddHeader("Microphone");
            tab.AddDropdown("Input Mode", CfgMicMode);
            tab.AddKeyButton("Keybind (PTT/Toggle)", CfgPushToTalk);
            tab.AddSlider("Mic Threshold", CfgMicThreshold); 

            tab.AddHeader("Spatial / Earmuffs");
            tab.AddToggle("3D Spatial Audio", CfgSpatialBlending);
            tab.AddSlider("Min Distance", CfgMinDistance);
            tab.AddSlider("Max Distance", CfgMaxDistance);

            tab.AddHeader("Visuals");
            tab.AddDropdown("Icon Visibility", CfgIconMode);
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
            if (packet is VoicePacket voicePkt)
            {
                VoiceManager.Instance.ReceiveVoiceData(header.SenderID, voicePkt.CompressedAudio, voicePkt.DataLength);
            }
        }
    }

    // -----------------------------------------------------------
    // 1. THE PACKET (GZIP COMPRESSED)
    // -----------------------------------------------------------
    public class VoicePacket : BinaryPacketBase
    {
        public override string PacketSignature => "VCLYSS_AUDIO_GZ";
        public byte[] CompressedAudio;
        public int DataLength;

        public VoicePacket() { }
        public VoicePacket(byte[] data, int length)
        {
            CompressedAudio = data;
            DataLength = length;
        }

        public override byte[] Serialize()
        {
            byte[] rawData;
            using (var ms = new MemoryStream())
            {
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(DataLength);
                    writer.Write(CompressedAudio, 0, DataLength);
                }
                rawData = ms.ToArray();
            }

            using (var compressedMs = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressedMs, CompressionMode.Compress))
                {
                    gzip.Write(rawData, 0, rawData.Length);
                }
                return compressedMs.ToArray();
            }
        }

        public override void Deserialize(byte[] data)
        {
            using (var compressedMs = new MemoryStream(data))
            using (var gzip = new GZipStream(compressedMs, CompressionMode.Decompress))
            using (var rawMs = new MemoryStream())
            {
                gzip.CopyTo(rawMs);
                rawMs.Position = 0;
                using (var reader = new BinaryReader(rawMs))
                {
                    DataLength = reader.ReadInt32();
                    CompressedAudio = reader.ReadBytes(DataLength);
                }
            }
        }
    }

    // -----------------------------------------------------------
    // 2. THE VISUAL INDICATOR
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
            _iconObject = new GameObject("VoiceIcon");
            _iconObject.transform.SetParent(this.transform, false);
            _iconObject.transform.localPosition = new Vector3(0, 2.8f, 0); 
            _iconObject.transform.localScale = Vector3.one * 0.5f;

            _renderer = _iconObject.AddComponent<SpriteRenderer>();
            
            if (_cachedSprite == null) _cachedSprite = CreateBubbleSprite();
            _renderer.sprite = _cachedSprite;
            
            _renderer.color = new Color(1f, 1f, 1f, 0.9f); 
            _renderer.enabled = false;
        }

        public void SetSpeaking()
        {
            if (Main.CfgIconMode.Value == Main.IconVisibility.Never) return;
            _lastTalkTime = Time.time;
        }

        private void Update()
        {
            if (Main.CfgIconMode.Value == Main.IconVisibility.Always)
            {
                _renderer.enabled = true;
            }
            else if (Main.CfgIconMode.Value == Main.IconVisibility.Never)
            {
                _renderer.enabled = false;
                return;
            }
            else 
            {
                bool isTalking = (Time.time - _lastTalkTime < 0.25f);
                _renderer.enabled = isTalking;
            }

            if (_renderer.enabled)
            {
                if (_cameraTransform == null)
                {
                    if (Camera.main != null) _cameraTransform = Camera.main.transform;
                    else return;
                }
                _iconObject.transform.LookAt(transform.position + _cameraTransform.rotation * Vector3.forward, _cameraTransform.rotation * Vector3.up);
            }
        }

        private Sprite CreateBubbleSprite()
        {
            int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            Color white = Color.white;
            Color clear = Color.clear;
            Vector2 center = new Vector2(size / 2, size / 2);
            float radius = size / 2 - 4;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), center);
                    if (dist < radius) pixels[y * size + x] = white;
                    else if (dist < radius + 2) pixels[y * size + x] = new Color(0,0,0,0.5f); 
                    else pixels[y * size + x] = clear;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }

    // -----------------------------------------------------------
    // 3. THE VOICE MANAGER (API READY)
    // -----------------------------------------------------------
    public class VoiceManager : MonoBehaviour
    {
        public static VoiceManager Instance;

        private const uint CompressedBufferSize = 8192;
        private const uint DecompressedBufferSize = 22050;
        private const uint SampleRate = 24000;

        private byte[] _compressedBuffer = new byte[CompressedBufferSize];
        private byte[] _decompressedBuffer = new byte[DecompressedBufferSize];

        private Dictionary<ulong, AudioSource> _playerAudioSources = new Dictionary<ulong, AudioSource>();
        private Dictionary<ulong, VoiceIndicator> _playerIndicators = new Dictionary<ulong, VoiceIndicator>();
        
        // --- PERSONAL SECURITY ---
        private HashSet<ulong> _mutedPlayers = new HashSet<ulong>();

        public bool IsVoiceEnabled { get; set; } = false;
        
        private bool _isToggleOn = false;
        private bool _wasKeyDown = false;

        void Awake() { Instance = this; }

        void Update()
        {
            if (!Main.CfgEnabled.Value) return;
            if (!IsVoiceEnabled || !SteamManager.Initialized) return;

            // --- INPUT LOGIC ---
            bool isTransmitting = false;

            if (Main.CfgMicMode.Value == Main.MicMode.AlwaysOn)
            {
                isTransmitting = true;
            }
            else if (Main.CfgMicMode.Value == Main.MicMode.PushToTalk)
            {
                isTransmitting = Input.GetKey(Main.CfgPushToTalk.Value);
            }
            else if (Main.CfgMicMode.Value == Main.MicMode.Toggle)
            {
                bool isKeyDown = Input.GetKey(Main.CfgPushToTalk.Value);
                if (isKeyDown && !_wasKeyDown) 
                {
                    _isToggleOn = !_isToggleOn;
                }
                _wasKeyDown = isKeyDown;
                isTransmitting = _isToggleOn;
            }

            if (!isTransmitting) return;

            // --- CAPTURE ---
            uint availableSize;
            SteamUser.GetAvailableVoice(out availableSize);

            if (availableSize > 0)
            {
                uint bytesWritten;
                SteamUser.GetVoice(true, _compressedBuffer, CompressedBufferSize, out bytesWritten);

                if (bytesWritten > 0)
                {
                    byte[] sendBuffer = new byte[bytesWritten];
                    Array.Copy(_compressedBuffer, sendBuffer, bytesWritten);

                    var packet = new VoicePacket(sendBuffer, (int)bytesWritten);
                    CodeTalkerNetwork.SendBinaryNetworkPacket(packet);
                    
                    ShowMyIcon();
                }
            }
        }

        private void ShowMyIcon()
        {
            if (Player._mainPlayer == null) return;
            ulong myID = SteamUser.GetSteamID().m_SteamID;
            
            if (!_playerIndicators.ContainsKey(myID))
            {
                var ind = Player._mainPlayer.gameObject.AddComponent<VoiceIndicator>();
                ind.Initialize();
                _playerIndicators[myID] = ind;
            }
            _playerIndicators[myID].SetSpeaking();
        }

        public void ApplySettingsToAll()
        {
            foreach(var source in _playerAudioSources.Values)
            {
                if(source != null) UpdateSourceSettings(source);
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

        // ==================================================
        //                 PUBLIC API
        // ==================================================

        public void MutePlayer(ulong steamID)
        {
            if (!_mutedPlayers.Contains(steamID))
            {
                _mutedPlayers.Add(steamID);
                Main.Log.LogInfo($"API: Muted player {steamID}");
                if (_playerAudioSources.TryGetValue(steamID, out var source) && source != null)
                {
                    source.Stop();
                }
            }
        }

        public void UnmutePlayer(ulong steamID)
        {
            if (_mutedPlayers.Contains(steamID))
            {
                _mutedPlayers.Remove(steamID);
                Main.Log.LogInfo($"API: Unmuted player {steamID}");
            }
        }

        public bool IsPlayerMuted(ulong steamID)
        {
            return _mutedPlayers.Contains(steamID);
        }

        // ==================================================

        public void ReceiveVoiceData(ulong senderSteamID, byte[] data, int length)
        {
            // --- SECURITY CHECKS ---
            if (!Main.CfgEnabled.Value) return;
            if (Main.CfgMuteAll.Value) return; 
            if (_mutedPlayers.Contains(senderSteamID)) return; 
            if (senderSteamID == SteamUser.GetSteamID().m_SteamID) return;

            uint bytesWritten;
            EVoiceResult result = SteamUser.DecompressVoice(
                data, (uint)length,
                _decompressedBuffer, DecompressedBufferSize,
                out bytesWritten, SampleRate
            );

            if (result == EVoiceResult.k_EVoiceResultOK && bytesWritten > 0)
            {
                float amplitude = CalculateAmplitude(_decompressedBuffer, (int)bytesWritten);
                if (amplitude >= Main.CfgMicThreshold.Value)
                {
                    PlayAudio(senderSteamID, _decompressedBuffer, (int)bytesWritten);
                }
            }
        }

        private float CalculateAmplitude(byte[] pcmData, int length)
        {
            float sum = 0;
            int sampleCount = length / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short val = BitConverter.ToInt16(pcmData, i * 2);
                sum += Mathf.Abs(val / 32768.0f);
            }
            return sum / sampleCount;
        }

        private void PlayAudio(ulong steamID, byte[] rawBytes, int length)
        {
            AudioSource source = GetAudioSource(steamID);
            
            // SECURITY: Check if source is null (kicked/left)
            if (source == null) 
            {
                if (_playerAudioSources.ContainsKey(steamID))
                {
                    _playerAudioSources.Remove(steamID);
                    _playerIndicators.Remove(steamID);
                }
                return;
            }

            float[] floatData = new float[length / 2];
            for (int i = 0; i < floatData.Length; i++)
            {
                short val = BitConverter.ToInt16(rawBytes, i * 2);
                floatData[i] = val / 32768.0f;
            }

            AudioClip clip = AudioClip.Create("VoiceSnippet", floatData.Length, 1, (int)SampleRate, false);
            clip.SetData(floatData, 0);
            source.PlayOneShot(clip);

            if (_playerIndicators.TryGetValue(steamID, out var indicator) && indicator != null)
            {
                indicator.SetSpeaking();
            }
        }

        private AudioSource GetAudioSource(ulong steamID)
        {
            if (_playerAudioSources.TryGetValue(steamID, out AudioSource existing))
            {
                if (existing == null) 
                {
                    _playerAudioSources.Remove(steamID);
                    _playerIndicators.Remove(steamID);
                }
                else
                {
                    return existing;
                }
            }

            GameObject playerObj = FindPlayerObject(steamID);

            if (playerObj != null)
            {
                var source = playerObj.AddComponent<AudioSource>();
                UpdateSourceSettings(source);
                _playerAudioSources[steamID] = source;

                var indicator = playerObj.AddComponent<VoiceIndicator>();
                indicator.Initialize();
                _playerIndicators[steamID] = indicator;

                return source;
            }
            return null;
        }

        private GameObject FindPlayerObject(ulong targetID)
        {
            foreach (Player p in UnityEngine.Object.FindObjectsOfType<Player>())
            {
                if (ulong.TryParse(p._steamID, out ulong pSteamID))
                {
                    if (pSteamID == targetID) return p.gameObject;
                }
            }
            return null; 
        }
    }

    // -----------------------------------------------------------
    // 4. HOST CONTROL PATCHES
    // -----------------------------------------------------------
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

            if (value == "true" && Main.CfgEnabled.Value)
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