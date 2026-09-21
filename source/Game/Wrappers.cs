using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace PerformanceLog
{
    /// <summary>Marks a wrapper so an array is never wrapped twice.</summary>
    internal interface ITimedWrapper { }

    internal static class Wrap
    {
        /// <summary>The key id of a class, looked up again when the profile has been reset since it was cached.</summary>
        public static int Resolve(ref int id, ref int generation, ProfileKind kind, Type type)
        {
            if (generation != Profile.Generation)
            {
                id = Profile.IdFor(kind, type);
                generation = Profile.Generation;
            }
            return id;
        }
    }

    // The game calls every singleton through an interface from an array it builds when the scene loads. These stand in for the
    // singletons in those arrays: they call the real one, timing the call. There is no patch inside the hot loop, so nothing here
    // depends on the runtime not inlining a method, and a mod's patch on the singleton's own method is inside the timing, as it should be.
    // When the log is off, or off the game thread, a wrapper is one flag and one call.

    internal sealed class TimedTickable : ITickableSingleton, ITimedWrapper
    {
        readonly ITickableSingleton inner;
        readonly Type type;
        int id, generation;

        public TimedTickable(ITickableSingleton inner) { this.inner = inner; type = inner.GetType(); }

        public void Tick()
        {
            if (!Probe.Enabled || !Probe.OnGameThread) { inner.Tick(); return; }
            int key = Wrap.Resolve(ref id, ref generation, ProfileKind.TickSingleton, type);
            Timing t = Profile.BeginExact();
            try { inner.Tick(); }
            finally { Profile.EndExact(key, t); }
        }
    }

    internal sealed class TimedUpdatable : IUpdatableSingleton, ITimedWrapper
    {
        readonly IUpdatableSingleton inner;
        readonly Type type;
        int id, generation;

        public TimedUpdatable(IUpdatableSingleton inner) { this.inner = inner; type = inner.GetType(); }

        public void UpdateSingleton()
        {
            if (!Probe.Enabled || !Probe.OnGameThread) { inner.UpdateSingleton(); return; }
            int key = Wrap.Resolve(ref id, ref generation, ProfileKind.UpdateSingleton, type);
            Timing t = Profile.BeginExact();
            try { inner.UpdateSingleton(); }
            finally { Profile.EndExact(key, t); }
        }
    }

    internal sealed class TimedLateUpdatable : ILateUpdatableSingleton, ITimedWrapper
    {
        readonly ILateUpdatableSingleton inner;
        readonly Type type;
        int id, generation;

        public TimedLateUpdatable(ILateUpdatableSingleton inner) { this.inner = inner; type = inner.GetType(); }

        public void LateUpdateSingleton()
        {
            if (!Probe.Enabled || !Probe.OnGameThread) { inner.LateUpdateSingleton(); return; }
            int key = Wrap.Resolve(ref id, ref generation, ProfileKind.LateSingleton, type);
            Timing t = Profile.BeginExact();
            try { inner.LateUpdateSingleton(); }
            finally { Profile.EndExact(key, t); }
        }
    }

    internal sealed class TimedParallelStart : IParallelTickableSingleton, ITimedWrapper
    {
        readonly IParallelTickableSingleton inner;
        readonly Type type;
        int id, generation;

        public TimedParallelStart(IParallelTickableSingleton inner) { this.inner = inner; type = inner.GetType(); }

        public void StartParallelTick()
        {
            if (!Probe.Enabled || !Probe.OnGameThread) { inner.StartParallelTick(); return; }
            int key = Wrap.Resolve(ref id, ref generation, ProfileKind.ParallelStart, type);
            Timing t = Profile.BeginExact();
            try { inner.StartParallelTick(); }
            finally { Profile.EndExact(key, t); }
        }
    }

    // Loading is timed whether or not a log has started: the log starts in the middle of loading, and what came before is kept
    // until it does. See LoadRecorder.

    internal sealed class TimedLoadable : ILoadableSingleton, ITimedWrapper
    {
        readonly ILoadableSingleton inner;
        public TimedLoadable(ILoadableSingleton inner) { this.inner = inner; }

        public void Load()
        {
            if (!LoadRecorder.Active) { inner.Load(); return; }
            long start = Stopwatch.GetTimestamp();
            try { inner.Load(); }
            finally { LoadRecorder.Add(ProfileKind.Load, inner.GetType(), Stopwatch.GetTimestamp() - start); }
        }
    }

    internal sealed class TimedNonSingletonLoader : INonSingletonLoader, ITimedWrapper
    {
        readonly INonSingletonLoader inner;
        public TimedNonSingletonLoader(INonSingletonLoader inner) { this.inner = inner; }

        public void LoadNonSingletons()
        {
            if (!LoadRecorder.Active) { inner.LoadNonSingletons(); return; }
            long start = Stopwatch.GetTimestamp();
            try { inner.LoadNonSingletons(); }
            finally { LoadRecorder.Add(ProfileKind.LoadNonSingleton, inner.GetType(), Stopwatch.GetTimestamp() - start); }
        }
    }

    internal sealed class TimedPostLoadable : IPostLoadableSingleton, ITimedWrapper
    {
        readonly IPostLoadableSingleton inner;
        public TimedPostLoadable(IPostLoadableSingleton inner) { this.inner = inner; }

        public void PostLoad()
        {
            if (!LoadRecorder.Active) { inner.PostLoad(); return; }
            long start = Stopwatch.GetTimestamp();
            try { inner.PostLoad(); }
            finally { LoadRecorder.Add(ProfileKind.PostLoad, inner.GetType(), Stopwatch.GetTimestamp() - start); }
        }
    }

    internal sealed class TimedNonSingletonPostLoader : INonSingletonPostLoader, ITimedWrapper
    {
        readonly INonSingletonPostLoader inner;
        public TimedNonSingletonPostLoader(INonSingletonPostLoader inner) { this.inner = inner; }

        public void PostLoadNonSingletons()
        {
            if (!LoadRecorder.Active) { inner.PostLoadNonSingletons(); return; }
            long start = Stopwatch.GetTimestamp();
            try { inner.PostLoadNonSingletons(); }
            finally { LoadRecorder.Add(ProfileKind.PostLoadNonSingleton, inner.GetType(), Stopwatch.GetTimestamp() - start); }
        }
    }

    /// <summary>Puts the wrappers into the arrays the game keeps its singletons in.</summary>
    internal static class SingletonArrays
    {
        /// <summary>
        /// Replaces every element of an ImmutableArray field with a wrapper around it. Returns how many were wrapped. Elements that are
        /// already wrappers are left alone, so it is safe to call twice.
        /// </summary>
        public static int Swap<T>(object instance, string fieldName, Func<T, T> wrap)
        {
            FieldInfo field = Reflect.Field(instance.GetType(), fieldName);
            var original = (ImmutableArray<T>)field.GetValue(instance);
            if (original.IsDefaultOrEmpty) return 0;
            ImmutableArray<T>.Builder wrapped = ImmutableArray.CreateBuilder<T>(original.Length);
            int count = 0;
            foreach (T item in original)
            {
                if (item == null || item is ITimedWrapper) wrapped.Add(item);
                else { wrapped.Add(wrap(item)); count++; }
            }
            field.SetValue(instance, wrapped.MoveToImmutable());
            return count;
        }

        /// <summary>
        /// The once-per-tick singletons are held in a private struct that also carries the game's own timer, so the struct is rebuilt with
        /// a wrapper in place of the singleton and everything else copied. Returns how many were wrapped.
        /// </summary>
        public static int SwapTickSingletons(object tickableSingletonService)
        {
            FieldInfo field = Reflect.Field(tickableSingletonService.GetType(), "_tickableSingletons");
            Type element = field.FieldType.GetGenericArguments()[0];
            FieldInfo singleton = Reflect.Field(element, "_tickableSingleton");
            FieldInfo metric = Reflect.Field(element, "_metric");
            FieldInfo metricsEnabled = Reflect.Field(element, "_metricsEnabled");
            var original = (IEnumerable)field.GetValue(tickableSingletonService);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
            int count = 0;
            foreach (object item in original)
            {
                var inner = (ITickableSingleton)singleton.GetValue(item);
                if (inner == null || inner is ITimedWrapper) { list.Add(item); continue; }
                object rebuilt = Activator.CreateInstance(element, new TimedTickable(inner), metric.GetValue(item), metricsEnabled.GetValue(item));
                list.Add(rebuilt);
                count++;
            }
            MethodInfo create = typeof(ImmutableArray).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1).MakeGenericMethod(element);
            field.SetValue(tickableSingletonService, create.Invoke(null, new object[] { list }));
            return count;
        }
    }

    /// <summary>The steps of the game's loading, kept from the moment loading starts until the log can write them down.</summary>
    internal static class LoadRecorder
    {
        internal struct Step
        {
            public ProfileKind Kind;
            public string Name, Assembly;
            public long Ticks;
        }

        static readonly List<Step> steps = new List<Step>();
        static readonly List<KeyValuePair<string, long>> phases = new List<KeyValuePair<string, long>>();
        static long started, total;
        static int flushedSteps, flushedPhases;

        public static bool Active { get; private set; }

        public static void Begin()
        {
            steps.Clear(); phases.Clear();
            flushedSteps = 0; flushedPhases = 0; total = 0;
            started = Stopwatch.GetTimestamp();
            Active = true;
        }

        public static void Add(ProfileKind kind, Type type, long ticks)
        {
            if (!Active || steps.Count > 4000) return;
            string assembly = "";
            try { assembly = type.Assembly.GetName().Name; } catch (Exception) { }
            steps.Add(new Step { Kind = kind, Name = type.FullName ?? type.Name, Assembly = assembly, Ticks = ticks });
        }

        public static void PhaseEnd(string name, long ticks)
        {
            if (Active) phases.Add(new KeyValuePair<string, long>(name, ticks));
        }

        public static long End()
        {
            if (!Active) return total;
            total = Stopwatch.GetTimestamp() - started;
            Active = false;
            return total;
        }

        public static long TotalTicks => total;

        /// <summary>Steps and phases recorded since the last call.</summary>
        public static void TakeNew(List<Step> newSteps, List<KeyValuePair<string, long>> newPhases)
        {
            for (; flushedSteps < steps.Count; flushedSteps++) newSteps.Add(steps[flushedSteps]);
            for (; flushedPhases < phases.Count; flushedPhases++) newPhases.Add(phases[flushedPhases]);
        }
    }

    /// <summary>The stages of one save, so the event line can say where a save's time went.</summary>
    internal static class SaveTracker
    {
        public static readonly string[] StageNames = { "finishing the tick", "snapshot", "world JSON and compression", "thumbnail" };
        static readonly long[] stage = new long[StageNames.Length];
        static long start;
        static string what;
        static bool finishingTick;

        public static bool IsOpen { get; private set; }

        /// <summary>A save still open after this long was abandoned by an exception (the game's save throws on an IO error and skips its end), not nested.</summary>
        public const double AbandonedAfterSeconds = 60;

        /// <summary>Opens a save. False if one is already open (the outer one owns it). <paramref name="now"/> is for tests.</summary>
        public static bool TryOpen(string description, long now = 0)
        {
            if (now == 0) now = Stopwatch.GetTimestamp();
            if (IsOpen && now - start < AbandonedAfterSeconds * Stopwatch.Frequency) return false;
            Array.Clear(stage, 0, stage.Length);
            what = description; start = now; finishingTick = false; IsOpen = true;
            return true;
        }

        /// <summary>The first hook to enter a stage owns it; a hook nested inside it does not count again.</summary>
        public static long BeginStage(int index)
        {
            if (!IsOpen) return 0;
            if (index == 0)
            {
                if (finishingTick) return 0;
                finishingTick = true;
            }
            return Stopwatch.GetTimestamp();
        }

        public static void EndStage(int index, long began)
        {
            if (began == 0) return;
            if (index == 0) finishingTick = false;
            if (IsOpen) stage[index] += Stopwatch.GetTimestamp() - began;
        }

        /// <summary>Closes the save and writes it to the events file.</summary>
        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            var parts = new List<string>();
            double staged = 0;
            for (int i = 0; i < stage.Length; i++)
            {
                double stageMs = stage[i] * 1000.0 / Stopwatch.Frequency;
                staged += stageMs;
                parts.Add(StageNames[i] + " " + stageMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            }
            parts.Add("everything else " + Math.Max(0, ms - staged).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            Session.Event("save", ms, what + ": " + string.Join(", ", parts));
        }

        /// <summary>Forgets an open save (an exception skipped its end).</summary>
        public static void Abandon() { IsOpen = false; finishingTick = false; }
    }
}
