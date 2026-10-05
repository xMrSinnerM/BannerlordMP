using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BannerlordMP.Core.Time;
using BannerlordMP.Steam;

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
        public string ServerName = "Calradia Co-op";
        /// <summary>Empty for an open server.</summary>
        public string ServerPassword = string.Empty;
        /// <summary>How many player heroes the server allows.</summary>
        public int MaxSlots = 4;
        public LobbyVisibility SteamVisibility = LobbyVisibility.FriendsOnly;
        public float SnapshotRateHz = 4f;
        public float CatchUpMultiplier = 16f;
        public double CatchUpThresholdHours = 0.5;

        // Client features that can each be switched off on their own, to find which one misbehaves.
        /// <summary>Switch off this campaign's own world simulation while joined (the host runs it).</summary>
        public bool ClientMirrorWorld = true;
        /// <summary>Turn every other party into a puppet (AI off, holding still) while joined.</summary>
        public bool ClientPuppetParties = true;
        /// <summary>Create stand-ins for parties the host spawns.</summary>
        public bool ClientMirrorSpawns = true;
        /// <summary>Remove local parties the host no longer has.</summary>
        public bool ClientRemoveMissingParties = true;
        /// <summary>Apply troop counts of nearby parties from the host.</summary>
        public bool ClientSyncRosters = true;
        /// <summary>Keep the player's own party and hero in agreement with the host (gold, troops, items, xp...).</summary>
        public bool ClientSyncOwnParty = true;
        /// <summary>Start encounters the host asks for (AI parties attacking you).</summary>
        public bool ClientAcceptEncounterRequests = true;
        /// <summary>Blend positions between updates instead of jumping.</summary>
        public bool ClientSmoothPositions = true;

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

        public MpConfig Clone() => (MpConfig)MemberwiseClone();

        private static bool Flag(Dictionary<string, string> values, string key, bool fallback)
        {
            return values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;
        }

        public string DescribeClientFeatures()
        {
            return $"mirrorWorld={ClientMirrorWorld} puppets={ClientPuppetParties} spawns={ClientMirrorSpawns} removeMissing={ClientRemoveMissingParties} " +
                   $"rosters={ClientSyncRosters} ownParty={ClientSyncOwnParty} encounterRequests={ClientAcceptEncounterRequests} smoothing={ClientSmoothPositions}";
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
                if (values.TryGetValue("ServerName", out var serverName) && serverName.Length > 0)
                    config.ServerName = serverName;
                if (values.TryGetValue("ServerPassword", out var serverPassword))
                    config.ServerPassword = serverPassword;
                if (values.TryGetValue("MaxSlots", out var slots) && int.TryParse(slots, out var ms) && ms > 0)
                    config.MaxSlots = ms;
                if (values.TryGetValue("SteamVisibility", out var vis) && Enum.TryParse(vis, true, out LobbyVisibility v))
                    config.SteamVisibility = v;
                if (values.TryGetValue("DedicatedHost", out var dedicated) && bool.TryParse(dedicated, out var dh))
                    config.DedicatedHost = dh;
                if (values.TryGetValue("SnapshotRateHz", out var rate) && float.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0)
                    config.SnapshotRateHz = r;
                if (values.TryGetValue("CatchUpMultiplier", out var mult) && float.TryParse(mult, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm) && cm >= 1)
                    config.CatchUpMultiplier = cm;
                config.ClientMirrorWorld = Flag(values, "ClientMirrorWorld", config.ClientMirrorWorld);
                config.ClientPuppetParties = Flag(values, "ClientPuppetParties", config.ClientPuppetParties);
                config.ClientMirrorSpawns = Flag(values, "ClientMirrorSpawns", config.ClientMirrorSpawns);
                config.ClientRemoveMissingParties = Flag(values, "ClientRemoveMissingParties", config.ClientRemoveMissingParties);
                config.ClientSyncRosters = Flag(values, "ClientSyncRosters", config.ClientSyncRosters);
                config.ClientSyncOwnParty = Flag(values, "ClientSyncOwnParty", config.ClientSyncOwnParty);
                config.ClientAcceptEncounterRequests = Flag(values, "ClientAcceptEncounterRequests", config.ClientAcceptEncounterRequests);
                config.ClientSmoothPositions = Flag(values, "ClientSmoothPositions", config.ClientSmoothPositions);
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
