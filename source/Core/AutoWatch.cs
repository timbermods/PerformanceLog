using System;
using System.Collections.Generic;
using System.Linq;

// Decides which of other mods' patch methods the auto watch (AutoWatch = true) times, from plain records: no Unity, Timberborn or Harmony
// types, so it can be tested on its own. The game side reads Harmony's registry, says which patched methods a profile row times, and makes
// the patches this chooses (Watch.AutoInstall).
namespace PerformanceLog
{
    /// <summary>One patch in Harmony's registry, as the auto watch sees it.</summary>
    public sealed class AutoWatchCandidate
    {
        /// <summary>The patch method, named the way a Watch entry is (Namespace.Type.Method(ParameterTypes)): its key, and its row's name in profile.csv.</summary>
        public string Label;
        /// <summary>The DLL the patch method lives in, which names the mod.</summary>
        public string Assembly;
        /// <summary>The Harmony id that made the patch.</summary>
        public string Owner;
        /// <summary>prefix, postfix, finalizer or transpiler.</summary>
        public string Kind;
        /// <summary>The patched method: its declaring type's short name, its name, and the full name the log shows.</summary>
        public string TypeName, MethodName, Target;
        /// <summary>
        /// The patched method is one a profile row times: a singleton's Tick, UpdateSingleton, LateUpdateSingleton or StartParallelTick, or an
        /// entity's or component's Tick. A patch there is inside that row, which names the game or the singleton's own mod, not the patching mod.
        /// </summary>
        public bool ProfileRow;
        /// <summary>
        /// A prefix that returns bool: it can return false and skip the method it patches (and every later prefix), doing that work itself.
        /// Its time is then work done instead of the game's, not on top of it.
        /// </summary>
        public bool CanReplace;
        /// <summary>Why the patch method cannot be watched, said the way Watch says it for a config entry; null if it can be.</summary>
        public string Refused;
        /// <summary>The game side's handle on the patch method. Carried through, never read here.</summary>
        public object Method;
    }

    /// <summary>What the auto watch did with one patch method.</summary>
    public sealed class AutoWatchResult
    {
        /// <summary>The patch (the first in the order, when the patch method is on several hot methods).</summary>
        public AutoWatchCandidate Candidate;
        /// <summary>
        /// Every hot method the patch method is on, as "prefix on Namespace.Type.Method", joined with "; ". A prefix that can skip the method
        /// it patches (it returns bool) says so: "prefix on Namespace.Type.Method (can replace it)".
        /// </summary>
        public string On;
        /// <summary>"watching", or why not.</summary>
        public string Status;
        public bool Watching;
    }

    public static class AutoWatch
    {
        public const string Watching = "watching";
        public const string NamedByWatch = "watched by a Watch entry";
        public const string NoFreeSlot = "skipped: no free Watch slot";
        public const string CanReplaceNote = "(can replace it)";

        sealed class Group
        {
            public string Label;
            public AutoWatchCandidate First;
            public int Tier, KindOrder;
            public readonly SortedSet<string> On = new SortedSet<string>(StringComparer.Ordinal);
            public string Refused;
        }

        /// <summary>
        /// Chooses and watches other mods' patch methods on hot methods, in <paramref name="freeSlots"/> Watch slots at most (the slots the
        /// config's Watch entries left). Candidates are the prefixes, postfixes and finalizers (a transpiler runs once, when its patch is made,
        /// so there is nothing to time) of every owner but <paramref name="ownOwner"/>, on a method a profile row times or on
        /// <see cref="PatchFormat"/>'s hot list. One patch method is one watch, however many hot methods it is on. The order is by name, so
        /// the same patches give the same choice whatever order Harmony lists them in: first the patches on the methods behind the profile's
        /// rows (their time is inside a row that names someone else), then the rest of the hot list; within each, by the patched method's
        /// full name, then prefix, postfix, finalizer, then the patch method's name. A patch method a Watch entry already watches
        /// (<paramref name="watchedAlready"/>, by label) keeps that watch; one that is refused, or that <paramref name="watch"/> could not
        /// patch (it returns why, or null once it has), takes no slot. The results are in that order, one for each patch method.
        /// </summary>
        public static List<AutoWatchResult> Plan(IEnumerable<AutoWatchCandidate> patches, string ownOwner, ICollection<string> watchedAlready,
            int freeSlots, Func<AutoWatchCandidate, string> watch)
        {
            var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
            foreach (AutoWatchCandidate c in patches ?? Enumerable.Empty<AutoWatchCandidate>())
            {
                if (c == null || string.IsNullOrEmpty(c.Label)) continue;
                int kindOrder = KindOrder(c.Kind);
                if (kindOrder < 0) continue;
                if (!string.IsNullOrEmpty(ownOwner) && (c.Owner ?? "").StartsWith(ownOwner, StringComparison.Ordinal)) continue;
                int tier = c.ProfileRow ? 0 : PatchFormat.IsHot(c.TypeName, c.MethodName) ? 1 : -1;
                if (tier < 0) continue;
                if (!groups.TryGetValue(c.Label, out Group g)) groups[c.Label] = g = new Group { Label = c.Label, Tier = int.MaxValue };
                g.On.Add(c.Kind + " on " + c.Target + (c.CanReplace && kindOrder == 0 ? " " + CanReplaceNote : ""));
                if (Before(c, tier, kindOrder, g)) { g.First = c; g.Tier = tier; g.KindOrder = kindOrder; }
                // Why a patch method cannot be watched does not depend on the hot method it is on; if the records disagree, the same one is kept whatever their order.
                if (c.Refused != null && (g.Refused == null || string.CompareOrdinal(c.Refused, g.Refused) < 0)) g.Refused = c.Refused;
            }
            var ordered = groups.Values.ToList();
            ordered.Sort((a, b) =>
            {
                int by = a.Tier.CompareTo(b.Tier);
                if (by == 0) by = string.CompareOrdinal(a.First.Target, b.First.Target);
                if (by == 0) by = a.KindOrder.CompareTo(b.KindOrder);
                if (by == 0) by = string.CompareOrdinal(a.Label, b.Label);
                return by;
            });

            var results = new List<AutoWatchResult>();
            int used = 0;
            foreach (Group g in ordered)
            {
                var r = new AutoWatchResult { Candidate = g.First, On = string.Join("; ", g.On) };
                if (watchedAlready != null && watchedAlready.Contains(g.Label)) r.Status = NamedByWatch;
                else if (g.Refused != null) r.Status = g.Refused;
                else if (used >= freeSlots) r.Status = NoFreeSlot;
                else
                {
                    string failure;
                    try { failure = watch(g.First); }
                    catch (Exception e) { failure = (e.InnerException ?? e).Message ?? e.GetType().Name; }
                    if (failure == null) { used++; r.Status = Watching; r.Watching = true; }
                    else r.Status = "could not be patched: " + failure;
                }
                results.Add(r);
            }
            return results;
        }

        /// <summary>Whether a patch of the method comes before the group's first so far: by tier, patched method, kind, then owner.</summary>
        static bool Before(AutoWatchCandidate c, int tier, int kindOrder, Group g)
        {
            if (g.First == null) return true;
            if (tier != g.Tier) return tier < g.Tier;
            int by = string.CompareOrdinal(c.Target, g.First.Target);
            if (by == 0) by = kindOrder.CompareTo(g.KindOrder);
            if (by == 0) by = string.CompareOrdinal(c.Owner, g.First.Owner);
            return by < 0;
        }

        static int KindOrder(string kind)
        {
            switch (kind)
            {
                case "prefix": return 0;
                case "postfix": return 1;
                case "finalizer": return 2;
                default: return -1;
            }
        }

        /// <summary>One line for the header and the summary: how many patch methods were found and watched, and why the rest were not.</summary>
        public static string Describe(List<AutoWatchResult> results)
        {
            if (results == null) return "not run";
            int watching = results.Count(r => r.Watching);
            int named = results.Count(r => r.Status == NamedByWatch);
            int noSlot = results.Count(r => r.Status == NoFreeSlot);
            int other = results.Count - watching - named - noSlot;
            var text = new List<string> { "watching " + watching + " of " + results.Count + " patch methods other mods put on hot methods" };
            if (named > 0) text.Add(named + " named by a Watch entry");
            if (noSlot > 0) text.Add(noSlot + " left out: no free Watch slot");
            if (other > 0) text.Add(other + " refused or could not be patched");
            return string.Join("; ", text);
        }
    }
}
