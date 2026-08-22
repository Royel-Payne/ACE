using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network;
using ACE.Server.Managers;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 193: the unified-progression recompute.
    ///
    /// THIS FILE IS THE MIGRATION, and per 190/192 the migration IS the reset. Under the unified model
    /// character level is derived from use rather than kills, so every existing character is carrying a
    /// level that was earned under the old rules. Recomputing it is what makes the model retroactive.
    ///
    /// Deliberately a CONSOLE command, not an in-game one. It has to run with nobody online (a level
    /// change under a live player's feet would fight their own session's save), and console commands
    /// are the only ones reachable while the world is closed - which is exactly the state DEPLOY.md
    /// Phase 5 puts the server in for a migration.
    ///
    /// Rules it follows, from DEPLOY.md Phase 5 and Task.md 190 option (a):
    ///   1. DRY RUN BY DEFAULT. Writing requires the explicit `apply` argument.
    ///   2. A human reads the dry run before it is applied.
    ///   3. Idempotent - it recomputes from source totals rather than adjusting by a delta, so running
    ///      it twice produces the same answer and re-running after a partial failure is safe.
    ///   4. REFUSES to write with players online.
    /// </summary>
    public static class ShadowgainRebalanceCommands
    {
        /// <summary>
        /// IPlayer does not expose Biota - Player (WorldObject) and OfflinePlayer each do, separately.
        /// One helper so the DRY RUN and the APPLY pass can never drift in how they compute the total.
        /// </summary>
        private static bool TryGetUseXp(IPlayer p, out long skillXp, out long attrXp)
        {
            skillXp = 0;
            attrXp = 0;

            var biota = (p as Player)?.Biota ?? (p as OfflinePlayer)?.Biota;

            if (biota == null)
                return false;

            if (biota.PropertiesSkill != null)
                foreach (var s in biota.PropertiesSkill.Values)
                    skillXp += s.PP;

            if (biota.PropertiesAttribute != null)
                foreach (var a in biota.PropertiesAttribute.Values)
                    attrXp += a.CPSpent;

            return true;
        }

        [CommandHandler("sg-unify-levels", AccessLevel.Admin, CommandHandlerFlag.ConsoleInvoke, 0,
            "Recompute character level from total skill + attribute XP (Shadowgain 193 unified progression).",
            "[apply]\n"
            + "No argument = DRY RUN, prints the table and writes nothing.\n"
            + "'apply'     = writes the new level and TotalExperience. Refuses if any player is online.")]
        public static void HandleUnifyLevels(Session session, params string[] parameters)
        {
            var apply = parameters.Length > 0 && parameters[0].Equals("apply", StringComparison.OrdinalIgnoreCase);

            var online = PlayerManager.GetAllOnline().Count;

            if (apply && online > 0)
            {
                Console.WriteLine($"REFUSED: {online} player(s) online. A migration must run on an empty world.");
                Console.WriteLine("Close the world (modifybool world_closed true), let it drain, then retry.");
                return;
            }

            // The real table, read from the DAT rather than the 52-point interpolation 192 used for
            // its projection. 192 flagged this explicitly as the thing a real migration must not reuse.
            var xpTable = DatManager.PortalDat.XpTable.CharacterLevelXPList;
            var maxLevel = xpTable.Count - 1;

            Console.WriteLine($"=== sg-unify-levels: {(apply ? "APPLY" : "DRY RUN")} ===");
            Console.WriteLine($"level table: {xpTable.Count} entries, max level {maxLevel}, "
                              + $"XP at max {xpTable[maxLevel]:N0}");
            Console.WriteLine($"players online: {online}");
            Console.WriteLine();
            Console.WriteLine($"{"character",-22}{"now",6}{"skill XP",18}{"attr XP",16}{"new",6}{"delta",7}");

            var rows = new List<(string Name, int Now, long Skill, long Attr, int New)>();

            foreach (var p in PlayerManager.GetAllPlayers())
            {
                if (!TryGetUseXp(p, out var skillXp, out var attrXp))
                    continue;

                var total = skillXp + attrXp;

                // Walk the same table CheckForLevelup walks, so the migration and live levelling can
                // never disagree about what a total is worth.
                var newLevel = 0;

                while (newLevel < maxLevel && (ulong)total >= xpTable[newLevel + 1])
                    newLevel++;

                rows.Add((p.Name, p.Level ?? 1, skillXp, attrXp, newLevel));
            }

            foreach (var r in rows.OrderByDescending(r => r.Now))
            {
                var delta = r.New - r.Now;
                Console.WriteLine($"{r.Name,-22}{r.Now,6}{r.Skill,18:N0}{r.Attr,16:N0}{r.New,6}{delta,7}");
            }

            Console.WriteLine();
            Console.WriteLine($"{rows.Count} character(s). "
                              + $"{rows.Count(r => r.New < r.Now)} would DROP, "
                              + $"{rows.Count(r => r.New > r.Now)} would RISE, "
                              + $"{rows.Count(r => r.New == r.Now)} unchanged.");

            if (!apply)
            {
                Console.WriteLine();
                Console.WriteLine("DRY RUN - nothing written. Re-run with 'apply' once a human has read the above.");
                return;
            }

            var written = 0;

            foreach (var p in PlayerManager.GetAllPlayers())
            {
                if (!TryGetUseXp(p, out var skillXp, out var attrXp))
                    continue;

                var total = skillXp + attrXp;

                var newLevel = 0;

                while (newLevel < maxLevel && (ulong)total >= xpTable[newLevel + 1])
                    newLevel++;

                // Set BOTH, and set TotalExperience to the use-total rather than to the level
                // threshold. Under the unified model TotalExperience IS the use-total - that is the
                // whole design - so writing the threshold would silently discard progress toward the
                // next level and make the migration non-idempotent.
                if (p is Player onlinePlayer)
                {
                    onlinePlayer.SetProperty(PropertyInt.Level, newLevel);
                    onlinePlayer.SetProperty(PropertyInt64.TotalExperience, total);
                    onlinePlayer.SaveBiotaToDatabase();
                }
                else if (p is OfflinePlayer offlinePlayer)
                {
                    offlinePlayer.SetProperty(PropertyInt.Level, newLevel);
                    offlinePlayer.SetProperty(PropertyInt64.TotalExperience, total);
                }
                else
                    continue;

                written++;
            }

            Console.WriteLine();
            // Offline edits live in the cache until this flush; without it the migration would
            // report success and vanish on restart.
            PlayerManager.SaveOfflinePlayersWithChanges();

            Console.WriteLine($"APPLIED to {written} character(s).");
            Console.WriteLine("Re-run without 'apply' to confirm idempotency: every delta should read 0.");
        }
    }
}
