using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace PerformanceLog
{
    /// <summary>One method of the game that is patched, and what to run before and after it.</summary>
    internal sealed class PatchSpec
    {
        public string Name;
        /// <summary>Not being able to make this patch is worth a warning at the top of the summary.</summary>
        public bool Required;
        public string Purpose;
        public Func<MethodBase> Target;
        public MethodInfo Prefix, Postfix;
        /// <summary>Time a whole scope: the prefix runs before every other mod's, the postfix after them, so the scope contains their patches.</summary>
        public bool Scope;
        /// <summary>Only with Profile = deep.</summary>
        public bool DeepOnly;
        public int Hit;
    }

    /// <summary>
    /// The patches that measure the game. Every patch only reads a clock, a counter or a field, and is wrapped so that a failure
    /// in it can never reach the game. Every one starts by reading one flag, so with no log running it costs almost nothing.
    /// None of them changes what the game does.
    /// </summary>
    internal static class Instrumentation
    {
        // Indexes into Hits, one for each patch, for the capability lines at the end of a log.
        internal const int HitTicker = 0, HitTickAll = 1, HitFinishParallel = 2, HitTickSingletons = 3, HitStartParallel = 4, HitEntityBucket = 5, HitEntity = 6,
            HitComponent = 7, HitUpdate = 8, HitLateUpdate = 9, HitLoadAll = 10, HitLoadPhase = 11, HitTickServiceLoad = 12, HitSaveQueued = 13,
            HitSaveInstant = 14, HitSaveStage = 15, HitWatch = 16, HitSaveWriter = 17, HitCount = 18;

        internal static readonly long[] Hits = new long[HitCount];
        /// <summary>Which patches were actually made, so a log can say "not installed" instead of "never ran".</summary>
        internal static readonly bool[] InstalledHit = new bool[HitCount];

        static readonly string[] hitNames =
        {
            "Ticker.Update", "TickableSingletonService.TickAll", "TickableSingletonService.FinishParallelTick", "TickableSingletonService.TickSingletons",
            "TickableSingletonService.StartParallelTick", "TickableEntityBucket.TickAll", "TickableEntity.Tick (sampled calls)", "MeteredTickableComponent.Tick (sampled calls)",
            "SingletonLifecycleService.UpdateSingletons", "SingletonLifecycleService.LateUpdateSingletons", "SingletonLifecycleService.LoadAll",
            "SingletonLifecycleService load phases", "singleton wrappers put in place", "GameSaver.SaveQueued", "GameSaver.SaveInstantlySkippingNameValidation",
            "save stages", "watched methods (sampled calls)", "SaveWriter.WriteToSaveStream",
        };

        public static string HitName(int index) => hitNames[index];

        public const string HarmonyId = Plugin.ModId;

        static Harmony harmony;
        internal static Func<object, int> EntityBucketCountReader => entityBucketCount;
        static Func<object, int> entityBucketCount;
        static Func<object, string> entityName;
        static Func<object, object> componentOf;
        static Func<object, object> queuedSaveOf;

        /// <summary>The result of trying to make each patch: name, purpose and whether it worked or why not.</summary>
        internal static readonly List<string> Results = new List<string>();
        internal static int Installed { get; private set; }
        internal static int Failed { get; private set; }

        // ---- what is patched ----

        internal static List<PatchSpec> CreateSpecs(bool deep, bool entities = true)
        {
            Type self = typeof(Instrumentation);
            var specs = new List<PatchSpec>
            {
                new PatchSpec
                {
                    Name = "Ticker.Update", Required = true, Scope = true, Hit = HitTicker,
                    Purpose = "the simulation's share of each frame (tickMs and everything nested in it)",
                    Target = () => Reflect.Method("Timberborn.TickSystem.Ticker", "Update"),
                    Prefix = Reflect.Own(self, nameof(TickerPrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "TickableSingletonService.TickAll", Required = false, Hit = HitTickAll,
                    Purpose = "counts the singleton bucket of a tick",
                    Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickAll"),
                    Prefix = Reflect.Own(self, nameof(TickAllPrefix)),
                },
                new PatchSpec
                {
                    Name = "TickableSingletonService.FinishParallelTick", Required = false, Scope = true, Hit = HitFinishParallel,
                    Purpose = "parWaitMs: the game thread waiting for the parallel tick",
                    Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "FinishParallelTick"),
                    Prefix = Reflect.Own(self, nameof(FinishParallelPrefix)), Postfix = Reflect.Own(self, nameof(FinishParallelPostfix)),
                },
                new PatchSpec
                {
                    Name = "TickableSingletonService.TickSingletons", Required = false, Scope = true, Hit = HitTickSingletons,
                    Purpose = "singMs: the once-per-tick singletons",
                    Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "TickSingletons"),
                    Prefix = Reflect.Own(self, nameof(TickSingletonsPrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "TickableSingletonService.StartParallelTick", Required = false, Scope = true, Hit = HitStartParallel,
                    Purpose = "parStartMs: starting the parallel tick",
                    Target = () => Reflect.Method("Timberborn.TickSystem.TickableSingletonService", "StartParallelTick"),
                    Prefix = Reflect.Own(self, nameof(StartParallelPrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "TickableEntityBucket.TickAll", Required = true, Scope = true, Hit = HitEntityBucket,
                    Purpose = "entMs: every entity's tick, and counting ticks",
                    Target = () =>
                    {
                        Type bucket = Reflect.GameType("Timberborn.TickSystem.TickableEntityBucket");
                        entityBucketCount = Reflect.CountOfField(bucket, "_tickableEntities");
                        ReadEntityBucketsPerTick();
                        return AccessTools.Method(bucket, "TickAll");
                    },
                    Prefix = Reflect.Own(self, nameof(EntityBucketPrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "SingletonLifecycleService.UpdateSingletons", Required = false, Scope = true, Hit = HitUpdate,
                    Purpose = "updMs: the per-frame singleton updates",
                    Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateSingletons"),
                    Prefix = Reflect.Own(self, nameof(UpdatePrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "SingletonLifecycleService.LateUpdateSingletons", Required = false, Scope = true, Hit = HitLateUpdate,
                    Purpose = "lateMs: the per-frame late singleton updates",
                    Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateSingletons"),
                    Prefix = Reflect.Own(self, nameof(LateUpdatePrefix)), Postfix = Reflect.Own(self, nameof(ScopePostfix)),
                },
                new PatchSpec
                {
                    Name = "SingletonLifecycleService.LoadAll", Required = false, Hit = HitLoadAll,
                    Purpose = "times loading",
                    Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", "LoadAll"),
                    Prefix = Reflect.Own(self, nameof(LoadAllPrefix)), Postfix = Reflect.Own(self, nameof(LoadAllPostfix)),
                },
            };
            foreach (string phase in new[] { "LoadSingletons", "LoadNonSingletons", "PostLoadSingletons", "PostLoadNonSingletons" })
            {
                string name = phase;
                specs.Add(new PatchSpec
                {
                    Name = "SingletonLifecycleService." + name, Required = false, Hit = HitLoadPhase,
                    Purpose = "times each step of loading",
                    Target = () => Reflect.Method("Timberborn.SingletonSystem.SingletonLifecycleService", name),
                    Prefix = Reflect.Own(self, nameof(LoadPhasePrefix)), Postfix = Reflect.Own(self, nameof(LoadPhasePostfix)),
                });
            }
            if (entities) specs.Add(new PatchSpec
            {
                Name = "TickableEntity.Tick", Required = false, Hit = HitEntity,
                Purpose = "profile.csv: which kinds of entity the tick's time goes to (sampled)",
                Target = () =>
                {
                    Type entity = Reflect.GameType("Timberborn.TickSystem.TickableEntity");
                    entityName = Reflect.FieldGetter<string>(entity, "_originalName");
                    return AccessTools.Method(entity, "Tick");
                },
                Prefix = Reflect.Own(self, nameof(EntityPrefix)), Postfix = Reflect.Own(self, nameof(EntityPostfix)),
            });
            specs.Add(new PatchSpec
            {
                Name = "GameSaver.SaveQueued", Required = false, Scope = true, Hit = HitSaveQueued,
                Purpose = "saveMs: autosaves and manual saves",
                Target = () =>
                {
                    Type saver = Reflect.GameType("Timberborn.GameSaveRuntimeSystem.GameSaver");
                    queuedSaveOf = Reflect.FieldGetter<object>(saver, "_queuedSave");
                    return AccessTools.Method(saver, "SaveQueued");
                },
                Prefix = Reflect.Own(self, nameof(SaveQueuedPrefix)), Postfix = Reflect.Own(self, nameof(SavePostfix)),
            });
            specs.Add(new PatchSpec
            {
                Name = "GameSaver.SaveInstantlySkippingNameValidation", Required = false, Scope = true, Hit = HitSaveInstant,
                Purpose = "saveMs: saves made at once (on exit, for example)",
                Target = () => Reflect.Method("Timberborn.GameSaveRuntimeSystem.GameSaver", "SaveInstantlySkippingNameValidation"),
                Prefix = Reflect.Own(self, nameof(SaveInstantPrefix)), Postfix = Reflect.Own(self, nameof(SavePostfix)),
            });
            // A mod that runs the game's save itself (BeaverBuddies defers it to the end of a tick) returns from SaveQueued at once, so the time of
            // the save is only visible where the world is written. Whichever hook is entered first owns the save.
            specs.Add(new PatchSpec
            {
                Name = "SaveWriter.WriteToSaveStream", Required = false, Scope = true, Hit = HitSaveWriter,
                Purpose = "saveMs: a save that another mod runs later than SaveQueued does",
                Target = () => Reflect.Method("Timberborn.SaveSystem.SaveWriter", "WriteToSaveStream"),
                Prefix = Reflect.Own(self, nameof(SaveWriterPrefix)), Postfix = Reflect.Own(self, nameof(SavePostfix)),
            });
            // Two hooks for the first stage: the one on Ticker is so short that the runtime may inline it, and the one on the bucket
            // service is called through an interface. Whichever is entered first counts; the one nested inside it does not.
            AddStage(specs, "Ticker.FinishFullTick", 0, () => Reflect.Method("Timberborn.TickSystem.Ticker", "FinishFullTick"));
            AddStage(specs, "TickableBucketService.FinishFullTick", 0, () => Reflect.Method("Timberborn.TickSystem.TickableBucketService", "FinishFullTick"));
            AddStage(specs, "SerializedWorldFactory.Create", 1, () =>
            {
                Type factory = Reflect.GameType("Timberborn.WorldPersistence.SerializedWorldFactory");
                return factory == null ? null : AccessTools.Method(factory, "Create", Type.EmptyTypes);
            });
            AddStage(specs, "WorldSerializer.WriteToSaveEntryStream", 2, () => Reflect.Method("Timberborn.WorldSerialization.WorldSerializer", "WriteToSaveEntryStream"));
            AddStage(specs, "ThumbnailSaveEntryWriter.WriteToSaveEntryStream", 3, () => Reflect.Method("Timberborn.ThumbnailCapturing.ThumbnailSaveEntryWriter", "WriteToSaveEntryStream"));
            if (deep && entities)
            {
                specs.Add(new PatchSpec
                {
                    Name = "MeteredTickableComponent.Tick", Required = false, DeepOnly = true, Hit = HitComponent,
                    Purpose = "profile.csv: which entity components the tick's time goes to (sampled; Profile = deep)",
                    Target = () =>
                    {
                        Type component = Reflect.GameType("Timberborn.TickSystem.MeteredTickableComponent");
                        componentOf = Reflect.FieldGetter<object>(component, "_tickableComponent");
                        return AccessTools.Method(component, "Tick");
                    },
                    Prefix = Reflect.Own(self, nameof(ComponentPrefix)), Postfix = Reflect.Own(self, nameof(ComponentPostfix)),
                });
            }
            return specs;
        }

        static void AddStage(List<PatchSpec> specs, string name, int stage, Func<MethodBase> target)
        {
            specs.Add(new PatchSpec
            {
                Name = name, Required = false, Hit = HitSaveStage,
                Purpose = "the stages of a save in events.csv",
                Target = target,
                Prefix = Reflect.Own(typeof(Instrumentation), nameof(SaveStagePrefix)),
                Postfix = Reflect.Own(typeof(Instrumentation), nameof(SaveStagePostfix)),
            });
        }

        internal static void ReadEntityBucketsPerTick()
        {
            try
            {
                Type service = Reflect.GameType("Timberborn.TickSystem.TickableBucketService");
                FieldInfo field = service == null ? null : AccessTools.Field(service, "NumberOfEntityBuckets");
                if (field != null && field.GetValue(null) is int buckets && buckets > 0) Probe.EntityBucketsPerTick = buckets;
            }
            catch (Exception) { }
        }

        // ---- installing ----

        /// <summary>Makes every patch that can be made. A patch that cannot be made is recorded, not fatal.</summary>
        internal static void Install(Config config)
        {
            Results.Clear(); Installed = 0; Failed = 0;
            harmony = new Harmony(HarmonyId);
            foreach (PatchSpec spec in CreateSpecs(config.Deep, config.SamplesEntities))
                InstallOne(spec);
            InstalledHit[HitTickServiceLoad] = true; // the wrappers are put in place by the patches above, on the first tick and frame
            MeasurePatchCost();
            Log.Info("Patches: " + Installed + " installed, " + Failed + " could not be made.");
        }

        static void InstallOne(PatchSpec spec)
        {
            try
            {
                MethodBase target = spec.Target();
                if (target == null) throw new MissingMethodException("the game has no such method (it may have changed in this version)");
                if (Reflect.HasExceptionFilter(target)) throw new NotSupportedException("the method has a catch ... when clause, which Harmony cannot patch under Mono");
                HarmonyMethod prefix = spec.Prefix == null ? null : new HarmonyMethod(spec.Prefix) { priority = spec.Scope ? Priority.First : Priority.Normal };
                HarmonyMethod postfix = spec.Postfix == null ? null : new HarmonyMethod(spec.Postfix) { priority = spec.Scope ? Priority.Last : Priority.Normal };
                harmony.Patch(target, prefix, postfix);
                Installed++;
                InstalledHit[spec.Hit] = true;
                Results.Add(spec.Name + "|installed|" + spec.Purpose);
            }
            catch (Exception e)
            {
                Failed++;
                string why = e.InnerException != null ? e.InnerException.Message : e.Message;
                Results.Add(spec.Name + "|not installed: " + why + "|" + spec.Purpose);
                Log.Warning((spec.Required ? "Required patch " : "Patch ") + spec.Name + " could not be made (" + why + "); " + spec.Purpose + " will be missing.");
            }
        }

        /// <summary>
        /// What running one of these patches costs when it does nothing, by patching a method of our own. Both loops are warmed up first (the first
        /// call of a method is compiled, and a patched one goes through a freshly made wrapper), and the least of a few rounds is taken, so
        /// one-off costs and the scheduler do not decide the figure. In the first game recording this read 0 because the unwarmed baseline
        /// included the compile.
        /// </summary>
        internal static void MeasurePatchCost()
        {
            try
            {
                MethodInfo target = Reflect.Own(typeof(Instrumentation), nameof(CostTarget));
                const int repeats = 4000, rounds = 5;
                for (int i = 0; i < 400; i++) CostTarget(i);
                double baseline = TimeCostTarget(repeats, rounds);
                harmony.Patch(target, new HarmonyMethod(Reflect.Own(typeof(Instrumentation), nameof(CostPrefix))), new HarmonyMethod(Reflect.Own(typeof(Instrumentation), nameof(CostPostfix))));
                for (int i = 0; i < 400; i++) CostTarget(i);
                double patched = TimeCostTarget(repeats, rounds);
                harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId);
                Probe.PatchCallTicks = Math.Max(0, patched - baseline);
            }
            catch (Exception e) { Log.Warning("Could not measure what a patch costs: " + e.Message); }
        }

        /// <summary>Stopwatch ticks for one call of <see cref="CostTarget"/>: the fastest of several rounds.</summary>
        static double TimeCostTarget(int repeats, int rounds)
        {
            double best = double.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) CostTarget(i);
                best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) / (double)repeats);
            }
            return best;
        }

        static long costSink;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void CostTarget(int i) { costSink += i; }
        internal static void CostPrefix(out long __state) { __state = 0; }
        internal static void CostPostfix(long __state) { costSink += __state; }

        // ---- the patch bodies ----
        // Each starts with a read of Probe.Enabled. Nothing here may throw into the game, so anything that reads the game's own objects is
        // wrapped. The 'scope' pairs put the token Probe.Begin returned into __state and hand it to Probe.End.

        internal static void TickerPrefix(out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitTicker]++;
            // If another mod replaced Unity's player loop, the frame markers are gone and no frame would ever be closed: notice, and try once more.
            if (Probe.FrameNumber == 0) Session.CheckFramesArriving();
            __state = Probe.Begin(Slot.Tick);
        }

        internal static void ScopePostfix(long __state)
        {
            if (__state != 0) Probe.End(__state);
        }

        internal static void TickAllPrefix(object __instance)
        {
            if (!Probe.Enabled) return;
            Hits[HitTickAll]++;
            EnsureTickSingletonsWrapped(__instance);
            Probe.NoteBucket();
        }

        internal static void FinishParallelPrefix(out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitFinishParallel]++;
            __state = Probe.Begin(Slot.ParallelWait);
        }

        internal static void FinishParallelPostfix(object __instance, long __state)
        {
            if (__state == 0) return;
            Probe.End(__state);
            try
            {
                // The game's figure only changes when a parallel tick finishes; a forced finish (during a save) would otherwise add it again.
                TimeSpan duration = ((ITickableSingletonService)__instance).LastParallelTickDuration;
                if (duration != lastParallelTick)
                {
                    lastParallelTick = duration;
                    Probe.NoteParallelTick(duration.TotalMilliseconds);
                }
            }
            catch (Exception) { }
        }

        static TimeSpan lastParallelTick;

        internal static void TickSingletonsPrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitTickSingletons]++;
            EnsureTickSingletonsWrapped(__instance);
            __state = Probe.Begin(Slot.Singletons);
        }

        internal static void StartParallelPrefix(out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitStartParallel]++;
            __state = Probe.Begin(Slot.ParallelStart);
        }

        internal static void EntityBucketPrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitEntityBucket]++;
            Probe.NoteEntityBucket();
            __state = Probe.Begin(Slot.Entities);
            if (__state == 0) return;
            try { Probe.Count(Counter.Entities, entityBucketCount(__instance)); }
            catch (Exception) { }
        }

        internal static void EntityPrefix(out Sample __state)
        {
            if (!Probe.Enabled) { __state = default; return; }
            Probe.Count(Counter.PatchCalls);
            __state = Profile.BeginEntity();
        }

        internal static void EntityPostfix(object __instance, Sample __state)
        {
            if (!__state.On) return;
            Hits[HitEntity]++;
            string name = null;
            try { name = entityName(__instance); }
            catch (Exception) { }
            Profile.EndEntity(name, __state);
        }

        internal static void ComponentPrefix(out Sample __state)
        {
            if (!Probe.Enabled) { __state = default; return; }
            Probe.Count(Counter.PatchCalls);
            __state = Profile.BeginComponent();
        }

        internal static void ComponentPostfix(object __instance, Sample __state)
        {
            if (!__state.On) return;
            Hits[HitComponent]++;
            Type type = null;
            try { type = componentOf(__instance)?.GetType(); }
            catch (Exception) { }
            Profile.EndComponent(type, __state);
        }

        internal static void UpdatePrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitUpdate]++;
            EnsureUpdatableWrapped(__instance);
            __state = Probe.Begin(Slot.Update);
        }

        internal static void LateUpdatePrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            Hits[HitLateUpdate]++;
            EnsureLateUpdatableWrapped(__instance);
            __state = Probe.Begin(Slot.LateUpdate);
        }

        // ---- loading and the per-frame arrays ----

        // The wrappers are put into the game's arrays on the first tick and the first frame, not when a scene loads. By then every other mod's postfix on
        // Load has run, so a mod that looks at what is in those arrays (BeaverBuddies reorders the once-per-tick singletons by their type) still sees the
        // game's own singletons, and tick order is what it would be without this mod. Once per service, and the services are held weakly so an old game
        // is not kept alive. More than one service is alive at a time (the game's has its own, and the application's updates every frame too), and they
        // alternate within a frame, so a single remembered service would be swapped back and forth: the first recording counted 488601 swaps in 122150 frames.
        static readonly ConditionalWeakTable<object, object> tickWrapped = new ConditionalWeakTable<object, object>(),
            updateWrapped = new ConditionalWeakTable<object, object>(), lateWrapped = new ConditionalWeakTable<object, object>();
        static readonly object wrappedMarker = new object();

        /// <summary>True the first time a service is offered to the table, false ever after (and for null).</summary>
        static bool FirstTime(ConditionalWeakTable<object, object> seen, object service)
        {
            if (service == null || seen.TryGetValue(service, out _)) return false;
            try { seen.Add(service, wrappedMarker); }
            catch (ArgumentException) { return false; }
            return true;
        }

        internal static void EnsureTickSingletonsWrapped(object service)
        {
            if (!FirstTime(tickWrapped, service)) return;
            try
            {
                SingletonArrays.SwapTickSingletons(service);
                SingletonArrays.Swap<IParallelTickableSingleton>(service, "_parallelTickableSingletons", x => new TimedParallelStart(x));
                Hits[HitTickServiceLoad]++;
            }
            catch (Exception e) { Log.Warning("Could not time the singletons that tick: " + e.Message); }
        }

        internal static void EnsureUpdatableWrapped(object service)
        {
            if (!FirstTime(updateWrapped, service)) return;
            try
            {
                SingletonArrays.Swap<IUpdatableSingleton>(service, "_updatableSingletons", x => new TimedUpdatable(x));
                Hits[HitTickServiceLoad]++;
            }
            catch (Exception e) { Log.Warning("Could not time the singletons that update every frame: " + e.Message); }
        }

        internal static void EnsureLateUpdatableWrapped(object service)
        {
            if (!FirstTime(lateWrapped, service)) return;
            try
            {
                SingletonArrays.Swap<ILateUpdatableSingleton>(service, "_lateUpdatableSingletons", x => new TimedLateUpdatable(x));
                Hits[HitTickServiceLoad]++;
            }
            catch (Exception e) { Log.Warning("Could not time the singletons that update late every frame: " + e.Message); }
        }

        /// <summary>Forgets what the last session counted. Called when a session starts.</summary>
        internal static void ResetForSession()
        {
            // A session starts in the middle of loading (in PostLoad), after the loading patches have counted, so those two counters are not
            // cleared: LoadAllPrefix starts them afresh for each load. (The first recording said LoadAll "never ran" for this reason.)
            long loadAll = Hits[HitLoadAll], loadPhases = Hits[HitLoadPhase];
            Array.Clear(Hits, 0, Hits.Length);
            Hits[HitLoadAll] = loadAll; Hits[HitLoadPhase] = loadPhases;
            lastParallelTick = default;
            SaveTracker.Abandon();
        }

        internal static void LoadAllPrefix()
        {
            try
            {
                Hits[HitLoadAll] = 1; Hits[HitLoadPhase] = 0;
                LoadRecorder.Begin();
                Milestones.Mark("load-begin");
            }
            catch (Exception) { }
        }

        internal static void LoadAllPostfix(object __instance)
        {
            try
            {
                LoadRecorder.End();
                Milestones.Mark("load-end");
                Session.OnLoadFinished();
            }
            catch (Exception) { }
        }

        internal static void LoadPhasePrefix(object __instance, MethodBase __originalMethod, out long __state)
        {
            __state = 0;
            try
            {
                if (!LoadRecorder.Active) return;
                Hits[HitLoadPhase]++;
                switch (__originalMethod.Name)
                {
                    case "LoadSingletons": SingletonArrays.Swap<ILoadableSingleton>(__instance, "_loadableSingletons", x => new TimedLoadable(x)); break;
                    case "LoadNonSingletons": SingletonArrays.Swap<INonSingletonLoader>(__instance, "_nonSingletonLoaders", x => new TimedNonSingletonLoader(x)); break;
                    case "PostLoadSingletons": SingletonArrays.Swap<IPostLoadableSingleton>(__instance, "_postLoadableSingletons", x => new TimedPostLoadable(x)); break;
                    case "PostLoadNonSingletons": SingletonArrays.Swap<INonSingletonPostLoader>(__instance, "_nonSingletonPostLoaders", x => new TimedNonSingletonPostLoader(x)); break;
                }
                __state = Stopwatch.GetTimestamp();
            }
            catch (Exception e) { Log.Warning("Could not time a step of loading: " + e.Message); }
        }

        internal static void LoadPhasePostfix(MethodBase __originalMethod, long __state)
        {
            try { if (__state != 0) LoadRecorder.PhaseEnd(__originalMethod.Name, Stopwatch.GetTimestamp() - __state); }
            catch (Exception) { }
        }

        // ---- saving ----
        // GameSaver.Save itself must NOT be patched: it has a catch with an exception filter, which Harmony cannot regenerate under
        // Mono (it crashed the game in another mod). Its callers are hooked instead, and Install refuses any target with a filter.

        internal static void SaveQueuedPrefix(object __instance, out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            try
            {
                if (queuedSaveOf(__instance) == null) return; // nothing queued: this is called every frame
                if (!SaveTracker.TryOpen("queued save")) return;
                Hits[HitSaveQueued]++;
                __state = Probe.Begin(Slot.Save);
                if (__state == 0) SaveTracker.Abandon();
            }
            catch (Exception) { }
        }

        internal static void SaveWriterPrefix(out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            try
            {
                if (!SaveTracker.TryOpen("save (writing the world)")) return; // an outer hook already owns this save
                Hits[HitSaveWriter]++;
                __state = Probe.Begin(Slot.Save);
                if (__state == 0) SaveTracker.Abandon();
            }
            catch (Exception) { }
        }

        internal static void SaveInstantPrefix(out long __state)
        {
            __state = 0;
            if (!Probe.Enabled) return;
            try
            {
                if (!SaveTracker.TryOpen("instant save")) return;
                Hits[HitSaveInstant]++;
                __state = Probe.Begin(Slot.Save);
                if (__state == 0) SaveTracker.Abandon();
            }
            catch (Exception) { }
        }

        internal static void SavePostfix(long __state)
        {
            if (__state == 0) return;
            try { Probe.End(__state); }
            finally { SaveTracker.Close(); }
        }

        internal static void SaveStagePrefix(MethodBase __originalMethod, out long __state)
        {
            __state = 0;
            try
            {
                if (!SaveTracker.IsOpen) return;
                Hits[HitSaveStage]++;
                __state = SaveTracker.BeginStage(StageOf(__originalMethod));
            }
            catch (Exception) { }
        }

        internal static void SaveStagePostfix(MethodBase __originalMethod, long __state)
        {
            try { SaveTracker.EndStage(StageOf(__originalMethod), __state); }
            catch (Exception) { }
        }

        internal static int StageOf(MethodBase method)
        {
            switch (method.DeclaringType?.Name)
            {
                case "Ticker":
                case "TickableBucketService": return 0;
                case "SerializedWorldFactory": return 1;
                case "WorldSerializer": return 2;
                default: return 3;
            }
        }
    }
}
