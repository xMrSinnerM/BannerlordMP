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
            // Two game windows on one PC share this file (same Modules folder): open it shared, retry briefly,
            // and tag each line with the side that wrote it.
            var role = Session.MpSession.Current == null ? "" : Session.MpSession.Current.IsHost ? "[server] " : "[client] ";
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss.fff} {level} {role}{message}{Environment.NewLine}");
            lock (Lock)
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            stream.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(2);
                    }
                    catch
                    {
                        return; // Logging must never take the game down.
                    }
                }
            }
        }
    }
}
