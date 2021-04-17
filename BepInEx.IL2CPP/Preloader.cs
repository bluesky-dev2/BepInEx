﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core;
using BepInEx.Preloader.Core.Logging;
using UnhollowerBaseLib;
using UnhollowerBaseLib.Runtime;

namespace BepInEx.IL2CPP
{
    public static class Preloader
    {
        public static string IL2CPPUnhollowedPath { get; internal set; }

        private static PreloaderConsoleListener PreloaderLog { get; set; }

        internal static ManualLogSource Log => PreloaderLogger.Log;
        internal static ManualLogSource UnhollowerLog { get; set; }

        // TODO: This is not needed, maybe remove? (Instance is saved in IL2CPPChainloader itself)
        private static IL2CPPChainloader Chainloader { get; set; }

        public static void Run()
        {
            try
            {
                ConsoleManager.Initialize(false);

                PreloaderLog = new PreloaderConsoleListener();
                Logger.Listeners.Add(PreloaderLog);


                if (ConsoleManager.ConfigConsoleEnabled.Value)
                {
                    ConsoleManager.CreateConsole();
                    Logger.Listeners.Add(new ConsoleLogListener());
                }

                ChainloaderLogHelper.PrintLogInfo(Log);

                Log.LogDebug($"Game executable path: {Paths.ExecutablePath}");
                Log.LogDebug($"Unhollowed assembly directory: {IL2CPPUnhollowedPath}");
                Log.LogDebug($"BepInEx root path: {Paths.BepInExRootPath}");

                UnhollowerLog = Logger.CreateLogSource("Unhollower");
                LogSupport.InfoHandler += UnhollowerLog.LogInfo;
                LogSupport.WarningHandler += UnhollowerLog.LogWarning;
                LogSupport.TraceHandler += UnhollowerLog.LogDebug;
                LogSupport.ErrorHandler += UnhollowerLog.LogError;


                if (ProxyAssemblyGenerator.CheckIfGenerationRequired())
                    ProxyAssemblyGenerator.GenerateAssemblies();
                
                
                InitializeUnityVersion();


                using (var assemblyPatcher = new AssemblyPatcher())
                {
                    assemblyPatcher.AddPatchersFromDirectory(Paths.PatcherPluginPath);

                    Log.LogInfo($"{assemblyPatcher.PatcherPlugins.Count} patcher plugin{(assemblyPatcher.PatcherPlugins.Count == 1 ? "" : "s")} loaded");

                    assemblyPatcher.LoadAssemblyDirectories(IL2CPPUnhollowedPath);

                    Log.LogInfo($"{assemblyPatcher.PatcherPlugins.Count} assemblies discovered");

                    assemblyPatcher.PatchAndLoad();
                }


                Logger.Listeners.Remove(PreloaderLog);


                Chainloader = new IL2CPPChainloader();

                Chainloader.Initialize();
            }
            catch (Exception ex)
            {
                Log.LogFatal(ex);

                throw;
            }
        }

        private static void InitializeUnityVersion()
        {
            try
            {
                var version = ConfigUnityVersion.Value;
                if (string.IsNullOrWhiteSpace(version))
                {
                    version = //Version.Parse(Application.unityVersion);
                        Process.GetCurrentProcess().MainModule.FileVersionInfo.FileVersion;

                    PreloaderLogger.Log.LogDebug($"Unity version obtained from main application module: [{version}]");
                }
                else
                {
                    PreloaderLogger.Log.LogDebug($"Unity version obtained from the config: [{version}]");
                }

                var parts = version.Split('.');
                var major = 0;
                var minor = 0;
                var build = 0;

                // Issue #229 - Don't use Version.Parse("2019.4.16.14703470L&ProductVersion")
                bool success = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
                if (success && parts.Length > 1)
                    success = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
                if (success && parts.Length > 2)
                    success = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out build);

                if (!success)
                    throw new InvalidDataException($"Failed to parse Unity version: {version}");

                UnityVersionHandler.Initialize(major, minor, build);
                Log.LogInfo($"Running under Unity v{major}.{minor}.{build}");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Failed to parse Unity version.", ex);
            }
        }
        
        #region Config

        private static readonly ConfigEntry<string> ConfigUnityVersion = ConfigFile.CoreConfig.Bind(
            "IL2CPP", "UnityVersion",
            string.Empty,
            "Unity version to report to Il2CppUnhollower. If empty, version is automatically determined from the game process.");
        
        #endregion
    }
}