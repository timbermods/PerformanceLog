using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;

namespace PerformanceLog
{
    /// <summary>
    /// Times methods that the config names (Watch = Namespace.Type.Method), including everything inside them and every patch on
    /// them. This is how to measure a mod's own work, or a game method that other mods patch, without editing any code. Calls are
    /// counted exactly and every Nth is timed (see <see cref="Profile"/>), so a hot method costs little. The result is rows of kind
    /// 'method' in profile.csv.
    ///
    /// With AutoWatch = true it also times, in the slots the Watch entries left free, the patch methods other mods put on hot methods
    /// (<see cref="AutoInstall()"/>; which ones is decided in <see cref="AutoWatch.Plan"/>). A patch method is watched like any other
    /// method: a prefix and a postfix of this mod's own around it. The patch it belongs to, and every other patch, stay as they were.
    /// </summary>
    internal static class Watch
    {
        sealed class Watched
        {
            public MethodBase Method;
            public string Name, Assembly;
            public bool Auto;
        }

        static readonly List<Watched> watched = new List<Watched>();
        // Read by the patches on every call, replaced whole when a log starts and never changed in place.
        static Dictionary<MethodBase, int> ids = new Dictionary<MethodBase, int>();
        static Harmony harmony;

        /// <summary>One line for each watched method, saying whether it was patched, for the header.</summary>
        internal static readonly List<string> Results = new List<string>();

        internal static int Count => watched.Count;

        /// <summary>A method the config asked for, or the reason it cannot be watched.</summary>
        internal sealed class Candidate
        {
            public MethodBase Method;
            public string Label, Assembly, Reason;
            public bool Ok => Method != null && Reason == null;
        }

        /// <summary>Finds the methods the config names, and says why any cannot be watched. Nothing is patched.</summary>
        internal static List<Candidate> Resolve(IEnumerable<string> entries)
        {
            var found = new List<Candidate>();
            int accepted = 0;
            foreach (string entry in entries)
            {
                try
                {
                    int dot = entry.LastIndexOf('.');
                    if (dot <= 0 || dot == entry.Length - 1) { found.Add(new Candidate { Label = entry, Reason = "not a Namespace.Type.Method name" }); continue; }
                    string typeName = entry.Substring(0, dot), methodName = entry.Substring(dot + 1);
                    Type type = AccessTools.TypeByName(typeName);
                    if (type == null) { found.Add(new Candidate { Label = entry, Reason = "no such type is loaded (is the mod that has it enabled? the name is the full name with its namespace)" }); continue; }
                    List<MethodBase> methods = Reflect.Overloads(type, methodName);
                    if (methods.Count == 0) { found.Add(new Candidate { Label = entry, Reason = "the type has no method of that name" }); continue; }
                    foreach (MethodBase method in methods)
                    {
                        var c = new Candidate { Method = method, Label = Label(type, method), Assembly = type.Assembly.GetName().Name };
                        c.Reason = Refusal(method);
                        if (c.Reason == null)
                        {
                            if (accepted >= Config.MaxWatched) c.Reason = "skipped: too many watched methods";
                            else accepted++;
                        }
                        found.Add(c);
                    }
                }
                catch (Exception e) { found.Add(new Candidate { Label = entry, Reason = "could not be examined: " + (e.InnerException ?? e).Message }); }
            }
            return found;
        }

        /// <summary>Why a method cannot be watched, or null if it can. The same rules for a Watch entry and for the auto watch.</summary>
        static string Refusal(MethodBase method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.IsGenericMethodDefinition) return "skipped: abstract or generic";
            if (method.GetMethodBody() == null) return "skipped: it has no body Harmony can patch";
            if (Reflect.HasExceptionFilter(method)) return "skipped: it has a catch ... when clause, which Harmony cannot patch under Mono";
            return null;
        }

        /// <summary>Patches every method the config names that can be patched. Returns the number patched.</summary>
        internal static int Install(Harmony harmony, Config config)
        {
            watched.Clear(); Results.Clear();
            Watch.harmony = harmony;
            int patched = 0;
            foreach (Candidate c in Resolve(config.Watch))
            {
                if (!c.Ok) { Results.Add(c.Label + "|" + c.Reason); continue; }
                string failure = Patch(c.Method);
                if (failure != null) { Results.Add(c.Label + "|could not be patched: " + failure); continue; }
                watched.Add(new Watched { Method = c.Method, Name = c.Label, Assembly = c.Assembly });
                Results.Add(c.Label + "|watching");
                patched++;
            }
            return patched;
        }

        /// <summary>Puts the watch's prefix (before every other) and postfix (after every other) on a method. Returns null, or why it could not.</summary>
        static string Patch(MethodBase method)
        {
            try
            {
                if (harmony == null) harmony = new Harmony(Instrumentation.HarmonyId + ".watch");
                MethodInfo prefix = Reflect.Own(typeof(Watch), nameof(WatchPrefix));
                MethodInfo postfix = Reflect.Own(typeof(Watch), nameof(WatchPostfix));
                harmony.Patch(method, new HarmonyMethod(prefix) { priority = Priority.First }, new HarmonyMethod(postfix) { priority = Priority.Last });
                return null;
            }
            catch (Exception e) { return (e.InnerException ?? e).Message; }
        }

        static string Label(Type type, MethodBase method)
        {
            var parameters = new List<string>();
            foreach (ParameterInfo p in method.GetParameters()) parameters.Add(p.ParameterType.Name);
            return (type.FullName ?? type.Name) + "." + method.Name + "(" + string.Join(",", parameters) + ")";
        }

        /// <summary>Registers the watched methods with a log that is starting (the profile was just reset, so old ids are gone).</summary>
        internal static void OnSessionStart()
        {
            var map = new Dictionary<MethodBase, int>();
            foreach (Watched w in watched)
                map[w.Method] = Profile.RegisterMethod(w.Name, w.Assembly, 8);
            ids = map;
        }

        internal static void WatchPrefix(MethodBase __originalMethod, out Sample __state)
        {
            __state = default;
            if (!Probe.Enabled || !Probe.OnGameThread) return;
            if (!ids.TryGetValue(__originalMethod, out int id)) return;
            Probe.Count(Counter.PatchCalls);
            __state = Profile.BeginMethod(id);
        }

        internal static void WatchPostfix(MethodBase __originalMethod, Sample __state)
        {
            if (!__state.On) return;
            Instrumentation.Hits[Instrumentation.HitWatch]++;
            if (ids.TryGetValue(__originalMethod, out int id)) Profile.EndMethod(id, __state);
        }

        // ---- the auto watch ----

        static bool autoTried;
        static string autoFailure;
        static List<AutoWatchResult> autoResults;

        /// <summary>
        /// Chooses and patches the auto watch's methods, once per run of the game (the patches stay for the next save loaded, as the Watch
        /// entries' do). Called when the first log starts: every mod has started and the game has loaded, so Harmony's registry holds the
        /// patches other mods make at start-up and while a game loads, and nothing ticks yet (no tick or parallel tick is running a patch
        /// method; only a thread another mod runs of its own could be). A patch another mod makes later is not seen. Only this mod's own
        /// patches are added; nothing of any other patch is removed, reordered or changed. All of it runs inside
        /// <see cref="AroundAutoPatching"/>, which leaves the game's random state as it found it (see there: this is what keeps co-op in step).
        /// </summary>
        internal static void AutoInstall()
        {
            if (autoTried) return;
            autoTried = true;
            try
            {
                AutoInstall(AutoCandidates, Patch);
                Log.Info("Auto watch: " + AutoWatch.Describe(autoResults) + ".");
            }
            catch (Exception e)
            {
                autoFailure = (e.InnerException ?? e).Message;
                Log.Warning("The auto watch could not run: " + autoFailure);
            }
        }

        /// <summary>
        /// Plans the auto watch over these patches and watches what it chooses, patching each with <paramref name="patch"/> (null, or why
        /// not). The patches are read and made inside <see cref="AroundAutoPatching"/>.
        /// </summary>
        internal static void AutoInstall(Func<List<AutoWatchCandidate>> candidates, Func<MethodBase, string> patch)
        {
            AroundAutoPatching(() =>
            {
                var named = new HashSet<string>(watched.Select(w => w.Name), StringComparer.Ordinal);
                autoResults = AutoWatch.Plan(candidates(), Instrumentation.HarmonyId, named, Config.MaxWatched - watched.Count, c =>
                {
                    if (!(c.Method is MethodBase method)) return "the patch method could not be found";
                    string failure = patch(method);
                    if (failure == null) watched.Add(new Watched { Method = method, Name = c.Label, Assembly = c.Assembly, Auto = true });
                    return failure;
                });
            });
        }

        /// <summary>
        /// Runs the auto watch's patching. In the game it is <see cref="KeepUnityRandom"/>; a test replaces it for a while (and puts it
        /// back), because Unity's random state cannot be read outside the game.
        /// </summary>
        internal static Action<Action> AroundAutoPatching = KeepUnityRandom;

        /// <summary>
        /// Runs <paramref name="patching"/> and then puts <c>UnityEngine.Random</c>'s state back exactly as it was, even if it throws.
        /// Every <c>Harmony.Patch</c> builds a MonoMod <c>DynamicMethodDefinition</c>, whose field initializer calls <c>Guid.NewGuid()</c>,
        /// and BeaverBuddies (its <c>GuidPatcher</c>) turns every <c>Guid.NewGuid()</c> into 16 draws from <c>UnityEngine.Random</c>, the
        /// state Timberborn's <c>RandomNumberGenerator</c> uses for the simulation. When the first log starts, BeaverBuddies has already
        /// seeded that state for the game (in <c>DeterminismService</c>'s constructor), so without this a player with AutoWatch = true, or
        /// with a different number of patches made, would enter the first tick with a different random state from the other players: a
        /// co-op desync. The Watch entries' patches are made at <c>StartMod</c>, before any game seeds it, so they need no such care. If the
        /// state cannot be read, nothing is patched.
        /// </summary>
        static void KeepUnityRandom(Action patching)
        {
            UnityEngine.Random.State state = UnityEngine.Random.state;
            try { patching(); }
            finally { UnityEngine.Random.state = state; }
        }

        /// <summary>Every prefix, postfix and finalizer in Harmony's registry on a method that runs behind a profile row or is on the hot list.</summary>
        static List<AutoWatchCandidate> AutoCandidates()
        {
            var candidates = new List<AutoWatchCandidate>();
            foreach (MethodBase target in Harmony.GetAllPatchedMethods().ToList())
            {
                try
                {
                    if (!PatchFormat.IsHot(target.DeclaringType?.Name, target.Name) && !RunsBehindAProfileRow(target)) continue;
                    Patches info = Harmony.GetPatchInfo(target);
                    if (info == null) continue;
                    AddCandidates(candidates, target, "prefix", info.Prefixes);
                    AddCandidates(candidates, target, "postfix", info.Postfixes);
                    AddCandidates(candidates, target, "finalizer", info.Finalizers);
                }
                catch (Exception) { /* a method Harmony cannot describe is left out */ }
            }
            return candidates;
        }

        static void AddCandidates(List<AutoWatchCandidate> candidates, MethodBase target, string kind, IEnumerable<Patch> patches)
        {
            if (patches == null) return;
            foreach (Patch patch in patches) candidates.Add(Examine(target, kind, patch.owner, patch.PatchMethod));
        }

        /// <summary>One patch as an auto watch candidate: its label, mod and owner, what it patches, and why it cannot be watched, if it cannot. Nothing is patched.</summary>
        internal static AutoWatchCandidate Examine(MethodBase target, string kind, string owner, MethodInfo patchMethod)
        {
            var c = new AutoWatchCandidate
            {
                Kind = kind, Owner = owner, TypeName = target?.DeclaringType?.Name, MethodName = target?.Name,
                Target = (target?.DeclaringType?.FullName ?? "?") + "." + target?.Name, ProfileRow = RunsBehindAProfileRow(target),
            };
            try
            {
                Type type = patchMethod?.DeclaringType;
                if (type == null) return c;   // no label: never a candidate
                c.Label = Label(type, patchMethod);
                c.Assembly = type.Assembly.GetName().Name;
                c.Method = patchMethod;
                c.Refused = Refusal(patchMethod);
                c.CanReplace = kind == "prefix" && patchMethod.ReturnType == typeof(bool);
                // Harmony hands its patches the method they are on as MethodBase.GetMethodFromHandle gives it, so that is the key the watch
                // looks up; the MethodInfo in Harmony's registry is the same method, but may be a different object.
                if (c.Refused == null && !type.IsGenericType) c.Method = MethodBase.GetMethodFromHandle(patchMethod.MethodHandle) ?? patchMethod;
            }
            catch (Exception e) { c.Refused = "could not be examined: " + (e.InnerException ?? e).Message; }
            return c;
        }

        /// <summary>
        /// Whether a method is one a profile row times: a singleton's Tick, UpdateSingleton, LateUpdateSingleton or StartParallelTick, or an
        /// entity's or component's Tick. Another mod's patch on one of these runs inside that row, which names the game or the singleton's own mod.
        /// </summary>
        internal static bool RunsBehindAProfileRow(MethodBase method)
        {
            try
            {
                Type type = method?.DeclaringType;
                if (type == null || method.IsStatic || method.GetParameters().Length != 0) return false;
                switch (method.Name)
                {
                    case "Tick":
                        return type == typeof(TickableEntity) || type == typeof(MeteredTickableComponent) ||
                               typeof(TickableComponent).IsAssignableFrom(type) || typeof(ITickableSingleton).IsAssignableFrom(type);
                    case "UpdateSingleton": return typeof(IUpdatableSingleton).IsAssignableFrom(type);
                    case "LateUpdateSingleton": return typeof(ILateUpdatableSingleton).IsAssignableFrom(type);
                    case "StartParallelTick": return typeof(IParallelTickableSingleton).IsAssignableFrom(type);
                    default: return false;
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>For the header's and the summary's capability lines: whether the auto watch ran and what it did.</summary>
        internal static string AutoCapability(Config config)
        {
            if (config == null || !config.AutoWatch) return "off (AutoWatch = false in " + Config.FileName + ")";
            if (autoFailure != null) return "could not run: " + autoFailure;
            return AutoWatch.Describe(autoResults);
        }

        /// <summary>A # watch| line for each patch method the auto watch looked at: label, status, "auto", the hot methods it is on, its owner.</summary>
        internal static IEnumerable<string[]> AutoLines()
        {
            if (autoResults == null) yield break;
            foreach (AutoWatchResult r in autoResults)
                yield return new[] { r.Candidate.Label, r.Status, "auto", r.On, r.Candidate.Owner ?? "" };
        }

        /// <summary>For the capability-final line: how many of the auto-watched patch methods were called, and which were not.</summary>
        internal static string AutoFinal()
        {
            var never = new List<string>();
            int auto = 0;
            foreach (Watched w in watched)
            {
                if (!w.Auto) continue;
                auto++;
                if (!ids.TryGetValue(w.Method, out int id) || Profile.CallsSoFar(id) <= 0) never.Add(w.Name);
            }
            if (autoResults == null && auto == 0) return autoFailure != null ? "could not run: " + autoFailure : "off";
            string text = (auto - never.Count) + " of " + auto + " watched patch methods were called";
            if (never.Count > 0)
                text += "|never seen called (not called, called only off the game thread, or so small that the runtime copied it into the method it patches, where no watch sees it): " + string.Join("; ", never.Take(12)) +
                        (never.Count > 12 ? "; and " + (never.Count - 12) + " more" : "");
            return text;
        }

        /// <summary>Test-only: forgets every watched method and the auto watch's choice.</summary>
        internal static void ResetForTest()
        {
            watched.Clear(); Results.Clear();
            ids = new Dictionary<MethodBase, int>();
            autoTried = false; autoFailure = null; autoResults = null;
        }

        /// <summary>Test-only: a method watched the way a Watch entry's is after <see cref="Install"/> has patched it.</summary>
        internal static void AddEntryForTest(MethodBase method, string name, string assembly) =>
            watched.Add(new Watched { Method = method, Name = name, Assembly = assembly });
    }
}
