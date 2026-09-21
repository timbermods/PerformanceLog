using System;

namespace PerformanceLog
{
    /// <summary>The mod's lines in the game's Player.log.</summary>
    internal static class Log
    {
        const string Prefix = "[PerformanceLog] ";

        // Replaced by the tests, where UnityEngine.Debug is not callable.
        public static Action<string> Sink = message => UnityEngine.Debug.Log(message);
        public static Action<string> WarningSink = message => UnityEngine.Debug.LogWarning(message);

        public static void Info(string message)
        {
            try { Sink(Prefix + message); } catch (Exception) { }
        }

        public static void Warning(string message)
        {
            try { WarningSink(Prefix + message); } catch (Exception) { }
        }
    }
}
