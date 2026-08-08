using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

using log4net;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Shadowgain 044: per-IP concurrent session limits, live and per-IP-numeric.
    ///
    /// ACE natively caps sessions per IP from Config.js, with an allowlist of IPs that get
    /// unlimited. Two things were wrong with that here:
    ///
    ///   - it is read at STARTUP, so easing the cap for a household meant restarting the world
    ///   - the allowlist is a boolean. An IP is capped or unlimited; "this house gets exactly 2"
    ///     is not expressible, so the only way to accommodate a couple was to exempt them fully
    ///
    /// Both are fixed by reading PropertyManager dials instead. Dials are DB-backed, so the
    /// settings are live AND survive restarts with no config write-back.
    ///
    /// PARSED VALUES ARE CACHED against the raw dial string. This runs on the connection path
    /// for every login packet, so re-splitting the override map each time would be wasteful;
    /// re-parsing only when the string changes keeps it to a reference comparison in the normal
    /// case while still picking up a live edit immediately.
    ///
    /// SAFETY: called from the network path before authentication. It must never throw, and it
    /// must fail OPEN - a malformed override string returns "unlimited" rather than locking
    /// everyone out of the server. A typo in a permission field should not be a denial of
    /// service against yourself.
    /// </summary>
    public static class ShadowgainMultibox
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(ShadowgainMultibox));

        private static readonly object cacheLock = new object();
        private static string cachedRaw;
        private static Dictionary<string, int> cachedMap = new Dictionary<string, int>();

        /// <summary>
        /// Concurrent sessions allowed from this address. Negative means unlimited.
        /// </summary>
        public static int EffectiveLimit(IPAddress address)
        {
            try
            {
                if (address != null)
                {
                    var key = address.ToString();
                    var map = GetMap();

                    if (map.TryGetValue(key, out var perIp))
                        return perIp <= 0 ? -1 : perIp;
                }

                var global = PropertyManager.GetLong("multibox_max_sessions_per_ip").Item;

                if (global < 0)
                    return -1;

                // 0 would mean "nobody may connect", which is never what someone means by a
                // session cap and is an easy typo for "unlimited". Treat it as unlimited.
                if (global == 0)
                    return -1;

                return global > int.MaxValue ? int.MaxValue : (int)global;
            }
            catch (Exception ex)
            {
                // Fail open. A broken dial must not become an outage.
                log.Warn($"[SHADOWGAIN-MULTIBOX] limit lookup failed, allowing connection: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Current overrides, as a copy the caller may keep.
        /// </summary>
        public static Dictionary<string, int> GetOverrides() => new Dictionary<string, int>(GetMap());

        /// <summary>
        /// Add or replace one IP's limit. <paramref name="limit"/> below zero means unlimited.
        /// </summary>
        public static void SetOverride(string ip, int limit)
        {
            var map = new Dictionary<string, int>(GetMap());
            map[ip] = limit;
            Save(map);
        }

        /// <summary>
        /// Remove one IP's override, returning it to the global cap. False if it had none.
        /// </summary>
        public static bool RemoveOverride(string ip)
        {
            var map = new Dictionary<string, int>(GetMap());

            if (!map.Remove(ip))
                return false;

            Save(map);
            return true;
        }

        private static void Save(Dictionary<string, int> map)
        {
            var sb = new StringBuilder();

            foreach (var kvp in map)
            {
                if (sb.Length > 0)
                    sb.Append(';');

                sb.Append(kvp.Key).Append('=').Append(kvp.Value);
            }

            PropertyManager.ModifyString("multibox_ip_overrides", sb.ToString());
        }

        private static Dictionary<string, int> GetMap()
        {
            var raw = PropertyManager.GetString("multibox_ip_overrides").Item ?? "";

            lock (cacheLock)
            {
                if (ReferenceEquals(raw, cachedRaw) || string.Equals(raw, cachedRaw, StringComparison.Ordinal))
                    return cachedMap;

                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var pair in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');

                    if (eq <= 0)
                        continue;

                    var ip = pair.Substring(0, eq).Trim();
                    var val = pair.Substring(eq + 1).Trim();

                    // Skip a malformed entry rather than discarding the whole map - one bad pair
                    // must not silently drop everyone else's exemption.
                    if (ip.Length == 0 || !int.TryParse(val, out var n))
                        continue;

                    map[ip] = n;
                }

                cachedRaw = raw;
                cachedMap = map;
                return map;
            }
        }
    }
}
