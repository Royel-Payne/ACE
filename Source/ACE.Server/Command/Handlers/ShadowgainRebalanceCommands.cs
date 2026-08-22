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

            // 196: PP IS A SHADOW, NOT THE TOTAL. Past the dat table top, CreatureSkill pins PP at
            // uint.MaxValue (4,294,967,295) for the wire format and the real total rides in
            // PropertyInt64 ShadowgainSkillXpBase + skill (9100+). Summing PP therefore UNDERSTATES
            // every uncapped skill, and the first run of this migration did exactly that - Adra by
            // 36.1 BILLION, Black Breath 5.5B, Adramelech 5.1B, Apex 0.2B - giving all four a level
            // computed from a number that had stopped moving.
            //
            // Caught because Adramelech noticed the WEBSITE disagreeing with his in-game portal:
            // @myskills reads TrueExperienceSpent, the site summed PP. The display mismatch was the
            // visible edge of this.
            //
            // Mirrors CreatureSkill.TrueExperienceSpent exactly: overflow if present, else PP.
            if (biota.PropertiesSkill != null)
            {
                foreach (var kvp in biota.PropertiesSkill)
                {
                    var overflowProp = (PropertyInt64)((int)PropertyInt64.ShadowgainSkillXpBase + (int)kvp.Key);
                    long overflow = 0;

                    var hasOverflow = biota.PropertiesInt64 != null
                                      && biota.PropertiesInt64.TryGetValue(overflowProp, out overflow);

                    skillXp += hasOverflow ? overflow : kvp.Value.PP;
                }
            }

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
            Console.WriteLine($"{"character",-22}{"now",6}{"new",6}{"delta",7}{"unassigned now",20}{"->",6}");

            var rows = new List<(string Name, int Now, long Unassigned, long Attr, int New)>();

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

                var unassigned = (p as Player)?.AvailableExperience
                                 ?? (p as OfflinePlayer)?.GetProperty(PropertyInt64.AvailableExperience) ?? 0;

                rows.Add((p.Name, p.Level ?? 1, unassigned, attrXp, newLevel));
            }

            foreach (var r in rows.OrderByDescending(r => r.Now))
            {
                var delta = r.New - r.Now;
                Console.WriteLine($"{r.Name,-22}{r.Now,6}{r.New,6}{delta,7}{r.Unassigned,20:N0}{0,6}");
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

            // AvailableExperience is ZEROED, and it has to be. It is the unassigned pool, which under
            // the old rules accumulated every point of kill XP - Adramelech was carrying 190.4 BILLION
            // against a recomputed total of 25.5B. Under unified progression that pool is fed by QUESTS
            // only and buys augmentations, so a legacy balance is not "saved up", it is a leftover from
            // a currency that no longer exists.
            //
            // The alternative considered was capping it at the new TotalExperience rather than zeroing.
            // Rejected as arbitrary: it would preserve a number with no meaning under the new rules.
            // Chris: "reset it, sync it with character skill xp, they're grossly inflated now."
            //
            // Consequence worth stating plainly: this removes existing augmentation purchasing power.
            // Players re-earn it through quests, which is the design.
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
                    onlinePlayer.SetProperty(PropertyInt64.AvailableExperience, 0);

                    // DeathLevel must not outlive the level scale it was recorded on. Vitae is worked
                    // off against VitaeCPPoolThreshold = (level^2.5 * 2.5 + 20) * vitae^5, so a
                    // character who died at 275 and recomputes to 8 keeps a threshold ~6,600x too
                    // large and can never clear the penalty. Clamping down only - a DeathLevel BELOW
                    // the new level is harmless and is left alone.
                    var deathOn = onlinePlayer.GetProperty(PropertyInt.DeathLevel) ?? 0;

                    if (deathOn > newLevel)
                        onlinePlayer.SetProperty(PropertyInt.DeathLevel, newLevel);

                    onlinePlayer.SaveBiotaToDatabase();
                }
                else if (p is OfflinePlayer offlinePlayer)
                {
                    offlinePlayer.SetProperty(PropertyInt.Level, newLevel);
                    offlinePlayer.SetProperty(PropertyInt64.TotalExperience, total);
                    offlinePlayer.SetProperty(PropertyInt64.AvailableExperience, 0);

                    // DeathLevel must not outlive the level scale it was recorded on. Vitae is worked
                    // off against VitaeCPPoolThreshold = (level^2.5 * 2.5 + 20) * vitae^5, so a
                    // character who died at 275 and recomputes to 8 keeps a threshold ~6,600x too
                    // large and can never clear the penalty. Clamping down only - a DeathLevel BELOW
                    // the new level is harmless and is left alone.
                    var deathOff = offlinePlayer.GetProperty(PropertyInt.DeathLevel) ?? 0;

                    if (deathOff > newLevel)
                        offlinePlayer.SetProperty(PropertyInt.DeathLevel, newLevel);

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
