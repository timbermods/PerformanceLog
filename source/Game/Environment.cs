using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.Modding;
using Timberborn.Versioning;
using UnityEngine;
using UnityEngine.Scripting;

namespace PerformanceLog
{
    /// <summary>What computer and game this is, for the header and the summary. Everything is optional: a value that cannot be read is "unknown".</summary>
    internal static class EnvironmentInfo
    {
        static string Describe(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>Name and value, in the order to show them.</summary>
        public static List<KeyValuePair<string, string>> Collect()
        {
            var list = new List<KeyValuePair<string, string>>();
            void Add(string key, Func<string> read) => list.Add(new KeyValuePair<string, string>(key, Describe(read)));
            Add("game", () => GameVersions.CurrentVersion.ToString());
            Add("unity", () => Application.unityVersion);
            Add("os", () => SystemInfo.operatingSystem);
            Add("cpu", () => SystemInfo.processorType + " x" + SystemInfo.processorCount + " @ " + SystemInfo.processorFrequency + " MHz");
            Add("memoryMB", () => SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture));
            Add("gpu", () => SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + " MB)");
            Add("graphics", () => SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
            Add("display", () => "vSyncCount=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate +
                " resolution=" + Screen.width + "x" + Screen.height + " refreshHz=" + Describe(() => Screen.currentResolution.refreshRateRatio.value.ToString("F2", CultureInfo.InvariantCulture)) +
                " fullScreen=" + Screen.fullScreenMode);
            Add("quality", () => QualitySettings.names[QualitySettings.GetQualityLevel()] + " antiAliasing=" + QualitySettings.antiAliasing);
            Add("gc", () => GcState());
            return list;
        }

        public static string GcState() =>
            "mode=" + GarbageCollector.GCMode + " incremental=" + GarbageCollector.isIncremental +
            " timeSliceNs=" + GarbageCollector.incrementalTimeSliceNanoseconds + " maxGeneration=" + GC.MaxGeneration;

        public static IEnumerable<string> BootConfig()
        {
            var lines = new List<string>();
            try
            {
                string file = Path.Combine(Application.dataPath, "boot.config");
                if (!File.Exists(file)) { lines.Add("(no boot.config)"); return lines; }
                foreach (string line in File.ReadAllLines(file))
                {
                    if (lines.Count >= 60) break;
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
                }
            }
            catch (Exception error) { lines.Add("(could not read boot.config: " + error.GetType().Name + ")"); }
            return lines;
        }

        public static string[] CommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                return args.Take(40).Select((a, i) => i == 0 ? Path.GetFileName(a) : a).ToArray();
            }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>The enabled mods: id, name, version.</summary>
        public static List<string[]> Mods(ModRepository repository)
        {
            var mods = new List<string[]>();
            try
            {
                foreach (Mod mod in repository.EnabledMods)
                    if (mod?.Manifest != null) mods.Add(new[] { mod.Manifest.Id ?? "", mod.Manifest.Name ?? "", mod.Manifest.Version.Formatted ?? "" });
            }
            catch (Exception e) { Log.Warning("Could not read the enabled mods: " + e.Message); }
            return mods;
        }

        /// <summary>Which mod each DLL belongs to, by file name, so a class in a mod's assembly can be named for the mod.</summary>
        public static Dictionary<string, string> AssemblyOwners(ModRepository repository)
        {
            var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Mod mod in repository.EnabledMods)
                {
                    if (mod?.Manifest == null) continue;
                    DirectoryInfo directory = mod.ModDirectory.Directory;
                    if (directory == null || !directory.Exists) continue;
                    foreach (FileInfo dll in directory.GetFiles("*.dll", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(dll.Name);
                        if (!owners.ContainsKey(name)) owners[name] = mod.Manifest.Id;
                    }
                }
            }
            catch (Exception e) { Log.Warning("Could not tell which mod each DLL belongs to: " + e.Message); }
            return owners;
        }
    }

    /// <summary>Reads Harmony's records of who has patched what, for the header. It only reads; what is listed is decided in <see cref="PatchBuilder"/>.</summary>
    internal static class PatchReporter
    {
        internal static void Append(List<string> lines)
        {
            try
            {
                var methods = new List<PatchedMethodRecord>();
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null) continue;
                    var record = new PatchedMethodRecord
                    {
                        TypeName = method.DeclaringType?.Name,
                        MethodName = method.Name,
                        FullName = (method.DeclaringType?.FullName ?? "?") + "." + method.Name,
                    };
                    AddPatches(record, "prefix", info.Prefixes);
                    AddPatches(record, "postfix", info.Postfixes);
                    AddPatches(record, "transpiler", info.Transpilers);
                    AddPatches(record, "finalizer", info.Finalizers);
                    methods.Add(record);
                }
                // Built aside, so a failure part way leaves the header without a half-written report.
                var report = new List<string>();
                PatchBuilder.Build(methods, Instrumentation.HarmonyId, report);
                lines.AddRange(report);
            }
            catch (Exception error)
            {
                lines.Add("# patches-unavailable: " + PatchFormat.Clean(error.GetType().Name + " " + error.Message));
            }
        }

        static void AddPatches(PatchedMethodRecord record, string kind, IEnumerable<Patch> patches)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                MethodInfo patchMethod = patch.PatchMethod;
                record.Patches.Add(new PatchRecord
                {
                    Kind = kind,
                    Owner = patch.owner,
                    Priority = patch.priority,
                    Index = patch.index,
                    Before = patch.before,
                    After = patch.after,
                    Assembly = patchMethod?.DeclaringType?.Assembly.GetName().Name,
                    PatchMethod = patchMethod == null ? null : patchMethod.DeclaringType?.FullName + "." + patchMethod.Name,
                });
            }
        }
    }
}
