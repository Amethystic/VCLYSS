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
using System.IO.Compression; // Required for GZip
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

namespace VCLYSS
{
	[BepInPlugin(ModInfo.GUID, ModInfo.NAME, ModInfo.VERSION)]
    [BepInDependency("CodeTalker")]
	public class Main : BaseUnityPlugin
	{
		private readonly Harmony _harmony = new Harmony(ModInfo.GUID);
		internal static ManualLogSource? Log;
        public static Main? Instance { get; private set; }
		
		private void Awake()
		{
            Instance = this;
			Log = Logger;
			_harmony.PatchAll();
		}
	}
}