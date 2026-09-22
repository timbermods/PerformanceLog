using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PerformanceLog
{
    /// <summary>How much the mod measures. Read from PerformanceLog.cfg next to the manifest when the game starts.</summary>
    internal sealed class Config
    {
        public const string FileName = "PerformanceLog.cfg";
        public const int MaxWatched = 40;

        /// <summary>off: do nothing at all. standard: frame, tick and singleton timing, and a sampled profile of entity kinds. deep: also every entity component.</summary>
        public const string ProfileOff = "off", ProfileStandard = "standard", ProfileDeep = "deep";

        // Defaults and valid ranges for the numeric settings, named so PerformanceLog.cfg's own clamp (Apply, below) and the in-game
        // settings panel (Settings.cs) always agree; a slider there uses exactly these bounds, never a narrower "nicer" one.
        //
        // Every default below is chosen for the most detail a fresh install can capture without anyone touching a setting: deep
        // profiling, every spike slot filled, and a sampling budget generous enough that "sampled" counts in profile.csv are rarely
        // tiny. This costs more than the old defaults (roughly double the sampling overhead, plus deep's component sampling); nothing
        // here is unbounded, and MaxSlowRowsPerMinute, SlowFrameMs and the window sizes are left where they were, since they trade off
        // row count and file size rather than the depth of what a single row can say, and the budget system still throttles itself if
        // measuring gets expensive.
        public const double SlowFrameMsDefault = 50, SlowFrameMsMin = 1, SlowFrameMsMax = 5000;
        public const double SummarySecondsDefault = 10, SummarySecondsMin = 1, SummarySecondsMax = 600;
        public const double ProfileSecondsDefault = 30, ProfileSecondsMin = 5, ProfileSecondsMax = 1800;
        // Double the old 0.5%: still comfortably under the 2% the report itself calls out as "measuring costs too much" once the
        // patch-call overhead that is not budget-limited is added on top.
        public const double OverheadBudgetPercentDefault = 1, OverheadBudgetPercentMin = 0.05, OverheadBudgetPercentMax = 5;
        public const int MaxSlowRowsPerMinuteDefault = 300, MaxSlowRowsPerMinuteMin = 10, MaxSlowRowsPerMinuteMax = 6000;
        // Every slot filled: this is PerformanceLog.Profile.TopK itself, a const in the same compilation, so the two can never drift apart.
        public const int SpikeContributorsDefault = PerformanceLog.Profile.TopK;

        public bool Enabled = true;
        public double SlowFrameMs = SlowFrameMsDefault;
        public double SummarySeconds = SummarySecondsDefault;
        public double ProfileSeconds = ProfileSecondsDefault;
        public string Profile = ProfileDeep;
        public double OverheadBudgetPercent = OverheadBudgetPercentDefault;
        public int SpikeContributors = SpikeContributorsDefault;
        public int MaxSlowRowsPerMinute = MaxSlowRowsPerMinuteDefault;
        public string OutputFolder = "";
        public readonly List<string> Watch = new List<string>();
        /// <summary>Also time the patch methods other mods put on hot methods, in the Watch slots the Watch entries leave (Watch.AutoInstall).</summary>
        public bool AutoWatch = false;

        /// <summary>Lines of the file that were not understood, for the log.</summary>
        public readonly List<string> Problems = new List<string>();

        public bool Deep => Profile == ProfileDeep;
        public bool SamplesEntities => Profile != ProfileOff;

        public static Config Load(params string[] directories)
        {
            var config = new Config();
            foreach (string directory in directories)
            {
                if (string.IsNullOrEmpty(directory)) continue;
                try
                {
                    string path = Path.Combine(directory, FileName);
                    if (!File.Exists(path)) continue;
                    config.Apply(Parse(File.ReadAllLines(path)));
                    Log.Info("Settings read from " + path);
                    return config;
                }
                catch (Exception e) { Log.Warning("Could not read " + FileName + " in " + directory + ": " + e.Message); }
            }
            Log.Info(FileName + " not found; using the defaults.");
            return config;
        }

        /// <summary>key = value lines; # starts a comment. A key may repeat (Watch does); the pairs come back in file order.</summary>
        public static List<KeyValuePair<string, string>> Parse(IEnumerable<string> lines)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (string raw in lines)
            {
                string line = raw;
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                pairs.Add(new KeyValuePair<string, string>(line.Substring(0, separator).Trim(), line.Substring(separator + 1).Trim()));
            }
            return pairs;
        }

        public void Apply(List<KeyValuePair<string, string>> pairs)
        {
            foreach (var pair in pairs)
            {
                string key = pair.Key, value = pair.Value;
                switch (key.ToLowerInvariant())
                {
                    case "enabled": Enabled = Bool(key, value, Enabled); break;
                    case "autowatch": AutoWatch = Bool(key, value, AutoWatch); break;
                    case "slowframems": SlowFrameMs = Clamp(Number(key, value, SlowFrameMs), SlowFrameMsMin, SlowFrameMsMax); break;
                    case "summaryseconds": SummarySeconds = Clamp(Number(key, value, SummarySeconds), SummarySecondsMin, SummarySecondsMax); break;
                    case "profileseconds": ProfileSeconds = Clamp(Number(key, value, ProfileSeconds), ProfileSecondsMin, ProfileSecondsMax); break;
                    case "overheadbudgetpercent": OverheadBudgetPercent = Clamp(Number(key, value, OverheadBudgetPercent), OverheadBudgetPercentMin, OverheadBudgetPercentMax); break;
                    case "spikecontributors": SpikeContributors = (int)Clamp(Number(key, value, SpikeContributors), 0, PerformanceLog.Profile.TopK); break;
                    case "maxslowrowsperminute": MaxSlowRowsPerMinute = (int)Clamp(Number(key, value, MaxSlowRowsPerMinute), MaxSlowRowsPerMinuteMin, MaxSlowRowsPerMinuteMax); break;
                    case "outputfolder": OutputFolder = value; break;
                    case "profile":
                        string level = value.ToLowerInvariant();
                        if (level == ProfileOff || level == ProfileStandard || level == ProfileDeep) Profile = level;
                        else Problems.Add("Profile = " + value + " is not off, standard or deep; using " + Profile);
                        break;
                    case "watch":
                        foreach (string item in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string name = item.Trim();
                            if (name.Length == 0) continue;
                            if (Watch.Count >= MaxWatched) { Problems.Add("Watch: only the first " + MaxWatched + " methods are used"); break; }
                            Watch.Add(name);
                        }
                        break;
                    default:
                        Problems.Add("unknown setting " + key);
                        break;
                }
            }
        }

        bool Bool(string key, string value, bool fallback)
        {
            if (bool.TryParse(value, out bool result)) return result;
            Problems.Add(key + " = " + value + " is not true or false; using " + fallback);
            return fallback;
        }

        double Number(string key, string value, double fallback)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)) return result;
            Problems.Add(key + " = " + value + " is not a number; using " + fallback.ToString(CultureInfo.InvariantCulture));
            return fallback;
        }

        /// <summary>Public so the in-game settings panel (Settings.cs) clamps its own values the same way. Also used by Apply, above.</summary>
        public static double Clamp(double value, double low, double high) => value < low ? low : value > high ? high : value;

        public override string ToString() =>
            "Enabled=" + Enabled + ", Profile=" + Profile + ", SlowFrameMs=" + SlowFrameMs.ToString(CultureInfo.InvariantCulture) +
            ", SummarySeconds=" + SummarySeconds.ToString(CultureInfo.InvariantCulture) + ", ProfileSeconds=" + ProfileSeconds.ToString(CultureInfo.InvariantCulture) +
            ", OverheadBudgetPercent=" + OverheadBudgetPercent.ToString(CultureInfo.InvariantCulture) + ", SpikeContributors=" + SpikeContributors + ", MaxSlowRowsPerMinute=" + MaxSlowRowsPerMinute +
            ", Watch=" + (Watch.Count == 0 ? "(none)" : string.Join(";", Watch)) + ", AutoWatch=" + AutoWatch;
    }
}
