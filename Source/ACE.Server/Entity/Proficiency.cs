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
                skill.PropertiesSkill.LastUsedTime = currentTime;       // update to prevent log spam
                return;
            }

            var difficulty_check = difficulty > last_difficulty;
            var time_check = timeDiff >= FullTime.TotalSeconds;

            if (debug && !difficulty_check && !time_check)
            {
                // The gate denied an award. Logging these is as valuable as logging the grants -
                // it measures how often the repeat-use gate suppresses gain during real play.
                log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | BLOCKED=gate | difficulty={difficulty} <= lastDifficulty={last_difficulty} | timeDiff={timeDiff:N1}s < {FullTime.TotalSeconds}s | rank={skill.Ranks} xp={skill.ExperienceSpent}");
            }

            if (difficulty_check || time_check)
            {
                // todo: not independent variables?
                // always scale if timeDiff < FullTime?
                var timeScale = 1.0f;
                if (!time_check)
                {
                    // 10 mins elapsed from 15 min FullTime:
                    // 0.66f timeScale
                    timeScale = (float)(timeDiff / FullTime.TotalSeconds);

                    // any rng involved?
                }

                skill.PropertiesSkill.ResistanceAtLastCheck = difficulty;
                skill.PropertiesSkill.LastUsedTime = currentTime;

                player.ChangesDetected = true;

                if (player.IsMaxLevel)
                {
                    if (debug)
                        log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | BLOCKED=maxLevel | difficulty={difficulty} | gate passed but no XP awarded");

                    return;
                }

                var pp = (uint)Math.Round(difficulty * timeScale);
                var totalXPGranted = (long)Math.Round(pp * 1.1f);   // give additional 10% of proficiency XP to unassigned XP

                if (totalXPGranted > 10000)
                {
                    log.Warn($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - totalXPGranted: {totalXPGranted:N0}");
                }

                var maxLevel = Player.GetMaxLevel();
                var remainingXP = player.GetRemainingXP(maxLevel).Value;

                if (totalXPGranted > remainingXP)
                {
                    // checks and balances:
                    // total xp = pp * 1.1
                    // pp = total xp / 1.1

                    totalXPGranted = remainingXP;
                    pp = (uint)Math.Round(totalXPGranted / 1.1f);
                }

                // if skill is maxed out, but player is below MaxLevel,
                // not sure if retail granted 0%, 10%, or 110% of the pp to TotalExperience here
                // since pp is such a miniscule system at the higher levels,
                // going to just naturally add it to TotalXP for now..

                pp = Math.Min(pp, skill.ExperienceLeft);

                //Console.WriteLine($"Earned {pp} PP ({skill.Skill})");

                var prevRank = skill.Ranks;
                var prevXP = skill.ExperienceSpent;

                // send CP to player as unassigned XP
                player.GrantXP(totalXPGranted, XpType.Proficiency, ShareType.None);

                // send PP to player as skill XP, which gets spent from the CP sent
                if (pp > 0)
                {
                    player.HandleActionRaiseSkill(skill.Skill, pp);
                }

                if (debug)
                {
                    log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | AWARD" +
                             $" | difficulty={difficulty} (lastDifficulty={last_difficulty})" +
                             $" | trigger={(difficulty_check ? "harderTarget" : "15minTimer")}" +
                             $" | timeDiff={timeDiff:N1}s timeScale={timeScale:N3}" +
                             $" | pp={pp} grantedXP={totalXPGranted}" +
                             $" | rank {prevRank}->{skill.Ranks} xp {prevXP}->{skill.ExperienceSpent}");
                }
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
