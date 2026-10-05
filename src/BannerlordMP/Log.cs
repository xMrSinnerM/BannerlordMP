using System;
using System.IO;
using TaleWorlds.Library;

namespace BannerlordMP
{
    internal static class Log
    {
        private static readonly object Lock = new object();
        private static string _path;

        private static string LogPath => _path ?? (_path = Path.Combine(MpConfig.ModuleDirectory, "BannerlordMP.log"));

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception e = null) => Write("ERROR", e == null ? message : message + ": " + e);

        /// <summary>Logs and shows the message in the game's message feed.</summary>
        public static void Notify(string message)
        {
            Info(message);
            InformationManager.DisplayMessage(new InformationMessage("[MP] " + message));
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take the game down.
            }
        }
    }
}
