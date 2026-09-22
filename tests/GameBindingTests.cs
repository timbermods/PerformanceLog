using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.Metrics;
using Timberborn.Multithreading;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    // Checks the game side against the installed game's real assemblies, without running the game. The Workshop build of Harmony only runs
    // under Mono, so patches are validated here rather than applied; the patch bodies are then called by hand, in the order Harmony would
    // call them, around the game's own classes. What still needs the running game is listed in docs/TESTING.md.
    internal static class GameBindingTests
    {
        static readonly string[] assemblies =
        {
            "Timberborn.TickSystem", "Timberborn.SingletonSystem", "Timberborn.GameSaveRuntimeSystem", "Timberborn.WorldPersistence",
            "Timberborn.WorldSerialization", "Timberborn.ThumbnailCapturing", "Timberborn.Metrics", "Timberborn.Multithreading", "Timberborn.SaveSystem",
        };

        public static IEnumerable<(string, Action)> All(string managed)
        {
            foreach (string name in assemblies)
            {
                string path = System.IO.Path.Combine(managed, name + ".dll");
                if (System.IO.File.Exists(path)) Assembly.LoadFrom(path);
            }
            yield return ("Game: every patch target exists and can be patched (no exception filter, parameters Harmony can supply)", TargetsResolve);
            yield return ("Game: the method the mod must never patch is recognised by its exception filter", ExceptionFilterRecognised);
            yield return ("Game: the game's tick loop runs one tick as 128 entity buckets, which is what the mod counts", EntityBucketsPerTick);
            yield return ("Game: the game's own SingletonLifecycleService runs wrapped per-frame singletons and times each", LifecycleWrapping);
            yield return ("Game: loading steps are recorded by wrappers around the game's own load loops", LoadWrapping);
            yield return ("Game: the game's own TickableSingletonService runs wrapped tick singletons and parallel starts", TickServiceWrapping);
            yield return ("Game: wrapping twice does not wrap the wrappers", NoDoubleWrapping);
            yield return ("Game: the wrappers wait for the first tick, so other mods' Load postfixes still see the game's own singletons", WrappingWaitsForTheFirstTick);
            yield return ("Game: the wrappers go in once per service, and again for the next game", WrappingIsOncePerService);
            yield return ("Game: two services that update every frame and alternate are each wrapped once, not swapped again every frame", WrappingHandlesServicesThatAlternate);
            yield return ("Game: the loading patches' counters survive the session starting in the middle of the load", LoadCountersSurviveTheSessionStart);
            yield return ("Game: Profile = off does not patch every entity tick, and only deep patches components", ProfileOffSkipsEntityPatches);
            yield return ("Game: a save left open by an exception does not block the next one", AbandonedSaveIsForgotten);
            yield return ("Game: the game's parallel tick figure is not added twice when a save finishes it again", ParallelTickCountedOnce);
            yield return ("Game: a wrapper passes through exceptions and still times the call", WrapperExceptions);
            yield return ("Game: a wrapper does nothing extra when the log is off", WrapperWhenOff);
            yield return ("Game: patch bodies pair up and hand the scope token from prefix to postfix", PatchBodiesPair);
            yield return ("Game: the game's own empty entity bucket is measured by the entity bucket patch", EntityBucketPatch);
            yield return ("Game: a save is tracked from its patch bodies into an event", SaveTracking);
            yield return ("Watch: named methods are found, and the ones that cannot be patched say why", WatchResolution);
            yield return ("Config: settings are read, clamped and checked", ConfigParsing);
            yield return ("Config: a missing file gives the defaults and a bad line is reported, not fatal", ConfigProblems);
            yield return ("Settings: the in-game panel's values are clamped onto a Config the same way the .cfg file is, and only those six", SettingsApplyTo);
            yield return ("Settings: the panel starts from whatever PerformanceLog.cfg already had, not the field defaults", SettingsSeedFromTheCfgFile);
            yield return ("Harmony: whether patches can be applied in this test process (informational)", HarmonyInfo);
        }

        // ---- patch targets ----

        static void TargetsResolve()
        {
            var problems = new List<string>();
            List<PatchSpec> specs = Instrumentation.CreateSpecs(deep: true);
            Check(specs.Count >= 20, "expected the full set of patches, got " + specs.Count);
            foreach (PatchSpec spec in specs)
            {
                MethodBase target;
                try { target = spec.Target(); }
                catch (Exception e) { problems.Add(spec.Name + ": " + e.Message); continue; }
                if (target == null) { problems.Add(spec.Name + ": the game has no such method"); continue; }
                if (Reflect.HasExceptionFilter(target)) problems.Add(spec.Name + ": target has an exception filter");
                if (spec.Hit < 0 || spec.Hit >= Instrumentation.HitCount) problems.Add(spec.Name + ": hit index out of range");
                if (spec.Prefix == null && spec.Postfix == null) problems.Add(spec.Name + ": neither a prefix nor a postfix");
                Type stateType = null;
                foreach (MethodInfo patch in new[] { spec.Prefix, spec.Postfix })
                {
                    if (patch == null) continue;
                    if (!patch.IsStatic) problems.Add(spec.Name + ": " + patch.Name + " is not static");
                    if (patch.ReturnType != typeof(void) && patch.ReturnType != typeof(bool)) problems.Add(spec.Name + ": " + patch.Name + " returns " + patch.ReturnType.Name);
                    foreach (ParameterInfo p in patch.GetParameters())
                    {
                        if (p.Name == "__instance")
                        {
                            if (target.IsStatic) problems.Add(spec.Name + ": __instance on a static target");
                            else if (!p.ParameterType.IsAssignableFrom(target.DeclaringType)) problems.Add(spec.Name + ": __instance type mismatch");
                        }
                        else if (p.Name == "__state")
                        {
                            Type t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                            if (patch == spec.Prefix && !p.IsOut) problems.Add(spec.Name + ": the prefix's __state must be out");
                            if (stateType != null && stateType != t) problems.Add(spec.Name + ": __state differs between prefix and postfix");
                            stateType = t;
                        }
                        else if (p.Name == "__originalMethod")
                        {
                            if (!p.ParameterType.IsAssignableFrom(typeof(MethodBase))) problems.Add(spec.Name + ": __originalMethod must be a MethodBase");
                        }
                        else
                        {
                            ParameterInfo match = target.GetParameters().FirstOrDefault(x => x.Name == p.Name);
                            if (match == null) problems.Add(spec.Name + ": target has no parameter '" + p.Name + "'");
                            else if (!p.ParameterType.IsAssignableFrom(match.ParameterType)) problems.Add(spec.Name + ": parameter '" + p.Name + "' type mismatch");
                        }
                    }
                }
                // Harmony builds its patch objects from the same methods; this checks they are accepted.
                foreach (MethodInfo patch in new[] { spec.Prefix, spec.Postfix })
                    if (patch != null) new HarmonyMethod(patch);
            }
            Check(problems.Count == 0, string.Join("; ", problems));
            Check(specs.Select(s => s.Name).Distinct().Count() == specs.Count, "patch names must be unique");
            Check(!specs.Any(s => s.Name.EndsWith("GameSaver.Save")), "GameSaver.Save must never be patched");
        }

        static void ExceptionFilterRecognised()
        {
            MethodBase save = Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "Save");
            Check(save != null, "GameSaver.Save exists");
            Check(Reflect.HasExceptionFilter(save), "GameSaver.Save has a catch ... when clause: it must be recognised so it is never patched");
            Check(!Reflect.HasExceptionFilter(Reflect.Method("Timberborn.TickSystem.Ticker", "Update")), "Ticker.Update has none");
            Check(!Reflect.HasExceptionFilter(Reflect.Method("Timberborn.TickSystem.TickableEntity", "Tick")), "TickableEntity.Tick has a try but no filter");
        }

        // ---- fakes of the game's own interfaces ----

        sealed class Repository : ISingletonRepository
        {
            public readonly List<object> Items = new List<object>();
            public IEnumerable<T> GetSingletons<T>() => Items.OfType<T>().ToList();
        }

        sealed class Everything : IUpdatableSingleton, ILateUpdatableSingleton, ITickableSingleton, IParallelTickableSingleton, ILoadableSingleton,
            IPostLoadableSingleton, INonSingletonLoader, INonSingletonPostLoader
        {
            public int Updates, Lates, Ticks, Parallels, Loads, PostLoads, NonLoads, NonPostLoads;
            public Action OnUpdate;
            public void UpdateSingleton() { Updates++; OnUpdate?.Invoke(); }
            public void LateUpdateSingleton() => Lates++;
            public void Tick() => Ticks++;
            public void StartParallelTick() => Parallels++;
            public void Load() => Loads++;
            public void PostLoad() => PostLoads++;
            public void LoadNonSingletons() => NonLoads++;
            public void PostLoadNonSingletons() => NonPostLoads++;
        }

        sealed class AllModes : ITickingMode { public bool SingletonIsActiveInThisMode(object singleton) => true; }
        sealed class Timer : ITimerMetric { public void Resume() { } public void Pause() { } }
        sealed class Metrics : IMetricsService
        {
            public bool MetricsEnabled => false;
            public ITimerMetric GetTimerMetric(string contextKey, string timerKey) => new Timer();
            public void ResetMetrics() { }
            public void WriteCollectedDataToFile(string path) { }
        }
        sealed class Snapshots : ISnapshotCollector
        {
            public bool IsCollecting => false;
            public void AddTaskSample(int run, int totalRuns, long startTimestamp, long endTimestamp, Type type) { }
            public void AddMarker(string id) { }
        }
        sealed class Workers : IParallelizer
        {
            public long LastTaskTimestamp => 0;
            public int NumberOfThreads => 1;
            public ParallelizerHandle Schedule<T>(in T task) where T : struct, IParallelizerSingleTask => default;
            public ParallelizerHandle Schedule<T>(in T task, ParallelizerHandle dependency) where T : struct, IParallelizerSingleTask => default;
            public ParallelizerHandle Schedule<T>(in T task, ReadOnlySpan<ParallelizerHandle> dependencies) where T : struct, IParallelizerSingleTask => default;
            public ParallelizerHandle Schedule<T>(int fromInclusive, int toExclusive, int batchSize, in T task) where T : struct, IParallelizerLoopTask => default;
            public ParallelizerHandle Schedule<T>(int fromInclusive, int toExclusive, int batchSize, in T task, ParallelizerHandle dependency) where T : struct, IParallelizerLoopTask => default;
            public ParallelizerHandle Schedule<T>(int fromInclusive, int toExclusive, int batchSize, in T task, ReadOnlySpan<ParallelizerHandle> dependencies) where T : struct, IParallelizerLoopTask => default;
            public void StartScheduling() { }
            public void StopScheduling() { }
            public void Wait() { }
            public void ThrowIfAnyPendingTasks() { }
        }

        static Type LifecycleType() => Reflect.GameType("Timberborn.SingletonSystem.SingletonLifecycleService");
        static Type TickServiceType() => Reflect.GameType("Timberborn.TickSystem.TickableSingletonService");

        static object NewLifecycle(Everything singleton)
        {
            var repository = new Repository();
            repository.Items.Add(singleton);
            return Activator.CreateInstance(LifecycleType(), repository);
        }

        static void Call(object instance, string method) =>
            AccessTools.Method(instance.GetType(), method).Invoke(instance, null);

        static Rig StartRig(out Everything singleton)
        {
            var rig = new Rig(thresholdMs: 1000);
            Profile.Configure(1, 1, 1);
            singleton = new Everything();
            return rig;
        }

        // ---- the tick loop ----

        static void EntityBucketsPerTick()
        {
            Type bucket = Reflect.GameType("Timberborn.TickSystem.TickableEntityBucket");
            Instrumentation.ReadEntityBucketsPerTick();
            Equal(128, Probe.EntityBucketsPerTick, "the game splits a tick into 128 entity buckets");
            Check(TickServiceType() != null && bucket != null);
        }

        // ---- wrappers on the game's own classes ----

        static void LifecycleWrapping()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                object service = NewLifecycle(singleton);
                Call(service, "LoadAll");                       // the game's own code loads it: nothing is patched here
                Equal(1, singleton.Loads); Equal(1, singleton.PostLoads); Equal(1, singleton.NonLoads); Equal(1, singleton.NonPostLoads);

                // What the prefixes of UpdateSingletons and LateUpdateSingletons do on the first frame.
                Instrumentation.UpdatePrefix(service, out long firstUpdate); Instrumentation.ScopePostfix(firstUpdate);
                Instrumentation.LateUpdatePrefix(service, out long firstLate); Instrumentation.ScopePostfix(firstLate);
                Call(service, "UpdateAll");
                Call(service, "LateUpdateAll");
                Equal(1, singleton.Updates); Equal(1, singleton.Lates);
                rig.Advance(2);
                rig.Frame();
                List<PerformanceLog.Profile.Total> totals = Profile.Totals();
                Profile.FlushWindow(1, 1, 1, rig.Prof);
                totals = Profile.Totals();
                Check(totals.Any(t => t.Kind == ProfileKind.UpdateSingleton && t.Name.EndsWith("GameBindingTests+Everything") && t.Calls == 1), "the update was timed and named");
                Check(totals.Any(t => t.Kind == ProfileKind.LateSingleton && t.Calls == 1), "the late update was timed");
                Call(service, "UpdateAll"); Call(service, "UpdateAll");
                Equal(3, singleton.Updates);
                Profile.FlushWindow(2, 2, 1, rig.Prof);
                Equal(3.0, Profile.Totals().Single(t => t.Kind == ProfileKind.UpdateSingleton).Calls);
            }
        }

        static void LoadWrapping()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                object service = NewLifecycle(singleton);
                // What the prefixes of the four load loops do, then the game's own loops run through the wrappers.
                LoadRecorder.Begin();
                Call(service, "LoadAll");                       // fills the arrays; runs unwrapped
                Instrumentation.LoadPhasePrefix(service, AccessTools.Method(service.GetType(), "LoadSingletons"), out long a);
                Instrumentation.LoadPhasePrefix(service, AccessTools.Method(service.GetType(), "LoadNonSingletons"), out long b);
                Instrumentation.LoadPhasePrefix(service, AccessTools.Method(service.GetType(), "PostLoadSingletons"), out long c);
                Instrumentation.LoadPhasePrefix(service, AccessTools.Method(service.GetType(), "PostLoadNonSingletons"), out long d);
                Check(a != 0 && b != 0 && c != 0 && d != 0, "each prefix gives a start time");
                foreach (string loop in new[] { "LoadSingletons", "LoadNonSingletons", "PostLoadSingletons", "PostLoadNonSingletons" }) Call(service, loop);
                Instrumentation.LoadPhasePostfix(AccessTools.Method(service.GetType(), "LoadSingletons"), a);
                LoadRecorder.End();
                Equal(2, singleton.Loads); Equal(2, singleton.PostLoads);
                var steps = new List<LoadRecorder.Step>(); var phases = new List<KeyValuePair<string, long>>();
                LoadRecorder.TakeNew(steps, phases);
                Equal(4, steps.Count);
                Check(steps.Select(s => s.Kind).OrderBy(k => k).SequenceEqual(new[] { ProfileKind.Load, ProfileKind.LoadNonSingleton, ProfileKind.PostLoad, ProfileKind.PostLoadNonSingleton }), "one step of each kind");
                Check(steps.All(s => s.Name.EndsWith("Everything") && s.Ticks >= 0));
                Equal(1, phases.Count);
                Equal("LoadSingletons", phases[0].Key);
                LoadRecorder.TakeNew(steps, phases);
                Equal(4, steps.Count, "nothing is handed out twice");
            }
        }

        static void TickServiceWrapping()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                var repository = new Repository();
                repository.Items.Add(singleton);
                object service = Activator.CreateInstance(TickServiceType(), repository, new AllModes(), new Metrics(), new Workers(), new Snapshots());
                Call(service, "Load");
                Instrumentation.TickAllPrefix(service);   // what the prefix of TickAll does on the first tick

                Call(service, "TickAll");                          // FinishParallelTick, TickSingletons, StartParallelTick
                Equal(1, singleton.Ticks); Equal(1, singleton.Parallels);
                Call(service, "TickAll");
                Equal(2, singleton.Ticks); Equal(2, singleton.Parallels);
                Profile.FlushWindow(1, 2, 1, rig.Prof);
                List<PerformanceLog.Profile.Total> totals = Profile.Totals();
                PerformanceLog.Profile.Total tick = totals.Single(t => t.Kind == ProfileKind.TickSingleton);
                Equal(2.0, tick.Calls);
                Check(tick.Name.EndsWith("Everything"));
                Equal(2.0, totals.Single(t => t.Kind == ProfileKind.ParallelStart).Calls);
            }
        }

        static void NoDoubleWrapping()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                object service = NewLifecycle(singleton);
                Call(service, "LoadAll");
                int first = SingletonArrays.Swap<IUpdatableSingleton>(service, "_updatableSingletons", x => new TimedUpdatable(x));
                int second = SingletonArrays.Swap<IUpdatableSingleton>(service, "_updatableSingletons", x => new TimedUpdatable(x));
                Equal(1, first); Equal(0, second, "the second pass finds only wrappers");
                Call(service, "UpdateAll");
                Equal(1, singleton.Updates);
                Profile.FlushWindow(1, 1, 1, rig.Prof);
                Equal(1.0, Profile.Totals().Single(t => t.Kind == ProfileKind.UpdateSingleton).Calls, "timed once, not twice");
            }
        }

        static void WrapperExceptions()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                singleton.OnUpdate = () => throw new InvalidOperationException("the singleton failed");
                object service = NewLifecycle(singleton);
                Call(service, "LoadAll");
                SingletonArrays.Swap<IUpdatableSingleton>(service, "_updatableSingletons", x => new TimedUpdatable(x));
                Exception thrown = null;
                try { Call(service, "UpdateAll"); } catch (TargetInvocationException e) { thrown = e.InnerException; }
                Check(thrown is InvalidOperationException && thrown.Message == "the singleton failed", "the game still sees the exception");
                Profile.FlushWindow(1, 1, 1, rig.Prof);
                Equal(1.0, Profile.Totals().Single(t => t.Kind == ProfileKind.UpdateSingleton).Calls, "and the call was timed anyway");
            }
        }

        static void WrapperWhenOff()
        {
            var singleton = new Everything();
            Probe.Stop();
            Profile.Reset();
            var wrapper = new TimedUpdatable(singleton);
            wrapper.UpdateSingleton();
            Equal(1, singleton.Updates);
            Equal(0, Profile.Count, "no key was registered while the log was off");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) wrapper.UpdateSingleton();
            Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before, "and calling it allocates nothing");
        }

        // ---- when the wrappers go in ----

        static IEnumerable<object> TickSingletonsIn(object service)
        {
            FieldInfo field = Reflect.Field(service.GetType(), "_tickableSingletons");
            FieldInfo inner = Reflect.Field(field.FieldType.GetGenericArguments()[0], "_tickableSingleton");
            foreach (object item in (System.Collections.IEnumerable)field.GetValue(service)) yield return inner.GetValue(item);
        }

        static IEnumerable<IUpdatableSingleton> UpdatablesIn(object service) =>
            (IEnumerable<IUpdatableSingleton>)Reflect.Field(service.GetType(), "_updatableSingletons").GetValue(service);

        static object NewTickService(Everything singleton)
        {
            var repository = new Repository();
            repository.Items.Add(singleton);
            object service = Activator.CreateInstance(TickServiceType(), repository, new AllModes(), new Metrics(), new Workers(), new Snapshots());
            Call(service, "Load");
            return service;
        }

        static void WrappingWaitsForTheFirstTick()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                object service = NewTickService(singleton);
                // A postfix another mod has on Load looks at what is in the array (BeaverBuddies reorders the singletons by their type). It must
                // see the game's own singletons, so tick order is what it would be without this mod.
                Check(TickSingletonsIn(service).All(x => x is Everything), "after Load the array holds the game's own singletons");
                Instrumentation.TickAllPrefix(service);
                Check(TickSingletonsIn(service).All(x => x is TimedTickable), "after the first tick's prefix it holds wrappers");

                object lifecycle = NewLifecycle(singleton);
                Call(lifecycle, "LoadAll");
                Check(UpdatablesIn(lifecycle).All(x => x is Everything), "the per-frame array is the game's own after LoadAll, and after this mod's LoadAll postfix");
                Instrumentation.LoadAllPostfix(lifecycle);
                Check(UpdatablesIn(lifecycle).All(x => x is Everything), "LoadAll's postfix does not wrap them");
                Instrumentation.UpdatePrefix(lifecycle, out long state);
                Instrumentation.ScopePostfix(state);
                Check(UpdatablesIn(lifecycle).All(x => x is TimedUpdatable), "the first frame does");
            }
        }

        static void WrappingIsOncePerService()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                Instrumentation.ResetForSession();
                object first = NewTickService(singleton);
                Instrumentation.EnsureTickSingletonsWrapped(first);
                long after = Instrumentation.Hits[Instrumentation.HitTickServiceLoad];
                Equal(1L, after, "one service wrapped");
                Instrumentation.EnsureTickSingletonsWrapped(first);
                Equal(after, Instrumentation.Hits[Instrumentation.HitTickServiceLoad], "the same service is not looked at again");
                object second = NewTickService(singleton);
                Instrumentation.EnsureTickSingletonsWrapped(second);
                Equal(after + 1, Instrumentation.Hits[Instrumentation.HitTickServiceLoad], "the next game's service is");
                Check(TickSingletonsIn(second).All(x => x is TimedTickable));
                Instrumentation.EnsureTickSingletonsWrapped(null);   // nothing to wrap: must not throw
                Instrumentation.ResetForSession();
                Equal(0L, Instrumentation.Hits[Instrumentation.HitTickServiceLoad], "a new session starts counting from zero");
            }
        }

        static void WrappingHandlesServicesThatAlternate()
        {
            Rig rig = StartRig(out Everything singleton);
            using (rig)
            {
                Instrumentation.ResetForSession();
                // The game keeps more than one singleton service alive and both update every frame, so their prefixes alternate. Remembering only
                // the last service made every call swap the arrays again (the first recording: 4 swaps a frame).
                object appLevel = NewLifecycle(singleton), gameLevel = NewLifecycle(singleton);
                Call(appLevel, "LoadAll"); Call(gameLevel, "LoadAll");
                long start = Instrumentation.Hits[Instrumentation.HitTickServiceLoad];
                for (int frame = 0; frame < 50; frame++)
                {
                    Instrumentation.EnsureUpdatableWrapped(appLevel);
                    Instrumentation.EnsureUpdatableWrapped(gameLevel);
                    Instrumentation.EnsureLateUpdatableWrapped(appLevel);
                    Instrumentation.EnsureLateUpdatableWrapped(gameLevel);
                }
                Equal(start + 4, Instrumentation.Hits[Instrumentation.HitTickServiceLoad], "two services, two arrays each: four swaps in all, not four a frame");
                Check(UpdatablesIn(appLevel).All(x => x is TimedUpdatable) && UpdatablesIn(gameLevel).All(x => x is TimedUpdatable), "and both are wrapped");
                Equal(1, UpdatablesIn(appLevel).Count(x => x is TimedUpdatable), "each once, not wrapped again");
            }
        }

        static void LoadCountersSurviveTheSessionStart()
        {
            Instrumentation.ResetForSession();
            Instrumentation.Hits[Instrumentation.HitLoadAll] = 0; Instrumentation.Hits[Instrumentation.HitLoadPhase] = 0;
            Instrumentation.LoadAllPrefix();
            Instrumentation.Hits[Instrumentation.HitLoadPhase] = 3;
            Instrumentation.Hits[Instrumentation.HitUpdate] = 9;
            Instrumentation.ResetForSession();   // a session starts in PostLoad, in the middle of the load
            Equal(1L, Instrumentation.Hits[Instrumentation.HitLoadAll], "LoadAll ran, and the log does not say it never did");
            Equal(3L, Instrumentation.Hits[Instrumentation.HitLoadPhase], "nor the load phases that ran before the session");
            Equal(0L, Instrumentation.Hits[Instrumentation.HitUpdate], "everything else starts from zero");
            Instrumentation.LoadAllPrefix();
            Equal(0L, Instrumentation.Hits[Instrumentation.HitLoadPhase], "the next load counts afresh");
            LoadRecorder.End();
        }

        static void ProfileOffSkipsEntityPatches()
        {
            var standard = Instrumentation.CreateSpecs(false, true).Select(x => x.Name).ToList();
            var off = Instrumentation.CreateSpecs(false, false).Select(x => x.Name).ToList();
            var deep = Instrumentation.CreateSpecs(true, true).Select(x => x.Name).ToList();
            var deepButOff = Instrumentation.CreateSpecs(true, false).Select(x => x.Name).ToList();
            Check(standard.Contains("TickableEntity.Tick") && !standard.Contains("MeteredTickableComponent.Tick"));
            Check(!off.Contains("TickableEntity.Tick") && !off.Contains("MeteredTickableComponent.Tick"), "Profile = off leaves the hottest method of the game alone");
            Check(deep.Contains("TickableEntity.Tick") && deep.Contains("MeteredTickableComponent.Tick"));
            Check(!deepButOff.Contains("TickableEntity.Tick") && !deepButOff.Contains("MeteredTickableComponent.Tick"));
            Check(off.Contains("Ticker.Update") && off.Contains("TickableEntityBucket.TickAll"), "the frame and tick timing stay");
            Check(off.Contains("SaveWriter.WriteToSaveStream"), "and so does the save that another mod runs later than SaveQueued");
        }

        static void AbandonedSaveIsForgotten()
        {
            SaveTracker.Abandon();
            Check(SaveTracker.TryOpen("first"));
            Check(!SaveTracker.TryOpen("nested"), "an open save owns the tracking");
            long later = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(SaveTracker.AbandonedAfterSeconds * 2 * System.Diagnostics.Stopwatch.Frequency);
            Check(SaveTracker.TryOpen("after the exception", later), "a save open for a minute was abandoned by an exception, so the next one is tracked");
            SaveTracker.Abandon();
            Check(!SaveTracker.IsOpen);
        }

        sealed class FakeTickService : ITickableSingletonService
        {
            public TimeSpan Duration;
            public TimeSpan LastParallelTickDuration => Duration;
            public bool ParalleTicklIsFinished => true;
            public bool IsStartingParallelTick => false;
            public event EventHandler ForcedParallelTickFinished { add { } remove { } }
            public void TickAll() { }
            public void ForceFinishParallelTick() { }
        }

        static void ParallelTickCountedOnce()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                Instrumentation.ResetForSession();
                var service = new FakeTickService { Duration = TimeSpan.FromMilliseconds(7.5) };
                for (int i = 0; i < 3; i++)   // a tick's own finish, then the forced finishes of a save
                {
                    Instrumentation.FinishParallelPrefix(out long state);
                    rig.Advance(1);
                    Instrumentation.FinishParallelPostfix(service, state);
                }
                rig.Advance(50);
                rig.Frame();
                double[] row = rig.FrameRows().Single(r => r[Columns.Type] == 'F');
                Near(7.5, row[Columns.ParTickMs], .01, "the game's figure once, not three times");
                service.Duration = TimeSpan.FromMilliseconds(3);
                Instrumentation.FinishParallelPrefix(out long next);
                Instrumentation.FinishParallelPostfix(service, next);
                rig.Advance(50);
                rig.Frame();
                Near(3.0, rig.FrameRows().Single(r => r[Columns.Type] == 'F')[Columns.ParTickMs], .01, "a new figure is added");
            }
        }

        // ---- patch bodies ----

        static void PatchBodiesPair()
        {
            Rig rig = StartRig(out _);
            using (rig)
            {
                Instrumentation.TickerPrefix(out long tick);
                Check(tick != 0, "the scope opened");
                Instrumentation.UpdatePrefix(null, out long update);
                rig.Advance(4);
                Instrumentation.ScopePostfix(update);
                rig.Advance(6);
                Instrumentation.ScopePostfix(tick);
                rig.Advance(5);
                rig.Frame();
                Probe.Stop();
                // Off: nothing opens.
                Instrumentation.TickerPrefix(out long off);
                Equal(0L, off);
                Instrumentation.ScopePostfix(off);
                Instrumentation.EntityPrefix(out Sample sample);
                Check(!sample.On);
            }
        }

        static void EntityBucketPatch()
        {
            Rig rig = StartRig(out _);
            using (rig)
            {
                Type bucketType = Reflect.GameType("Timberborn.TickSystem.TickableEntityBucket");
                // The spec's Target builds the reader of the bucket's entity count from the game's own field.
                PatchSpec spec = Instrumentation.CreateSpecs(false).Single(s => s.Name == "TickableEntityBucket.TickAll");
                MethodBase target = spec.Target();
                Equal(128, Probe.EntityBucketsPerTick);
                object bucket = Activator.CreateInstance(bucketType, true);
                for (int tick = 0; tick < 3; tick++)
                {
                    for (int i = 0; i < 128; i++)
                    {
                        Instrumentation.EntityBucketPrefix(bucket, out long state);
                        Check(state != 0);
                        target.Invoke(bucket, null);           // the game's own TickAll on an empty bucket
                        Instrumentation.ScopePostfix(state);
                        rig.Advance(0.01);
                    }
                }
                Equal(3, Probe.TickCount, "three ticks of 128 buckets");
                rig.Advance(1);
                rig.Frame();
                Probe.Stop();
            }
        }

        static void SaveTracking()
        {
            Rig rig = StartRig(out _);
            using (rig)
            {
                // Without a queued save nothing is opened, however often SaveQueued is called.
                Check(!SaveTracker.IsOpen);
                Check(SaveTracker.TryOpen("queued save"));
                Check(!SaveTracker.TryOpen("nested"), "an outer save owns the tracking");
                long t1 = SaveTracker.BeginStage(0);
                long t2 = SaveTracker.BeginStage(0);
                Check(t1 != 0 && t2 == 0, "a stage entered twice counts once (the outer wins)");
                SaveTracker.EndStage(0, t2);
                SaveTracker.EndStage(0, t1);
                long snapshot = SaveTracker.BeginStage(1);
                SaveTracker.EndStage(1, snapshot);
                SaveTracker.Close();
                Check(!SaveTracker.IsOpen);
                Check(SaveTracker.BeginStage(2) == 0, "no stage is timed outside a save");
                SaveTracker.Abandon();
                Equal(0, Instrumentation.StageOf(Reflect.Method("Timberborn.TickSystem.Ticker", "FinishFullTick")));
                Equal(0, Instrumentation.StageOf(Reflect.Method("Timberborn.TickSystem.TickableBucketService", "FinishFullTick")));
                Equal(1, Instrumentation.StageOf(Reflect.Method("Timberborn.WorldPersistence.SerializedWorldFactory", "Create", Type.EmptyTypes)));
            }
        }

        // ---- watch list and config ----

        static void WatchResolution()
        {
            var found = Watch.Resolve(new[]
            {
                "Timberborn.TickSystem.Ticker.Update",
                "Timberborn.GameSaveRuntimeSystem.GameSaver.Save",
                "Timberborn.Nope.Nothing.Method",
                "Timberborn.TickSystem.Ticker.NoSuchMethod",
                "justaname",
            });
            Watch.Candidate update = found.Single(c => c.Label.StartsWith("Timberborn.TickSystem.Ticker.Update"));
            Check(update.Ok, "Ticker.Update can be watched");
            Check(update.Label.EndsWith("Update(Single)"), "the label names the overload: " + update.Label);
            Check(update.Assembly == "Timberborn.TickSystem");
            Watch.Candidate save = found.Single(c => c.Label.StartsWith("Timberborn.GameSaveRuntimeSystem.GameSaver.Save("));
            Check(!save.Ok && save.Reason.Contains("catch ... when"), "GameSaver.Save is refused: " + save.Reason);
            Check(found.Single(c => c.Label == "Timberborn.Nope.Nothing.Method").Reason.Contains("no such type"));
            Check(found.Single(c => c.Label == "Timberborn.TickSystem.Ticker.NoSuchMethod").Reason.Contains("no method of that name"));
            Check(found.Single(c => c.Label == "justaname").Reason.Contains("Namespace.Type.Method"));
        }

        static void ConfigParsing()
        {
            var config = new Config();
            // Defaults capture the most detail a fresh install can, with nothing configured: deep profiling, every spike slot, a
            // doubled sampling budget.
            Equal(true, config.Enabled); Equal(Config.ProfileDeep, config.Profile); Equal(50.0, config.SlowFrameMs);
            Equal(PerformanceLog.Profile.TopK, config.SpikeContributors); Equal(1.0, config.OverheadBudgetPercent);
            Check(config.Deep && config.SamplesEntities, "deep by default");
            config.Apply(Config.Parse(new[]
            {
                "# a comment",
                "Enabled = true   # trailing comment",
                "SlowFrameMs=33.5", "SummarySeconds = 0", "ProfileSeconds=100000", "Profile = STANDARD", "SpikeContributors = 99",
                "OverheadBudgetPercent = 1", "OutputFolder = D:\\logs", "MaxSlowRowsPerMinute = 5",
                "Watch = A.B.C; D.E.F",
                "Watch = G.H.I",
                "no equals sign", "=novalue",
            }));
            Equal(33.5, config.SlowFrameMs);
            Equal(1.0, config.SummarySeconds, "clamped up");
            Equal(1800.0, config.ProfileSeconds, "clamped down");
            Equal(Config.ProfileStandard, config.Profile);
            Check(!config.Deep && config.SamplesEntities, "the .cfg file can still move away from the deep default");
            Equal(Profile.TopK, config.SpikeContributors);   // clamped down from 99
            Equal(1.0, config.OverheadBudgetPercent);
            Equal("D:\\logs", config.OutputFolder);
            Equal(10, config.MaxSlowRowsPerMinute);   // clamped up
            Check(config.Watch.SequenceEqual(new[] { "A.B.C", "D.E.F", "G.H.I" }), "Watch may repeat and use ;");
            Equal(0, config.Problems.Count);
            var off = new Config();
            off.Apply(Config.Parse(new[] { "Profile=off" }));
            Check(!off.SamplesEntities && !off.Deep);
        }

        static void ConfigProblems()
        {
            var config = new Config();
            config.Apply(Config.Parse(new[] { "Enabled = maybe", "SlowFrameMs = fast", "Profile = extreme", "Surprise = 1" }));
            Equal(true, config.Enabled); Equal(50.0, config.SlowFrameMs); Equal(Config.ProfileDeep, config.Profile);
            Equal(4, config.Problems.Count);
            Check(config.Problems.Any(p => p.Contains("Enabled")) && config.Problems.Any(p => p.Contains("unknown setting Surprise")));
            var many = new Config();
            many.Apply(Config.Parse(new[] { "Watch = " + string.Join(";", Enumerable.Range(0, 60).Select(i => "T.T" + i + ".M")) }));
            Equal(Config.MaxWatched, many.Watch.Count);
            Check(many.Problems.Any(p => p.Contains("first " + Config.MaxWatched)));

            Log.Sink = _ => { }; Log.WarningSink = _ => { };
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perflog-config-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                Equal(true, Config.Load(dir, null, "").Enabled);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, Config.FileName), "Enabled = false\nSlowFrameMs = 20\n");
                Config loaded = Config.Load(null, dir);
                Equal(false, loaded.Enabled); Equal(20.0, loaded.SlowFrameMs);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        static void SettingsApplyTo()
        {
            // Constructed with no real Mod Settings dependencies: ApplyTo never touches them (it only reads .Value off each property),
            // so this exercises the real clamping logic without needing ISettings/ModRepository/ModSettingsOwnerRegistry fakes.
            var owner = new PerformanceSettings(null, null, null);
            owner.SlowFrameMs.SetValue(999999);          // clamped by the widget itself to Config.SlowFrameMsMax
            owner.SummarySeconds.SetValue(-5);           // clamped to Config.SummarySecondsMin
            owner.ProfileSeconds.SetValue(42);
            owner.SpikeContributors.SetValue(-3);         // clamped to 0
            owner.MaxSlowRowsPerMinute.SetValue(1);       // clamped to Config.MaxSlowRowsPerMinuteMin
            owner.OverheadBudgetPercent.SetValue(999f);   // not self-clamping: ApplyTo must clamp it
            var cfg = new Config();
            owner.ApplyTo(cfg);
            Equal(Config.SlowFrameMsMax, cfg.SlowFrameMs);
            Equal(Config.SummarySecondsMin, cfg.SummarySeconds);
            Equal(42.0, cfg.ProfileSeconds);
            Equal(0, cfg.SpikeContributors);
            Equal((int)Config.MaxSlowRowsPerMinuteMin, cfg.MaxSlowRowsPerMinute);
            Equal(Config.OverheadBudgetPercentMax, cfg.OverheadBudgetPercent);
            // Enabled, Profile, Watch and OutputFolder decide which patches exist, so ApplyTo must never touch them.
            Equal(true, cfg.Enabled); Equal(Config.ProfileDeep, cfg.Profile); Equal(0, cfg.Watch.Count); Equal("", cfg.OutputFolder);
        }

        static void SettingsSeedFromTheCfgFile()
        {
            // .Value stays the C# default (0) until Mod Settings calls Load(), which needs a real ISettings/ModRepository; what this mod
            // controls, and what Load() would seed .Value from the first time (no Mod Settings save file yet), is DefaultValue.
            Config previous = Plugin.SetConfigForTest(new Config { SlowFrameMs = 77, SpikeContributors = 2 });
            try
            {
                var owner = new PerformanceSettings(null, null, null);
                Equal(77, owner.SlowFrameMs.DefaultValue, "the panel starts from what PerformanceLog.cfg already had, not the field default (50)");
                Equal(2, owner.SpikeContributors.DefaultValue);
            }
            finally { Plugin.SetConfigForTest(previous); }

            Plugin.SetConfigForTest(null);
            try
            {
                var owner = new PerformanceSettings(null, null, null);
                Equal((int)Config.SlowFrameMsDefault, owner.SlowFrameMs.DefaultValue, "no Config yet (StartMod has not run): the field default");
            }
            finally { Plugin.SetConfigForTest(previous); }
        }

        static void HarmonyInfo()
        {
            // Not a pass/fail check of the mod: it records whether this environment can apply Harmony patches at all.
            string result;
            try
            {
                var harmony = new Harmony("perflog.test");
                MethodInfo dummy = typeof(GameBindingTests).GetMethod(nameof(Dummy), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(dummy, new HarmonyMethod(typeof(GameBindingTests).GetMethod(nameof(DummyPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Dummy();
                harmony.UnpatchAll("perflog.test");
                result = "patches can be applied here";
            }
            catch (Exception e) { result = "patches cannot be applied here (" + e.GetType().Name + "); they are validated instead"; }
            Console.WriteLine("     info: " + result);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Dummy() { }
        static void DummyPrefix() { }
    }
}
