using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PerformanceLog
{
    /// <summary>
    /// Times methods that the config names (Watch = Namespace.Type.Method), including everything inside them and every patch on
    /// them. This is how to measure a mod's own work, or a game method that other mods patch, without editing any code. Calls are
    /// counted exactly and every Nth is timed (see <see cref="Profile"/>), so a hot method costs little. The result is rows of kind
    /// 'method' in profile.csv.
    /// </summary>
    internal static class Watch
    {
        sealed class Watched
        {
            public MethodBase Method;
            public string Name, Assembly;
        }

        static readonly List<Watched> watched = new List<Watched>();
        // Read by the patches on every call, replaced whole when a log starts and never changed in place.
        static Dictionary<MethodBase, int> ids = new Dictionary<MethodBase, int>();

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
                        if (method.IsAbstract || method.ContainsGenericParameters || method.IsGenericMethodDefinition) c.Reason = "skipped: abstract or generic";
                        else if (method.GetMethodBody() == null) c.Reason = "skipped: it has no body Harmony can patch";
                        else if (Reflect.HasExceptionFilter(method)) c.Reason = "skipped: it has a catch ... when clause, which Harmony cannot patch under Mono";
                        else if (accepted >= Config.MaxWatched) c.Reason = "skipped: too many watched methods";
                        else accepted++;
                        found.Add(c);
                    }
                }
                catch (Exception e) { found.Add(new Candidate { Label = entry, Reason = "could not be examined: " + (e.InnerException ?? e).Message }); }
            }
            return found;
        }

        /// <summary>Patches every method the config names that can be patched. Returns the number patched.</summary>
        internal static int Install(Harmony harmony, Config config)
        {
            watched.Clear(); Results.Clear();
            int patched = 0;
            MethodInfo prefix = Reflect.Own(typeof(Watch), nameof(WatchPrefix));
            MethodInfo postfix = Reflect.Own(typeof(Watch), nameof(WatchPostfix));
            foreach (Candidate c in Resolve(config.Watch))
            {
                if (!c.Ok) { Results.Add(c.Label + "|" + c.Reason); continue; }
                try
                {
                    harmony.Patch(c.Method, new HarmonyMethod(prefix) { priority = Priority.First }, new HarmonyMethod(postfix) { priority = Priority.Last });
                    watched.Add(new Watched { Method = c.Method, Name = c.Label, Assembly = c.Assembly });
                    Results.Add(c.Label + "|watching");
                    patched++;
                }
                catch (Exception e) { Results.Add(c.Label + "|could not be patched: " + (e.InnerException ?? e).Message); }
            }
            return patched;
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

        static void WatchPrefix(MethodBase __originalMethod, out Sample __state)
        {
            __state = default;
            if (!Probe.Enabled || !Probe.OnGameThread) return;
            if (!ids.TryGetValue(__originalMethod, out int id)) return;
            Instrumentation.Hits[Instrumentation.HitWatch]++;
            Probe.Count(Counter.PatchCalls);
            __state = Profile.BeginMethod(id);
        }

        static void WatchPostfix(MethodBase __originalMethod, Sample __state)
        {
            if (!__state.On) return;
            if (ids.TryGetValue(__originalMethod, out int id)) Profile.EndMethod(id, __state);
        }
    }
}
