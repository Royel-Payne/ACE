using System;
using log4net;
using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Entity
{
    public class Proficiency
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// LEGACY (Shadowgain 003): this was the throttle for the old repeat-use gate - a skill could
        /// only re-award once 15 minutes had elapsed, unless the target was strictly harder than the
        /// last one. That gate has been removed; nothing reads this any more. Retained so any external
        /// Mods referencing Proficiency.FullTime still compile.
        /// </summary>
        public static TimeSpan FullTime = TimeSpan.FromMinutes(15);

        public static void OnSuccessUse(Player player, CreatureSkill skill, uint difficulty)
        {
            //Console.WriteLine($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, targetDiff: {difficulty})");

            // TODO: this formula still probably needs some work to match up with retail truly...

            // possible todo: does this only apply to players?
            // ie., can monsters still level up from skill usage, or killing players?
            // it was possible on release, but i think they might have removed that feature?

            // Shadowgain: opt-in per-award instrumentation. p_p / ExperienceSpent is written by both
            // this method and manual HandleActionRaiseSkill spending, so diffing the DB cannot tell
            // passive gain from a player spending unassigned XP. These lines are the only unambiguous
            // record of what proficiency actually awarded. Off by default; enable with
            // /modifybool proficiency_debug_logging true
            var debug = PropertyManager.GetBool("proficiency_debug_logging").Item;

            if (player.IsOlthoiPlayer)
                return;

            // ensure skill is at least trained
            if (skill.AdvancementClass < SkillAdvancementClass.Trained)
            {
                if (debug)
                    log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | BLOCKED=untrained | sac={skill.AdvancementClass} | difficulty={difficulty}");

                return;
            }

            var last_difficulty = skill.PropertiesSkill.ResistanceAtLastCheck;
            var last_used_time = skill.PropertiesSkill.LastUsedTime;

            var currentTime = Time.GetUnixTime();

            var timeDiff = currentTime - last_used_time;

            if (timeDiff < 0)
            {
                // can happen if server clock is rewound back in time
                log.Warn($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - timeDiff: {timeDiff}");
                timeDiff = 0;   // no longer fatal: nothing gates on elapsed time any more
            }

            // ----------------------------------------------------------------------------------
            // Shadowgain 003: the repeat-use gate is GONE.
            //
            // Upstream awarded only when (difficulty > ResistanceAtLastCheck || timeSinceLastUse
            // >= 15 min). Repeating a skill against same-difficulty targets therefore paid
            // literally nothing - measured in 001, where a second kill with the same spell 11
            // minutes later awarded exactly zero. That is fundamentally incompatible with "skills
            // rise by use", and no amount of re-tuning the multiplier would have fixed it.
            //
            // Anti-farming is now the difficulty-relative modifier below instead of a timer:
            // trivial targets trickle, appropriately-hard targets pay full.
            //
            // These two fields are still maintained - not as a gate now, but as telemetry
            // (last difficulty faced / last time used) that the logging and other systems read.
            // ----------------------------------------------------------------------------------
            skill.PropertiesSkill.ResistanceAtLastCheck = difficulty;
            skill.PropertiesSkill.LastUsedTime = currentTime;

            player.ChangesDetected = true;

            // Difficulty relative to the actor's CURRENT skill. A target trivial for a master pays
            // a trickle; one that stretches them pays full credit. This is what makes farming
            // chickens pointless once strong, per Greylock's example.
            var floor = PropertyManager.GetDouble("skill_gain_difficulty_floor").Item;
            var cap = PropertyManager.GetDouble("skill_gain_difficulty_cap").Item;
            var multiplier = PropertyManager.GetDouble("skill_gain_multiplier").Item;
            var minAward = (uint)Math.Max(0, PropertyManager.GetLong("skill_gain_min_award").Item);

            var current = Math.Max(1u, skill.Current);
            var ratio = (double)difficulty / current;

            // Math.Min/Max guard the clamp in case an operator sets floor above cap live.
            var difficultyFactor = Math.Clamp(ratio, Math.Min(floor, cap), Math.Max(floor, cap));

            // Model A: base points (difficulty) x difficultyFactor x global multiplier, written as
            // raw XP into the skill. The steep native XP tables then give "fast early, slow to
            // master" for free - no normalisation needed (Model B was considered and rejected).
            var awarded = difficulty * difficultyFactor * multiplier;

            // These knobs are operator-settable live to arbitrary values. Clamp into uint range
            // before casting: a large multiplier would otherwise overflow and wrap to a garbage
            // (often tiny) award, and a negative one would wrap to something enormous.
            if (double.IsNaN(awarded) || awarded < 0)
                awarded = 0;

            var pp = (uint)Math.Min(uint.MaxValue, Math.Max(minAward, Math.Round(awarded)));

            var prevRank = skill.Ranks;
            var prevXP = skill.ExperienceSpent;

            // Direct write. Deliberately bypasses GrantXP/HandleActionRaiseSkill, and with them the
            // unassigned-XP pool, the IsMaxLevel early-return, and the GetRemainingXP(maxLevel)
            // bound that upstream imposed. Usage gain must keep working at and past max level, and
            // must not be killed when the player-facing spend path is disabled.
            var applied = player.AwardSkillUsageXP(skill, pp);

            if (debug)
            {
                log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | {(applied ? "AWARD" : "NOOP=maxRank")}" +
                         $" | difficulty={difficulty} vs current={current} ratio={ratio:N3}" +
                         $" | factor={difficultyFactor:N3} mult={multiplier:N2}" +
                         $" | pp={pp}" +
                         $" | rank {prevRank}->{skill.Ranks} xp {prevXP}->{skill.ExperienceSpent}" +
                         $" | sinceLastUse={timeDiff:N1}s prevDifficulty={last_difficulty}");
            }
        }

        public static void OnSuccessUse(Player player, CreatureSkill skill, int difficulty)
        {
            if (difficulty < 0)
            {
                log.Error($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - difficulty cannot be negative");
                return;
            }
            OnSuccessUse(player, skill, (uint)difficulty);
        }
    }
}
