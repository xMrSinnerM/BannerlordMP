using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BannerlordMP.Core.Time;

namespace BannerlordMP
{
    /// <summary>Settings read from Modules/BannerlordMP/config.ini.</summary>
    internal sealed class MpConfig
    {
        public string PlayerName = Environment.UserName;
        public int Port = 7777;
        public TimeArbitrationMode TimeArbitration = TimeArbitrationMode.LastRequestWins;
        public bool DetachDuringConversations = true;
        public bool DedicatedHost;
        public float SnapshotRateHz = 4f;
        public float CatchUpMultiplier = 16f;
        public double CatchUpThresholdHours = 0.5;

        public static string ModuleDirectory
        {
            get
            {
                // <Module>/bin/Win64_Shipping_Client/BannerlordMP.dll
                var dll = typeof(MpConfig).Assembly.Location;
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dll) ?? ".", "..", ".."));
            }
        }

        public static string FilePath => Path.Combine(ModuleDirectory, "config.ini");

        public TimeSyncSettings ToSyncSettings()
        {
            return new TimeSyncSettings
            {
                CatchUpMultiplier = CatchUpMultiplier,
                CatchUpThresholdHours = CatchUpThresholdHours,
                AheadThresholdHours = CatchUpThresholdHours,
            };
        }

        public static MpConfig Load()
        {
            var config = new MpConfig();
            try
            {
                if (!File.Exists(FilePath))
                    return config;

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                        values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }

                if (values.TryGetValue("PlayerName", out var name) && name.Length > 0)
                    config.PlayerName = name;
                if (values.TryGetValue("Port", out var port) && int.TryParse(port, out var p))
                    config.Port = p;
                if (values.TryGetValue("TimeArbitration", out var mode) && Enum.TryParse(mode, true, out TimeArbitrationMode m))
                    config.TimeArbitration = m;
                if (values.TryGetValue("DetachDuringConversations", out var detach) && bool.TryParse(detach, out var d))
                    config.DetachDuringConversations = d;
                if (values.TryGetValue("DedicatedHost", out var dedicated) && bool.TryParse(dedicated, out var dh))
                    config.DedicatedHost = dh;
                if (values.TryGetValue("SnapshotRateHz", out var rate) && float.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                    config.SnapshotRateHz = r;
                if (values.TryGetValue("CatchUpMultiplier", out var mult) && float.TryParse(mult, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) && cm >= 1)
                    config.CatchUpMultiplier = cm;
                if (values.TryGetValue("CatchUpThresholdHours", out var thr) && double.TryParse(thr, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0)
                    config.CatchUpThresholdHours = t;
            }
            catch (Exception e)
            {
                Log.Error("Could not read config.ini, using defaults", e);
            }
            return config;
        }
    }
}
