using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Decides what goes into the patch report of a session's header, from plain records: no Unity, Timberborn or Harmony types, so it can
// be tested on its own. The game side reads Harmony and hands the records here.
namespace PerformanceLog
{
    public sealed class PatchRecord
    {
        public string Kind, Owner, Assembly, PatchMethod;
        public int Priority, Index;
        public string[] Before, After;
    }

    public sealed class PatchedMethodRecord
    {
        /// <summary>The declaring type's short name, for the hot list.</summary>
        public string TypeName;
        public string MethodName;
        /// <summary>The full name shown in the log.</summary>
        public string FullName;
        /// <summary>Every patch on the method, in the order they run: prefixes, postfixes, transpilers, finalizers.</summary>
        public List<PatchRecord> Patches = new List<PatchRecord>();
    }

    public static class PatchFormat
    {
        // Methods that run every tick or every frame (or on every call of something that does). Another mod's patch on one of them
        // adds cost to a hot path. Written as "TypeName.MethodName" (getters as get_Name), matched against every overload.
        static readonly HashSet<string> hotMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Ticker.Update",
            "TickerUnityAdapter.Update",
            "TickableBucketService.TickBuckets",
            "TickableBucketService.FinishFullTick",
            "TickableBucketService.TickNextBucket",
            "TickableEntityBucket.TickAll",
            "TickableEntityBucket.Add",
            "TickableEntity.Tick",
            "MeteredTickableComponent.Tick",
            "TickableSingletonService.TickAll",
            "TickableSingletonService.TickSingletons",
            "TickableSingletonService.FinishParallelTick",
            "TickableSingletonService.StartParallelTick",
            "SingletonLifecycleService.UpdateSingletons",
            "SingletonLifecycleService.LateUpdateSingletons",
            "SingletonLifecycleUnityAdapter.Update",
            "SingletonLifecycleUnityAdapter.LateUpdate",
            "GameSaver.SaveQueued",
            "WaterSource.Tick",
            "WaterSourceRegistry.UpdateThreadSafeRegistry",
            "WaterDepthStrengthModifier.GetStrengthModifier",
            "ThreadSafeWaterMap.Update",
            "SoilMoistureService.UpdateMoistureLevels",
            "RandomNumberGenerator.Range",
            "RandomNumberGenerator.InsideUnitCircle",
            "Guid.NewGuid",
            "EntityService.Instantiate",
            "MovementAnimator.Update",
            "InputService.UpdateSingleton",
            "SoundEmitter.Update",
            "DayNightCycle.get_FluidSecondsPassedToday",
            "TickOnlyArrayService.get_AllowEdit",
            "ZiplineWaterPenaltyModifier.get_WaterPenaltyModifier",
            "DistrictObstacleService.SetObstacle",
            "DistrictObstacleService.UnsetObstacle",
            "DistrictConnections.GetDistrictsConnectedWith",
            "DistrictConnections.AreDistrictsConnected",
            "DistrictMap.AddDistrictCenter",
            "BehaviorManager.TickRunningExecutor",
            "Walker.FindPath",
            "Walker.StopMoving",
            "Enterer.Enter",
            "SlotManager.AddEnterer",
            "RecoveredGoodStackSpawner.UpdateSingleton",
            "SpeedManager.ChangeSpeedScale",
            "DateTime.ToString",
        };

        public static int HotMethodCount => hotMethods.Count;

        public static bool IsHot(string typeName, string methodName) =>
            typeName != null && methodName != null && hotMethods.Contains(typeName + "." + methodName);

        /// <summary>Makes text safe for one field of a header line: no line breaks and no field separator.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
                clean.Append(c == '|' ? '/' : c == '\r' || c == '\n' || c == '\t' ? ' ' : c);
            return clean.ToString().Trim();
        }

        /// <summary>
        /// One patch as one line of the header: <c># patch|tag|method|kind|owner|priority|index|before|after|assembly|patch</c>.
        /// The tag says why it is listed: hot (on the list above), shared (more than one owner) or other (not this mod's).
        /// </summary>
        public static string PatchLine(string tag, string method, string kind, string owner, int priority, int index,
            IEnumerable<string> before, IEnumerable<string> after, string assembly, string patchMethod)
        {
            return "# patch|" + Clean(tag) + "|" + Clean(method) + "|" + Clean(kind) + "|" + Clean(owner) + "|" +
                "priority=" + priority + "|index=" + index + "|" +
                "before=" + Clean(Join(before)) + "|after=" + Clean(Join(after)) + "|" +
                Clean(assembly) + "|" + Clean(patchMethod);
        }

        static string Join(IEnumerable<string> items) => items == null ? "" : string.Join(";", items);
    }

    public static class PatchBuilder
    {
        public const int MaxDetailLines = 600;

        /// <summary>
        /// Lists the methods that run every tick or frame and are patched (whoever patched them), every method more than one owner
        /// patches, and every method patched only by other mods. Methods only <paramref name="ownOwner"/> patches, and that are not
        /// hot, are counted but not listed.
        /// </summary>
        public static void Build(IEnumerable<PatchedMethodRecord> methods, string ownOwner, List<string> lines)
        {
            var detail = new List<string>();
            var methodsPerOwner = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int total = 0, hot = 0, shared = 0, other = 0, oursOnly = 0;
            foreach (PatchedMethodRecord method in methods.OrderBy(m => m.FullName, StringComparer.Ordinal))
            {
                if (method.Patches.Count == 0) continue;
                total++;
                List<string> owners = method.Patches.Select(p => p.Owner ?? "").Distinct(StringComparer.Ordinal).ToList();
                foreach (string owner in owners)
                    methodsPerOwner[owner] = methodsPerOwner.TryGetValue(owner, out int n) ? n + 1 : 1;

                bool ours = owners.Count == 1 && ownOwner != null && owners[0].StartsWith(ownOwner, StringComparison.Ordinal);
                string tag;
                if (PatchFormat.IsHot(method.TypeName, method.MethodName)) { tag = "hot"; hot++; }
                else if (owners.Count > 1) { tag = "shared"; shared++; }
                else if (ours) { oursOnly++; continue; }
                else { tag = "other"; other++; }

                foreach (PatchRecord p in method.Patches)
                    detail.Add(PatchFormat.PatchLine(tag, method.FullName, p.Kind, p.Owner, p.Priority, p.Index,
                        p.Before, p.After, p.Assembly, p.PatchMethod));
            }

            lines.Add($"# patches: methods={total} hot={hot} shared={shared} other={other} ownedOnlyByThisMod={oursOnly}");
            lines.Add("# patches-note: hot = a method that runs every tick or frame (whoever patched it, this mod included); shared = patched by more than one owner; " +
                      "other = patched only by other mods. Methods only this mod patches, and are not hot, are not listed.");
            lines.Add("# patches-note: the owner is the Harmony id the mod chose; the assembly is the DLL the patch code lives in, which names the mod. " +
                      "Within a kind, patches run in the order listed (higher priority first).");
            lines.Add("# patches-note: Harmony reports only patches made through the Harmony library the game loads. MonoMod hooks and native detours " +
                      "are not visible to it, so a mod that hooks that way does not appear here.");
            foreach (var pair in methodsPerOwner)
                lines.Add("# patch-owner|" + PatchFormat.Clean(pair.Key) + "|methods=" + pair.Value);
            lines.AddRange(detail.Take(MaxDetailLines));
            if (detail.Count > MaxDetailLines)
                lines.Add($"# patches-truncated: {detail.Count - MaxDetailLines} more patch lines not shown");
        }
    }
}
