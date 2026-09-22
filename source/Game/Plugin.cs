using System;
using System.IO;
using System.Reflection;
using Bindito.Core;
using Timberborn.ModManagerScene;

namespace PerformanceLog
{
    /// <summary>
    /// The mod's entry point. It reads the config and makes the patches that measure the game. Nothing here changes what the game
    /// does; if anything fails the game runs as it would without the mod.
    /// </summary>
    public class Plugin : IModStarter
    {
        /// <summary>Must equal the Id in manifest.json.</summary>
        public const string ModId = "kyler.performancelog";

        public static readonly string Version = ReadVersion();

        internal static Config Config { get; private set; }
        internal static bool Started { get; private set; }

        /// <summary>Test-only: lets a check set what StartMod would have set, without actually starting the mod. Returns the previous value.</summary>
        internal static Config SetConfigForTest(Config config)
        {
            Config previous = Config;
            Config = config;
            return previous;
        }

        static string ReadVersion()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString(3);
            }
            catch (Exception) { return "?"; }
        }

        public void StartMod(IModEnvironment modEnvironment)
        {
            Milestones.Mark("mod-started");
            try
            {
                Log.Info(Version + " loading.");
                Config = Config.Load(modEnvironment.ModPath, modEnvironment.OriginPath, FolderAboveScripts());
                Log.Info("Settings: " + Config);
                foreach (string problem in Config.Problems) Log.Warning("Setting: " + problem);
                if (!Config.Enabled)
                {
                    Log.Info("Disabled in " + Config.FileName + "; nothing will be measured.");
                    return;
                }
                Instrumentation.Install(Config);
                if (Config.Watch.Count > 0)
                {
                    int watched = Watch.Install(new HarmonyLib.Harmony(Instrumentation.HarmonyId + ".watch"), Config);
                    Log.Info("Watching " + watched + " method(s).");
                    if (watched > 0) Instrumentation.InstalledHit[Instrumentation.HitWatch] = true;
                }
                Started = true;
            }
            catch (Exception exception)
            {
                Log.Warning("Failed to start; the game runs unmodified. " + exception);
            }
        }

        // The settings file ships next to the manifest, one level above Scripts/. ModPath may or may not be that folder depending
        // on how the game resolved the versioned layout, so both are tried.
        static string FolderAboveScripts()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(Path.GetDirectoryName(location));
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Starts a log in every game scene (a new game or a loaded save), when the mod is enabled.</summary>
    [Context("Game")]
    public class GameConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            Milestones.Mark("game-context-configured");
            if (Plugin.Started) containerDefinition.Bind<SessionService>().AsSingleton();
        }
    }

    [Context("MainMenu")]
    public class MainMenuConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            Milestones.Mark("main-menu-configured");
        }
    }
}
