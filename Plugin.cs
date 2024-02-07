using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using MonoMod.RuntimeDetour;

namespace ResourceUnloadOptimizer
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class ResourceUnloadOptimizerPlugin : BaseUnityPlugin
    {
        internal const string ModName = "ResourceUnloadOptimizer";
        internal const string ModVersion = "1.0.3";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        private readonly Harmony _harmony = new(ModGUID);
        public static readonly ManualLogSource ResourceUnloadOptimizerLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
        
        private static AsyncOperation _currentOperation;
        private static Func<AsyncOperation> _originalUnload;

        private static int _garbageCollect;
        private float _waitTime;

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        public void Awake()
        {
            DisableUnload = config("1 - General", "DisableUnload", Toggle.Off, "Disable the unloading of all resources. Requires large amounts of RAM or will likely crash your game. NOT RECOMMENDED FOR NORMAL USE.");
            OptimizeMemoryUsage = config("1 - General", "OptimizeMemoryUsage", Toggle.On, "Use more memory (if available) in order to load the game faster and reduce random stuttering.");
            PercentMemoryThreshold = config("1 - General", "PercentMemoryThreshold", 75, "Minimum amount of memory to be used before resource unloading will run.");
            
            InstallHooks();
            StartCoroutine(CleanupCo());
            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private static void InstallHooks()
        {
            var target = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets));
            var replacement = AccessTools.Method(typeof(Hooks), nameof(Hooks.UnloadUnusedAssetsHook));
            
            var detour = new NativeDetour(target, replacement);
            detour.Apply();
            
            _originalUnload = detour.GenerateTrampoline<Func<AsyncOperation>>();
            
        }
        
        private IEnumerator CleanupCo()
        {
            while (true)
            {
                while (Time.realtimeSinceStartup < _waitTime)
                    yield return null;

                _waitTime = Time.realtimeSinceStartup + 1;

                if (_garbageCollect > 0)
                {
                    if (--_garbageCollect == 0)
                        RunGarbageCollect();
                }
            }
        }
        
        private static AsyncOperation RunUnloadAssets()
        {
            // Only allow a single unload operation to run at one time
            if (_currentOperation == null || _currentOperation.isDone && !PlentyOfMemory())
            {
                ResourceUnloadOptimizerLogger.LogDebug("Starting unused asset cleanup");
                _currentOperation = _originalUnload();
            }
            return _currentOperation;
        }

        private static void RunGarbageCollect()
        {
            if (PlentyOfMemory()) return;

            ResourceUnloadOptimizerLogger.LogDebug("Starting full garbage collection");
            // Use different overload since we disable the parameterless one
            GC.Collect(GC.MaxGeneration);
        }

        private static bool PlentyOfMemory()
        {
            if (OptimizeMemoryUsage.Value == Toggle.Off) return false;

            var mem = MemoryInfo.GetCurrentStatus();
            if (mem == null) return false;

            // Clean up more aggresively during loading, less aggresively during gameplay
            var pageFileFree = mem.ullAvailPageFile / (float)mem.ullTotalPageFile;
            var plentyOfMemory = mem.dwMemoryLoad < PercentMemoryThreshold.Value // physical memory free %
                                 && pageFileFree > 0.3f // page file free %
                                 && mem.ullAvailPageFile > 2ul * 1024ul * 1024ul * 1024ul; // at least 2GB of page file free
            if (!plentyOfMemory)
                return false;

            ResourceUnloadOptimizerLogger.LogDebug($"Skipping cleanup because of low memory load ({mem.dwMemoryLoad}% RAM, {100 - (int)(pageFileFree * 100)}% Page file, {mem.ullAvailPageFile / 1024 / 1024}MB available in PF)");
            return true;
        }

        private static class Hooks
        {
            [HarmonyPrefix]
            [HarmonyPatch(typeof(GC), nameof(GC.Collect), new Type[0])]
            public static bool GCCollectHook()
            {
                // Throttle down the calls. Keep resetting the timer until things calm down since it's usually fairly low memory usage
                _garbageCollect = 3;
                // Disable the original method, Invoke will call it later
                return false;
            }

            // Replacement method needs to be inside a static class to be used in NativeDetour
            public static AsyncOperation UnloadUnusedAssetsHook()
            {
                return DisableUnload.Value  == Toggle.On ? null : RunUnloadAssets();
            }
        }

        private void OnDestroy()
        {
            Config.Save();
        }

        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                ResourceUnloadOptimizerLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                ResourceUnloadOptimizerLogger.LogError($"There was an issue loading your {ConfigFileName}");
                ResourceUnloadOptimizerLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        private static ConfigEntry<Toggle> DisableUnload = null!;
        private static ConfigEntry<Toggle> OptimizeMemoryUsage = null!;
        private static ConfigEntry<int> PercentMemoryThreshold = null!;

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description)
        {
            return config(group, name, value, new ConfigDescription(description));
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order;
            [UsedImplicitly] public bool? Browsable;
            [UsedImplicitly] public string? Category;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer;
        }

        #endregion
    }
}