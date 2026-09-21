using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PerformanceLog
{
    /// <summary>
    /// Builds the comment lines at the top of a CSV file. Three shapes, all starting with #, so any CSV reader that skips comments
    /// (pandas: comment='#') still reads the rows:
    ///   # key: value              a fact about the session (game version, the computer...)
    ///   # kind|a|b|c              a list entry (a mod, a capability, a patch...)
    ///   # note: text              prose for the reader
    /// </summary>
    public sealed class HeaderBuilder
    {
        public const int FramesFormat = 1;
        public readonly List<string> Lines = new List<string>();

        public HeaderBuilder(string fileKind, int format)
        {
            Lines.Add("# PerformanceLog " + fileKind + ", format " + format);
        }

        public void Add(string key, string value) => Lines.Add("# " + key + ": " + PatchFormat.Clean(value));

        public void Note(string text) => Lines.Add("# note: " + PatchFormat.Clean(text));

        public void Pipe(string kind, params string[] parts) =>
            Lines.Add("# " + kind + "|" + string.Join("|", parts.Select(p => PatchFormat.Clean(p))));

        public void Mod(string id, string name, string version) => Pipe("mod", id, name, version);

        public void Append(IEnumerable<string> lines) => Lines.AddRange(lines);

        public static string Number(double value, int decimals = 0) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    /// <summary>The text of README.md and columns.md, written into every session folder so the folder explains itself.</summary>
    public static class SessionReadme
    {
        /// <summary>Fills the placeholders of docs/SESSION-README.md.</summary>
        public static string Render(string template, string version)
        {
            string kinds = string.Join("\n", Enumerable.Range(0, ProfileKinds.Words.Length)
                .Select(i => "- `" + ProfileKinds.Words[i] + "`: " + ProfileKinds.Meaning[i]));
            return template
                .Replace("{{VERSION}}", version)
                .Replace("{{PROFILE_KINDS}}", kinds)
                .Replace("{{FRAME_EDGES}}", string.Join(", ", Columns.FrameEdgesMs.Select(e => e.ToString(CultureInfo.InvariantCulture))));
        }

        /// <summary>The definition of every column of the three CSV files, generated from the code so it cannot drift from what is written.</summary>
        public static string RenderColumns(string version)
        {
            var text = new System.Text.StringBuilder();
            text.Append("# Columns of the Performance Log files (version ").Append(version).Append(")\n\n");
            text.Append("Generated from the mod's own column definitions. `README.md` explains how to read the numbers; this file only says what each column is.\n\n");
            text.Append("## frames.csv\n\n");
            text.Append("One row per slow frame (`F`) and per summary window (`S`). Every row has every column. In `S` rows times and Unity's figures are averages per frame, ");
            text.Append("and allocation, counts and the two histograms are totals over the window's frames (the third column below says which). `fh0`..`fh16` count frames per frame-time bucket; the upper edges in milliseconds are ");
            text.Append(string.Join(", ", Columns.FrameEdgesMs.Select(e => e.ToString(CultureInfo.InvariantCulture))));
            text.Append(" (the last bucket is everything above). `th0`..`th5` count frames that ran 0, 1, 2, 3-4, 5-9 and 10 or more ticks.\n\n");
            text.Append(Columns.Main.GlossaryMarkdown()).Append('\n');
            text.Append("## profile.csv\n\nOne row per key per window. The last three columns (`name`, `assembly`, `mod`) are text written after the numbers. `kind` is one of:\n\n");
            for (int i = 0; i < ProfileKinds.Words.Length; i++) text.Append("- `").Append(ProfileKinds.Words[i]).Append("`: ").Append(ProfileKinds.Meaning[i]).Append('\n');
            text.Append('\n').Append(Profile.Table.GlossaryMarkdown()).Append('\n');
            text.Append("## spikes.csv\n\nFor each slow frame, the singletons that took the most of it. The last two columns (`name`, `mod`) are text written after the numbers.\n\n");
            text.Append(Profile.SpikeTable.GlossaryMarkdown()).Append('\n');
            text.Append("## events.csv\n\n`utcMs,tick,kind,ms,detail`. `kind` is `session-start`, `session-end`, `save`, `load`, `load-phase` or `warning`. `detail` is text in quotes.\n");
            return text.ToString();
        }
    }
}
