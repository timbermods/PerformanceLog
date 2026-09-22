using System;
using Bindito.Core;
using ModSettings.Common;
using ModSettings.Core;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace PerformanceLog
{
    /// <summary>
    /// The in-game settings page (the Mod Settings mod, a required dependency). Only the six numbers <see cref="Session.Start"/> reads
    /// fresh at the start of every session are here, because they are the only ones that can change without restarting Timberborn:
    /// <see cref="Config.Enabled"/>, <see cref="Config.Profile"/>, <see cref="Config.Watch"/> and <see cref="Config.AutoWatch"/> decide which
    /// Harmony patches this mod makes, from the config <see cref="Plugin.StartMod"/> reads before Bindito (and so before Mod Settings)
    /// exists (the auto watch's patches are made when the first game loads, once per run of the game), so those four and
    /// <see cref="Config.OutputFolder"/> (a folder path; Mod Settings has no free-text widget that works with the game's own settings
    /// storage) stay in <c>PerformanceLog.cfg</c> only. This class is the only place that touches Mod Settings types, so nothing else
    /// depends on that assembly being loadable (it always is: a required mod).
    /// </summary>
    public class PerformanceSettings : ModSettingsOwner
    {
        public ReadonlyTextModSetting Note { get; } = new ReadonlyTextModSetting(
            ModSettingDescriptor.Create("Enabled, Profile, Watch, AutoWatch and OutputFolder are set in PerformanceLog.cfg, next to this mod's manifest, " +
                "and need Timberborn restarted (they decide which parts of the game get patched, before this menu exists). Everything below " +
                "applies to the next game or save you load."),
            new ReadonlyTextModSetting.TextSettings());

        public RangeIntModSetting SlowFrameMs { get; } = new RangeIntModSetting(
            (int)StartValue(c => c.SlowFrameMs, Config.SlowFrameMsDefault), (int)Config.SlowFrameMsMin, (int)Config.SlowFrameMsMax,
            ModSettingDescriptor.Create("Slow frame threshold (ms)")
                .SetTooltip("A frame this long or longer gets its own row in frames.csv, and its biggest contributors go to spikes.csv. " +
                            "16.7 ms is 60 fps; lower catches more but writes more."));

        public RangeIntModSetting SummarySeconds { get; } = new RangeIntModSetting(
            (int)StartValue(c => c.SummarySeconds, Config.SummarySecondsDefault), (int)Config.SummarySecondsMin, (int)Config.SummarySecondsMax,
            ModSettingDescriptor.Create("Summary window (s)")
                .SetTooltip("How often a summary (S) row, averaging every frame since the last one, is written to frames.csv."));

        public RangeIntModSetting ProfileSeconds { get; } = new RangeIntModSetting(
            (int)StartValue(c => c.ProfileSeconds, Config.ProfileSecondsDefault), (int)Config.ProfileSecondsMin, (int)Config.ProfileSecondsMax,
            ModSettingDescriptor.Create("Profile window (s)")
                .SetTooltip("How often profile.csv gets a fresh set of rows for every singleton, entity kind, component and watched method."));

        public RangeIntModSetting SpikeContributors { get; } = new RangeIntModSetting(
            (int)StartValue(c => c.SpikeContributors, Config.SpikeContributorsDefault), 0, PerformanceLog.Profile.TopK,
            ModSettingDescriptor.Create("Spike contributors")
                .SetTooltip("How many of the biggest contributors to each slow frame are written to spikes.csv (0 turns spikes.csv off)."));

        public RangeIntModSetting MaxSlowRowsPerMinute { get; } = new RangeIntModSetting(
            (int)StartValue(c => c.MaxSlowRowsPerMinute, Config.MaxSlowRowsPerMinuteDefault), (int)Config.MaxSlowRowsPerMinuteMin, (int)Config.MaxSlowRowsPerMinuteMax,
            ModSettingDescriptor.Create("Slow frame rows per minute, at most")
                .SetTooltip("At most this many slow frames get a row of their own a minute; the rest are only counted (summary.md says how " +
                            "many), so a game that is slow all the time cannot fill the disk."));

        // No range-checked float setting exists in this version of Mod Settings, so this is a plain float; ApplyTo clamps it the same
        // way PerformanceLog.cfg's own OverheadBudgetPercent is clamped.
        public ModSetting<float> OverheadBudgetPercent { get; } = new ModSetting<float>(
            (float)StartValue(c => c.OverheadBudgetPercent, Config.OverheadBudgetPercentDefault),
            ModSettingDescriptor.Create("Overhead budget (% of a frame)")
                .SetTooltip("How much of a second measuring may cost, per second, before entity and component sampling is spread thinner. " +
                            "0.5 is half a percent."));

        public PerformanceSettings(ISettings settings, ModSettingsOwnerRegistry modSettingsOwnerRegistry, ModRepository modRepository)
            : base(settings, modSettingsOwnerRegistry, modRepository)
        {
        }

        // Changeable from the main menu or from inside a running game; either way it only takes effect from the next session (the note above says so).
        public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

        // Must equal the Id in manifest.json.
        protected override string ModId => Plugin.ModId;

        /// <summary>What PerformanceLog.cfg already had for a setting when this page is first built, so a hand-edited .cfg shows up correctly
        /// the first time the menu is opened. After that, whatever is set here is what is used (Mod Settings remembers it, not this mod).</summary>
        static double StartValue(Func<Config, double> read, double fallback)
        {
            try { return Plugin.Config != null ? read(Plugin.Config) : fallback; }
            catch (Exception) { return fallback; }
        }

        /// <summary>
        /// Writes the current in-game values onto <paramref name="cfg"/>, clamped the same way PerformanceLog.cfg is. Called once per
        /// session, before <see cref="Session.Start"/>; never throws, so a problem reading a Mod Settings value cannot stop a session
        /// from starting (the session falls back to whatever <paramref name="cfg"/> already had).
        /// </summary>
        internal void ApplyTo(Config cfg)
        {
            try
            {
                cfg.SlowFrameMs = Config.Clamp(SlowFrameMs.Value, Config.SlowFrameMsMin, Config.SlowFrameMsMax);
                cfg.SummarySeconds = Config.Clamp(SummarySeconds.Value, Config.SummarySecondsMin, Config.SummarySecondsMax);
                cfg.ProfileSeconds = Config.Clamp(ProfileSeconds.Value, Config.ProfileSecondsMin, Config.ProfileSecondsMax);
                cfg.OverheadBudgetPercent = Config.Clamp(OverheadBudgetPercent.Value, Config.OverheadBudgetPercentMin, Config.OverheadBudgetPercentMax);
                cfg.SpikeContributors = (int)Config.Clamp(SpikeContributors.Value, 0, PerformanceLog.Profile.TopK);
                cfg.MaxSlowRowsPerMinute = (int)Config.Clamp(MaxSlowRowsPerMinute.Value, Config.MaxSlowRowsPerMinuteMin, Config.MaxSlowRowsPerMinuteMax);
            }
            catch (Exception e) { Log.Warning("Could not read the in-game settings; using PerformanceLog.cfg's own numbers. " + e.Message); }
        }
    }

    [Context("MainMenu")]
    [Context("Game")]
    public class PerformanceSettingsConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<PerformanceSettings>().AsSingleton();
        }
    }
}
