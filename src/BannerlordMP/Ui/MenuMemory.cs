using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BannerlordMP.Ui
{
    /// <summary>
    /// What the menus remember between sessions (last host settings, last server joined, hero name per server),
    /// in Modules/BannerlordMP/menu_memory.ini. Hero passwords are never stored.
    /// </summary>
    internal static class MenuMemory
    {
        private static Dictionary<string, string> _values;

        private static string FilePath => Path.Combine(MpConfig.ModuleDirectory, "menu_memory.ini");

        public static string Get(string key, string fallback = "")
        {
            Load();
            return _values.TryGetValue(key, out var value) ? value : fallback;
        }

        public static int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;

        public static bool GetBool(string key, bool fallback) => bool.TryParse(Get(key), out var v) ? v : fallback;

        public static void Set(string key, object value)
        {
            Load();
            _values[key] = (value?.ToString() ?? string.Empty).Replace("\n", " ").Replace("\r", " ");
            try
            {
                File.WriteAllLines(FilePath, _values.Select(p => p.Key + "=" + p.Value));
            }
            catch (Exception e)
            {
                Log.Error("Could not save menu choices", e);
            }
        }

        private static void Load()
        {
            if (_values != null)
                return;
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath))
                    return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                        _values[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
            }
            catch (Exception e)
            {
                Log.Error("Could not read menu choices", e);
            }
        }
    }
}
