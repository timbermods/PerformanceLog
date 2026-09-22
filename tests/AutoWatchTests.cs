using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    // AutoWatch = true: the patch methods other mods put on hot methods are timed in the Watch slots the config left free. Which ones is
    // decided from plain records (AutoWatch.Plan), so the choice is checked here without Harmony; the game side is checked against real
    // methods, with the patching itself done by hand, because Harmony cannot apply a patch in this process.
    internal static class AutoWatchTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("AutoWatch: other mods' patches fill only the free Watch slots; one that is refused or cannot be patched takes none", CapIsRespected);
            yield return ("AutoWatch: a method a Watch entry names is not watched twice, and the Watch entries take their slots first", WatchEntriesWin);
            yield return ("AutoWatch: the choice is in name order whatever order Harmony lists the patches in, the methods behind the profile's rows first", OrderIsByName);
            yield return ("AutoWatch: a patch is examined the way a Watch entry is, and the methods behind the profile's rows are recognised", PatchesAreExamined);
            yield return ("AutoWatch: a watched patch method is registered with the log, counted and timed, and its watch allocates nothing", WatchedPatchIsTimed);
            yield return ("AutoWatch: the patches are made in the slots the Watch entries left, never on a method one of them watches", EntriesKeepTheirSlots);
            yield return ("AutoWatch: every patch is made inside the guard that puts the game's random state back (co-op stays in step)", GameRandomIsKept);
        }

        const string Own = "kyler.performancelog";
        const string EntityTick = "Timberborn.TickSystem.TickableEntity.Tick";
        const string Panel = "Timberborn.EntityPanelSystem.EntityPanel.UpdateSingleton";
        const string Input = "Timberborn.InputSystem.InputService.UpdateSingleton";
        const string Range = "Timberborn.Common.RandomNumberGenerator.Range";
        const string Connected = "Timberborn.GameDistricts.DistrictConnections.AreDistrictsConnected";
        const string ConnectedWith = "Timberborn.GameDistricts.DistrictConnections.GetDistrictsConnectedWith";
        const string TickerUpdate = "Timberborn.TickSystem.Ticker.Update";
        const string Strength = "Timberborn.WaterSourceSystem.WaterDepthStrengthModifier.GetStrengthModifier";
        const string Visuals = "Timberborn.StockpileVisualization.StockpileVisualizers.OnInventoryChanged";

        /// <summary>One patch as the game side reports it. The assembly is the label's first part, the owner made up from it unless given.</summary>
        static AutoWatchCandidate C(string label, string kind, string target, bool row = false, string owner = null, string refused = null, bool replaces = false)
        {
            int dot = target.LastIndexOf('.');
            string type = target.Substring(0, dot);
            string assembly = label.Substring(0, label.IndexOf('.'));
            return new AutoWatchCandidate
            {
                Label = label, Assembly = assembly, Owner = owner ?? assembly.ToLowerInvariant() + ".mod", Kind = kind,
                TypeName = type.Substring(type.LastIndexOf('.') + 1), MethodName = target.Substring(dot + 1), Target = target,
                ProfileRow = row, Refused = refused, Method = label, CanReplace = replaces,
            };
        }

        // Shaped like the patches in a real recording (BeaverBuddies, LateGamePerformance and MixedStorage on the same game).
        static List<AutoWatchCandidate> Patches() => new List<AutoWatchCandidate>
        {
            C("BB.EntityPatch.Prefix(TickableEntity)", "prefix", EntityTick, row: true),
            C("BB.EntityPatch.Postfix(TickableEntity)", "postfix", EntityTick, row: true),
            C("BB.RandomPatch.Prefix(Int32,Int32)", "prefix", Range),
            C("BB.DistrictFix.Finalizer(Exception)", "finalizer", Connected),
            C("BB.DistrictFix.Finalizer(Exception)", "finalizer", ConnectedWith),
            C("LGP.UiThrottle.PanelPrefix()", "prefix", Panel, row: true, owner: "kyler.lategameperformance.UiThrottle"),
            C("LGP.Timing.UpdatePrefix()", "prefix", TickerUpdate, owner: "kyler.lategameperformance.Timing", replaces: true),
            C("MS.TextEditingInputPatch.Finalizer(Exception)", "finalizer", Input, row: true),
            C("MS.TextEditingInputPatch.Prefix()", "prefix", Input, row: true),
            // Never taken: this mod's own patches (its measuring and its watch), a transpiler (it runs once, when the patch is made, so
            // there is nothing to time), and a patch on a method that is neither behind a profile row nor on the hot list.
            C("PerformanceLog.Instrumentation.EntityPrefix(Sample&)", "prefix", EntityTick, row: true, owner: Own),
            C("PerformanceLog.Watch.WatchPrefix(MethodBase,Sample&)", "prefix", TickerUpdate, owner: Own + ".watch"),
            C("BB.WaterSourceTimingFix.Transpiler(IEnumerable`1)", "transpiler", Strength),
            C("MS.Visuals.Postfix()", "postfix", Visuals),
        };

        // Name order: the methods behind the profile's own rows first (by the patched method's name, then prefix, postfix, finalizer), then
        // the rest of the hot list the same way. One patch method on two hot methods is one watch, placed by the first of them.
        static readonly string[] Expected =
        {
            "LGP.UiThrottle.PanelPrefix()",
            "MS.TextEditingInputPatch.Prefix()",
            "MS.TextEditingInputPatch.Finalizer(Exception)",
            "BB.EntityPatch.Prefix(TickableEntity)",
            "BB.EntityPatch.Postfix(TickableEntity)",
            "BB.RandomPatch.Prefix(Int32,Int32)",
            "BB.DistrictFix.Finalizer(Exception)",
            "LGP.Timing.UpdatePrefix()",
        };

        static string Labels(IEnumerable<AutoWatchResult> results) => string.Join(", ", results.Select(r => r.Candidate.Label + " [" + r.Status + "]"));

        static void CapIsRespected()
        {
            List<AutoWatchCandidate> patches = Patches();
            // First in name order, and refused (the game side says why, as Watch does for a config entry).
            patches.Add(C("LGP.Generic.Prefix()", "prefix", Panel, row: true, owner: "kyler.lategameperformance.UiThrottle", refused: "skipped: abstract or generic"));
            var tried = new List<string>();
            List<AutoWatchResult> results = AutoWatch.Plan(patches, Own, new List<string>(), 3, c =>
            {
                tried.Add(c.Label);
                return c.Label == "MS.TextEditingInputPatch.Prefix()" ? "boom" : null;
            });
            Equal(9, results.Count, "one result for each patch method on a hot method: " + Labels(results));
            Equal(3, results.Count(r => r.Watching), "watching: " + Labels(results));
            Check(tried.SequenceEqual(new[] { "LGP.UiThrottle.PanelPrefix()", "MS.TextEditingInputPatch.Prefix()", "MS.TextEditingInputPatch.Finalizer(Exception)", "BB.EntityPatch.Prefix(TickableEntity)" }),
                "a refused method is never patched, a failed patch leaves its slot to the next, and nothing is patched once the slots are used: " + string.Join(", ", tried));
            Equal("skipped: abstract or generic", results.Single(r => r.Candidate.Label == "LGP.Generic.Prefix()").Status);
            Equal("could not be patched: boom", results.Single(r => r.Candidate.Label == "MS.TextEditingInputPatch.Prefix()").Status);
            Check(results.Where(r => !r.Watching && tried.IndexOf(r.Candidate.Label) < 0 && r.Candidate.Refused == null).All(r => r.Status.StartsWith("skipped: no free Watch slot")),
                "the rest say there was no free slot: " + Labels(results));
            Equal(4, results.Count(r => r.Status.StartsWith("skipped: no free Watch slot")));
            Check(AutoWatch.Describe(results).StartsWith("watching 3 of 9 "), AutoWatch.Describe(results));

            foreach (int free in new[] { 0, -2 })
            {
                bool called = false;
                List<AutoWatchResult> none = AutoWatch.Plan(Patches(), Own, new List<string>(), free, c => { called = true; return null; });
                Check(!called && none.Count == Expected.Length && none.All(r => !r.Watching && r.Status.StartsWith("skipped: no free Watch slot")),
                    "with no free slot (" + free + ") nothing is patched: " + Labels(none));
            }
        }

        static void WatchEntriesWin()
        {
            // The Watch entries resolved to 38 methods, one of them a patch the auto watch would have taken, so 2 slots are left.
            var named = new List<string> { "BB.EntityPatch.Prefix(TickableEntity)" };
            var tried = new List<string>();
            List<AutoWatchResult> results = AutoWatch.Plan(Patches(), Own, named, Config.MaxWatched - 38, c => { tried.Add(c.Label); return null; });
            AutoWatchResult entry = results.Single(r => r.Candidate.Label == "BB.EntityPatch.Prefix(TickableEntity)");
            Check(!entry.Watching && entry.Status == "watched by a Watch entry", "the Watch entry's own watch is kept: " + entry.Status);
            Check(tried.SequenceEqual(new[] { "LGP.UiThrottle.PanelPrefix()", "MS.TextEditingInputPatch.Prefix()" }), "only the free slots are filled: " + string.Join(", ", tried));
            Equal(2, results.Count(r => r.Watching));
            Check(AutoWatch.Describe(results).Contains("1 named by a Watch entry"), AutoWatch.Describe(results));
        }

        static void OrderIsByName()
        {
            List<AutoWatchResult> first = AutoWatch.Plan(Patches(), Own, new List<string>(), Config.MaxWatched, c => null);
            Check(first.Select(r => r.Candidate.Label).SequenceEqual(Expected), "in name order: " + Labels(first));
            Check(first.All(r => r.Watching && r.Status == AutoWatch.Watching), Labels(first));
            Equal("finalizer on " + Connected + "; finalizer on " + ConnectedWith, first.Single(r => r.Candidate.Label == "BB.DistrictFix.Finalizer(Exception)").On,
                "one watch for a patch method on two hot methods, naming both");
            Equal("prefix on " + EntityTick, first.Single(r => r.Candidate.Label == "BB.EntityPatch.Prefix(TickableEntity)").On);
            Equal("prefix on " + TickerUpdate + " (can replace it)", first.Single(r => r.Candidate.Label == "LGP.Timing.UpdatePrefix()").On,
                "a prefix that can skip the method it patches says so, since its time is then work done instead of the game's");
            string[] expected = first.Select(r => r.Candidate.Label + "|" + r.Status + "|" + r.On).ToArray();
            var random = new Random(7);
            for (int round = 0; round < 20; round++)
            {
                List<AutoWatchCandidate> shuffled = Patches().OrderBy(_ => random.Next()).ToList();
                string[] again = AutoWatch.Plan(shuffled, Own, new List<string>(), Config.MaxWatched, c => null).Select(r => r.Candidate.Label + "|" + r.Status + "|" + r.On).ToArray();
                Check(again.SequenceEqual(expected), "the same choice whatever order the patches come in: " + string.Join(", ", again));
            }
            // With fewer slots it is the front of the same order that is taken.
            List<AutoWatchResult> five = AutoWatch.Plan(Patches().AsEnumerable().Reverse(), Own, new List<string>(), 5, c => null);
            Check(five.Where(r => r.Watching).Select(r => r.Candidate.Label).SequenceEqual(Expected.Take(5)), Labels(five));

            // A patch method on several hot methods is placed by the first of them in that same order, whichever Harmony lists first: one on
            // a method behind a profile row and on a hot-list method whose name sorts first goes with the rows, and one on two row methods
            // goes with the one whose name sorts first.
            var several = new List<AutoWatchCandidate>
            {
                C("BB.Both.Prefix()", "prefix", Range),
                C("BB.Both.Prefix()", "prefix", EntityTick, row: true),
                C("BB.Two.Postfix()", "postfix", Input, row: true),
                C("BB.Two.Postfix()", "postfix", Panel, row: true),
            };
            string[] expectedSeveral =
            {
                "LGP.UiThrottle.PanelPrefix()",
                "BB.Two.Postfix()",
                "MS.TextEditingInputPatch.Prefix()",
                "MS.TextEditingInputPatch.Finalizer(Exception)",
                "BB.Both.Prefix()",
                "BB.EntityPatch.Prefix(TickableEntity)",
                "BB.EntityPatch.Postfix(TickableEntity)",
                "BB.RandomPatch.Prefix(Int32,Int32)",
                "BB.DistrictFix.Finalizer(Exception)",
                "LGP.Timing.UpdatePrefix()",
            };
            for (int round = 0; round < 20; round++)
            {
                List<AutoWatchCandidate> shuffled = Patches().Concat(several).OrderBy(_ => random.Next()).ToList();
                List<AutoWatchResult> placed = AutoWatch.Plan(shuffled, Own, new List<string>(), Config.MaxWatched, c => null);
                Check(placed.Select(r => r.Candidate.Label).SequenceEqual(expectedSeveral), "placed by its first hot method: " + Labels(placed));
                AutoWatchResult both = placed.Single(r => r.Candidate.Label == "BB.Both.Prefix()");
                Equal(EntityTick, both.Candidate.Target, "the patch that places it");
                Equal("prefix on " + Range + "; prefix on " + EntityTick, both.On);
                Equal(Panel, placed.Single(r => r.Candidate.Label == "BB.Two.Postfix()").Candidate.Target);
            }
        }

        // ---- the game side ----

        static void PatchesAreExamined()
        {
            MethodInfo entityTick = typeof(TickableEntity).GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
            Check(entityTick != null, "TickableEntity.Tick exists");
            MethodInfo prefix = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Prefix));
            AutoWatchCandidate c = Watch.Examine(entityTick, "prefix", "other.mod", prefix);
            Equal("PerformanceLog.Tests.AutoWatchFakePatches.Prefix(Int32,String)", c.Label, "labelled the way a Watch entry is");
            Equal("PerformanceLog.Tests", c.Assembly);
            Equal("other.mod", c.Owner); Equal("prefix", c.Kind);
            Equal("TickableEntity", c.TypeName); Equal("Tick", c.MethodName); Equal(EntityTick, c.Target);
            Check(c.ProfileRow, "TickableEntity.Tick is what the entity rows time");
            Check(c.Refused == null, "a plain static method can be watched: " + c.Refused);
            Check(Equals(MethodBase.GetMethodFromHandle(prefix.MethodHandle), c.Method),
                "the key is the method as MethodBase.GetMethodFromHandle gives it, which is what Harmony hands a patch as __originalMethod " +
                "(on .NET 8 that is the registry's own object anyway; only the game's Mono could tell them apart, see TESTING.md)");
            Check(!c.CanReplace, "a prefix that returns nothing cannot skip the method it patches");
            MethodInfo skip = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Skip));
            Check(Watch.Examine(entityTick, "prefix", "other.mod", skip).CanReplace, "a prefix that returns bool can skip the method it patches");
            Check(!Watch.Examine(entityTick, "postfix", "other.mod", skip).CanReplace, "a postfix never can");

            MethodInfo generic = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Generic));
            Check((Watch.Examine(entityTick, "postfix", "other.mod", generic).Refused ?? "").Contains("abstract or generic"));
            MethodBase filtered = Reflect.Overloads(AccessTools.TypeByName("Timberborn.GameSaveRuntimeSystem.GameSaver"), "Save").First(Reflect.HasExceptionFilter);
            Check((Watch.Examine(entityTick, "prefix", "other.mod", (MethodInfo)filtered).Refused ?? "").Contains("catch ... when"),
                "a patch method with an exception filter is refused, as a Watch entry is");

            // The methods whose time a profile row already holds: a singleton's per-tick and per-frame methods, an entity's and a component's tick.
            Check(Watch.RunsBehindAProfileRow(typeof(AutoWatchFakeTickable).GetMethod("Tick")), "ITickableSingleton.Tick");
            Check(Watch.RunsBehindAProfileRow(typeof(AutoWatchFakeUpdatable).GetMethod("UpdateSingleton")), "IUpdatableSingleton.UpdateSingleton");
            Check(Watch.RunsBehindAProfileRow(typeof(AutoWatchFakeUpdatable).GetMethod("LateUpdateSingleton")), "ILateUpdatableSingleton.LateUpdateSingleton");
            Check(Watch.RunsBehindAProfileRow(typeof(AutoWatchFakeParallel).GetMethod("StartParallelTick")), "IParallelTickableSingleton.StartParallelTick");
            Check(Watch.RunsBehindAProfileRow(entityTick), "TickableEntity.Tick");
            Check(Watch.RunsBehindAProfileRow(typeof(MeteredTickableComponent).GetMethod("Tick")), "MeteredTickableComponent.Tick");
            Type walker = Assembly.Load("Timberborn.WalkingSystem").GetType("Timberborn.WalkingSystem.Walker", true);
            Check(typeof(TickableComponent).IsAssignableFrom(walker), "Walker is one of the game's own components");
            Check(Watch.RunsBehindAProfileRow(walker.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)),
                "a component's own Tick (Walker.Tick): the component rows time it");
            Check(!Watch.RunsBehindAProfileRow(typeof(Ticker).GetMethod("Update")), "Ticker.Update is on the hot list, not behind a row");
            Check(!Watch.RunsBehindAProfileRow(typeof(AutoWatchFakeTickable).GetMethod("Tock")), "another method of a singleton");
            Check(!Watch.RunsBehindAProfileRow(typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Tick))), "a static Tick of a class that is no singleton");
            Check(!Watch.RunsBehindAProfileRow(null));
        }

        static void WatchedPatchIsTimed()
        {
            var rig = new Rig();
            Action<Action> guard = Watch.AroundAutoPatching;
            try
            {
                Watch.ResetForTest();
                MethodInfo entityTick = typeof(TickableEntity).GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo prefix = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Prefix));
                MethodInfo refused = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Generic));
                MethodInfo unused = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Unused));
                var patched = new List<MethodBase>();
                Watch.AroundAutoPatching = run => run();   // Unity's random state cannot be read here; GameRandomIsKept checks the real guard
                Watch.AutoInstall(() => new List<AutoWatchCandidate>
                {
                    Watch.Examine(entityTick, "prefix", "other.mod", prefix),
                    Watch.Examine(entityTick, "postfix", "other.mod", refused),
                    Watch.Examine(entityTick, "postfix", "other.mod", unused),
                }, m => { patched.Add(m); return null; });
                Equal(2, patched.Count, "only the methods that can be watched are patched");
                Equal(2, Watch.Count);

                // What the header says: one capability line, and a # watch| line per patch method (label, status, auto, what it is on, owner),
                // in the order perflog.py reads them.
                string capability = Watch.AutoCapability(new Config { AutoWatch = true });
                Check(capability.StartsWith("watching 2 of 3 patch methods other mods put on hot methods") && capability.Contains("1 refused or could not be patched"), capability);
                Check(Watch.AutoCapability(new Config()).StartsWith("off"), "AutoWatch = false says off");
                List<string[]> lines = Watch.AutoLines().ToList();
                Equal(3, lines.Count, "a # watch| line for each patch method, the refused one too");
                Check(lines[0].SequenceEqual(new[] { "PerformanceLog.Tests.AutoWatchFakePatches.Prefix(Int32,String)", "watching", "auto", "prefix on " + EntityTick, "other.mod" }),
                    string.Join("|", lines[0]));
                Check(lines[1][0].EndsWith(".Generic()") && lines[1][1].Contains("abstract or generic") && lines[1][2] == "auto", string.Join("|", lines[1]));
                Check(lines[2][0].EndsWith(".Unused()") && lines[2][1] == "watching" && lines[2][3] == "postfix on " + EntityTick, string.Join("|", lines[2]));
                Watch.OnSessionStart();   // what Session.Start does once the profile is reset

                // Harmony hands the patch the method it is on, as MethodBase.GetMethodFromHandle gives it.
                MethodBase original = MethodBase.GetMethodFromHandle(prefix.MethodHandle);
                for (int i = 0; i < 100; i++) { Watch.WatchPrefix(original, out Sample warm); rig.Advance(0.01); Watch.WatchPostfix(original, warm); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2000; i++)
                {
                    Watch.WatchPrefix(original, out Sample s);
                    rig.Advance(0.01);
                    Watch.WatchPostfix(original, s);
                }
                Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "bytes allocated by 2000 calls of a watched patch method");
                string final = Watch.AutoFinal();
                Check(final.StartsWith("1 of 2 watched patch methods were called|never seen called"), "the end of the log says which ran and which never did: " + final);
                Check(final.EndsWith(": PerformanceLog.Tests.AutoWatchFakePatches.Unused()"), "the one never called is named: " + final);

                Profile.FlushWindow(1, 0, 10, rig.Prof);
                double[] row = rig.ProfileRows().Single(r => (ProfileKind)(int)r[0] == ProfileKind.Method && r[4] > 0);
                Equal(2100.0, row[4], "every call counted");
                Check(row[5] >= 1 && row[6] > 0, "some calls timed");
                Equal("PerformanceLog.Tests.AutoWatchFakePatches.Prefix(Int32,String)", Profile.NameOf((int)row[3]));
                Equal("PerformanceLog.Tests", Profile.AssemblyOf((int)row[3]), "the row names the patch's own DLL, so the mod column is the patching mod");
            }
            finally
            {
                Watch.ResetForTest();
                Watch.AroundAutoPatching = guard;
                rig.Dispose();
            }
        }

        static void EntriesKeepTheirSlots()
        {
            Action<Action> guard = Watch.AroundAutoPatching;
            try
            {
                Watch.ResetForTest();
                Watch.AroundAutoPatching = run => run();
                MethodInfo entityTick = typeof(TickableEntity).GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo prefix = typeof(AutoWatchFakePatches).GetMethod(nameof(AutoWatchFakePatches.Prefix));
                // The Watch entries resolved to 38 methods, one of them a patch method the auto watch finds, so 2 slots are left.
                AutoWatchCandidate named = Watch.Examine(entityTick, "prefix", "other.mod", prefix);
                Watch.AddEntryForTest((MethodBase)named.Method, named.Label, named.Assembly);
                for (int i = 1; i < 38; i++) Watch.AddEntryForTest(entityTick, "Some.Mod.Entry" + i + "()", "SomeMod");
                var patched = new List<string>();
                Watch.AutoInstall(() => new[] { nameof(AutoWatchFakePatches.Unused), nameof(AutoWatchFakePatches.Tick), nameof(AutoWatchFakePatches.Skip), nameof(AutoWatchFakePatches.Other) }
                    .Select(n => Watch.Examine(entityTick, "postfix", "other.mod", typeof(AutoWatchFakePatches).GetMethod(n)))
                    .Append(named).ToList(), m => { patched.Add(m.Name); return null; });
                Check(patched.SequenceEqual(new[] { "Other", "Skip" }), "only the 2 free slots are filled, in name order: " + string.Join(", ", patched));
                Equal(Config.MaxWatched, Watch.Count, "40 watched in all");
                List<string[]> lines = Watch.AutoLines().ToList();
                Equal(AutoWatch.NamedByWatch, lines.Single(l => l[0] == named.Label)[1], "the Watch entry keeps its own watch");
                Equal(2, lines.Count(l => l[1] == AutoWatch.NoFreeSlot), "the rest say there was no free slot");
            }
            finally { Watch.ResetForTest(); Watch.AroundAutoPatching = guard; }
        }

        static void GameRandomIsKept()
        {
            Action<Action> guard = Watch.AroundAutoPatching;
            try
            {
                // In the game the guard is KeepUnityRandom: it reads UnityEngine.Random.state before it runs anything, and writes it back in a
                // finally, so a Harmony patch's draws (BeaverBuddies turns MonoMod's Guid.NewGuid into Unity random numbers) are undone.
                Watch.ResetForTest();
                MethodInfo keep = typeof(Watch).GetMethod("KeepUnityRandom", BindingFlags.NonPublic | BindingFlags.Static);
                Check(keep != null, "Watch.KeepUnityRandom exists");
                Check(guard.Method == keep, "the auto watch's patching runs inside KeepUnityRandom (the checks before this one put it back)");
                ExceptionHandlingClause restore = keep.GetMethodBody().ExceptionHandlingClauses.Single(c => c.Flags == ExceptionHandlingClauseOptions.Finally);
                List<(int At, MethodBase Method)> calls = Calls(keep);
                List<int> At(Type type, string name) => calls.Where(c => c.Method.DeclaringType?.FullName == type.FullName && c.Method.Name == name).Select(c => c.At).ToList();
                List<int> get = At(typeof(UnityEngine.Random), "get_state"), set = At(typeof(UnityEngine.Random), "set_state"), run = At(typeof(Action), "Invoke");
                Check(get.Count == 1 && get[0] < restore.TryOffset, "the state is read once, before anything runs: at " + string.Join(",", get) + ", try at " + restore.TryOffset);
                Check(run.Count == 1 && run[0] >= restore.TryOffset && run[0] < restore.TryOffset + restore.TryLength, "the patching runs inside the try");
                Check(set.Count == 1 && set[0] >= restore.HandlerOffset && set[0] < restore.HandlerOffset + restore.HandlerLength,
                    "the state is written back in the finally: at " + string.Join(",", set) + ", finally at " + restore.HandlerOffset);

                // Every patch the auto watch makes, and its reading of Harmony's registry, happen inside the guard.
                int entered = 0;
                bool inside = false;
                var outside = new List<string>();
                Watch.AroundAutoPatching = patching => { entered++; inside = true; try { patching(); } finally { inside = false; } };
                MethodInfo entityTick = typeof(TickableEntity).GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public);
                var patched = new List<string>();
                Watch.AutoInstall(() =>
                {
                    if (!inside) outside.Add("reading the registry");
                    return new[] { nameof(AutoWatchFakePatches.Prefix), nameof(AutoWatchFakePatches.Unused) }
                        .Select(n => Watch.Examine(entityTick, "prefix", "other.mod", typeof(AutoWatchFakePatches).GetMethod(n))).ToList();
                }, m => { if (!inside) outside.Add("patching " + m.Name); patched.Add(m.Name); return null; });
                Equal(1, entered, "the guard is entered once");
                Equal(2, patched.Count, "patches were made");
                Check(outside.Count == 0, "nothing is done outside the guard: " + string.Join(", ", outside));
            }
            finally { Watch.ResetForTest(); Watch.AroundAutoPatching = guard; }
        }

        /// <summary>Every call in a method's IL, with the offset of its instruction.</summary>
        static List<(int At, MethodBase Method)> Calls(MethodInfo method)
        {
            var codes = new Dictionary<int, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.GetValue(null) is OpCode op) codes[(ushort)op.Value] = op;
            byte[] il = method.GetMethodBody().GetILAsByteArray();
            var calls = new List<(int, MethodBase)>();
            for (int i = 0; i < il.Length;)
            {
                int at = i;
                int value = il[i++];
                if (value == 0xFE) value = 0xFE00 | il[i++];
                OpCode op = codes[value];
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                    case OperandType.InlineVar: i += 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                    case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                    case OperandType.InlineMethod: calls.Add((at, method.Module.ResolveMethod(BitConverter.ToInt32(il, i)))); i += 4; break;
                    default: i += 4; break;
                }
            }
            return calls;
        }
    }

    internal static class AutoWatchFakePatches
    {
        public static void Prefix(int a, string b) { }
        public static void Generic<T>() { }
        public static void Tick() { }
        public static void Unused() { }
        public static void Other() { }
        public static bool Skip() => true;
    }

    internal sealed class AutoWatchFakeTickable : ITickableSingleton
    {
        public void Tick() { }
        public void Tock() { }
    }

    internal sealed class AutoWatchFakeUpdatable : IUpdatableSingleton, ILateUpdatableSingleton
    {
        public void UpdateSingleton() { }
        public void LateUpdateSingleton() { }
    }

    internal sealed class AutoWatchFakeParallel : IParallelTickableSingleton
    {
        public void StartParallelTick() { }
    }
}
