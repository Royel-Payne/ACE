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

        /// <param name="weight">
        /// Shadowgain 103: linear scale on the final award, for callers that need to pay a skill at a
        /// different rate from the action's face value. Applied to the multiplier, so 0.5 really is
        /// half the XP - deliberately NOT folded into `difficulty`, which appears twice in the
        /// formula and would make a 0.5 knob pay a quarter.
        ///
        /// Used by the salvage path, where one action pays Salvaging at full rate and the matching
        /// tinkering skill at its own, independently tunable rate.
        /// </param>
        /// <param name="opponent">
        /// Shadowgain 119: the OTHER party to this action, when there is one - the target being hit,
        /// the attacker being evaded, the creature being assessed. Supplied so the PvP gate can be
        /// decided in ONE place rather than at each call site. Leave null for actions with no second
        /// party (salvage, tinkering, jump, run, the allegiance skills).
        /// </param>
        /// <param name="boundDifficulty">
        /// Shadowgain 119: whether <paramref name="difficulty"/> is on the same scale as the skill's
        /// Base, and can therefore be bounded against it. True for everything whose difficulty comes
        /// from a creature's skill value. FALSE for the allegiance/pet paths, where difficulty is an
        /// XP magnitude already divided down by its own dial - a different unit entirely, which the
        /// skill's Base says nothing about. See the note on the bound below.
        /// </param>
        public static void OnSuccessUse(Player player, CreatureSkill skill, uint difficulty, double weight = 1.0,
            WorldObject opponent = null, bool boundDifficulty = true)
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

            // Shadowgain 119: PvP usage gain. Reported by Jkurs, who asked for his own exploit to be
            // patched - "i would disable pkl gains btw. i duno why thats allowed lmao".
            if (!AllowsUsageGain(player, opponent))
            {
                if (debug)
                    log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | BLOCKED=pvp | opponent={opponent?.Name} | difficulty={difficulty}");

                return;
            }

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

            // A never-used skill has LastUsedTime 0, which would otherwise report a nonsense
            // "seconds since last use" of ~1.79 billion (i.e. the unix epoch) in the logs.
            var timeDiff = last_used_time > 0 ? currentTime - last_used_time : 0;

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

            // Per-skill-type override (design decision #5). Measured 2026-08-06: the difficulty inputs
            // are not on comparable scales. Against similar-tier content, melee read the target's
            // physical defense at ~72 while war magic read its Magic Defense at ~12 - so normalising
            // magic to the target fixed its *scaling* (it varies with the fight now) but left it an
            // order of magnitude behind melee in *magnitude*. A global multiplier cannot correct that,
            // since it moves both paths together. This knob can.
            if (IsMagicSkill(skill.Skill))
                multiplier *= PropertyManager.GetDouble("skill_gain_magic_multiplier").Item;

            // Shadowgain 005: uncapping removes Specialized's old advantage - a higher rank ceiling -
            // so without this Trained and Specialized collapse into identical progression, differing
            // only by the +5/+10 InitLevel head start. Specialized instead grows FASTER per use.
            if (skill.AdvancementClass == SkillAdvancementClass.Specialized)
                multiplier *= PropertyManager.GetDouble("spec_gain_multiplier").Item;

            // Shadowgain 021: the character's progression lane. Applied to skill gain, attribute
            // gain and kill XP alike so power and level advance together - see Player_Progression.
            multiplier *= player.ProgressionSpeed;

            // Shadowgain 103: caller-supplied rate. 1.0 for everything except the salvage path.
            if (double.IsNaN(weight) || weight < 0)
                weight = 0;

            multiplier *= weight;
            var minAward = (uint)Math.Max(0, PropertyManager.GetLong("skill_gain_min_award").Item);

            // Base, deliberately NOT Current. Current applies enchantment multipliers and vitae, so
            // using it made being buffed reduce your gain - and buffing is the normal state in AC, even
            // melee players self-buff. That created a perverse incentive to play unbuffed to progress
            // faster, and made measurements depend on buff state. Base is the actual trained level, so
            // buffs still help you land hits and survive; they just no longer tax your learning.
            var baseValue = Math.Max(1u, skill.Base);

            // ----------------------------------------------------------------------------------
            // Shadowgain 119: BOUND THE DIFFICULTY AGAINST THE ACTOR'S OWN BASE.
            //
            // The clamp below bounds the RATIO. It never bounded `difficulty`, which is also a free
            // multiplier out front - so the award grew linearly with target difficulty forever.
            // Apex worked this out and reported it: "if someone with 10 attack skill hit something
            // with 500 defense, it's like 100k xp is given per hit". Reproduced exactly from the
            // code - 500 x 2.0 (ratio capped) x 100 (fast lane) = 100,000.
            //
            // 003 removed upstream's repeat-use timer and made the difficulty-relative modifier the
            // ONLY anti-farm mechanism - "trivial targets trickle, appropriately-hard targets pay
            // full". That was verified in the trivial direction and is silently wrong in the other.
            //
            // The ceiling now scales with the ACTOR rather than with whatever they can find to hit,
            // so "an appropriately-hard target pays full" survives and "an impossible target pays
            // absurdly" does not. It also closes, sight unseen, the high-defence mob Apex declined
            // to name - the cap is relative, so no specific creature has to be found.
            //
            // K MEASURED, NOT GUESSED, from 216,629 skill awards over 24h on LIVE 2026-08-13:
            // every target-derived skill topped out below 2.6 (MeleeDefense max 2.04,
            // TwoHandedCombat 2.28, ArcaneLore 2.52), and of 130,189 attribute awards exactly TWO
            // exceeded 3.0. Since the ratio itself caps at 2.0, any K >= 2 leaves normal fights
            // untouched; K = 3 sits clear of the highest legitimate ratio ever observed, and
            // deliberately clears ArcaneLore's 2.52 so it does not quietly undo 114.
            //
            // Set the dial to 0 to disable the bound entirely (restores pre-119 behaviour).
            // ----------------------------------------------------------------------------------
            var effectiveDifficulty = difficulty;

            if (boundDifficulty)
            {
                var bound = PropertyManager.GetDouble("skill_gain_difficulty_bound").Item;

                if (bound > 0)
                {
                    var ceiling = baseValue * bound;

                    if (ceiling < effectiveDifficulty)
                        effectiveDifficulty = (uint)Math.Max(1, Math.Round(ceiling));
                }
            }

            var ratio = (double)effectiveDifficulty / baseValue;

            // Math.Min/Max guard the clamp in case an operator sets floor above cap live.
            var difficultyFactor = Math.Clamp(ratio, Math.Min(floor, cap), Math.Max(floor, cap));

            // Model A: base points (difficulty) x difficultyFactor x global multiplier, written as
            // raw XP into the skill. The steep native XP tables then give "fast early, slow to
            // master" for free - no normalisation needed (Model B was considered and rejected).
            var awarded = effectiveDifficulty * difficultyFactor * multiplier;

            // These knobs are operator-settable live to arbitrary values. Clamp into uint range
            // before casting: a large multiplier would otherwise overflow and wrap to a garbage
            // (often tiny) award, and a negative one would wrap to something enormous.
            if (double.IsNaN(awarded) || awarded < 0)
                awarded = 0;

            var pp = (uint)Math.Min(uint.MaxValue, Math.Max(minAward, Math.Round(awarded)));

            var prevRank = skill.Ranks;

            // Shadowgain 109e: log the TRUE total and the overflow field, not the clamped shadow.
            //
            // This line lied on LIVE within minutes of 109 shipping. Past the table top BOTH of the
            // fields it printed are pinned - ExperienceSpent at uint.MaxValue and Ranks at the table
            // maximum, because the overflow rides in InitLevel - so a skill banking 4.1 MILLION
            // experience logged as
            //
            //     rank 208->208 xp 4294967295->4294967295
            //
            // which reads as "nothing happened" when the award had in fact landed. Only the database
            // told the truth. That is exactly the blindness 109 existed to remove, reintroduced in
            // the one place someone would go to check whether 109 was working.
            var prevInitLevel = skill.InitLevel;
            var prevXP = skill.TrueExperienceSpent;

            // Direct write. Deliberately bypasses GrantXP/HandleActionRaiseSkill, and with them the
            // unassigned-XP pool, the IsMaxLevel early-return, and the GetRemainingXP(maxLevel)
            // bound that upstream imposed. Usage gain must keep working at and past max level, and
            // must not be killed when the player-facing spend path is disabled.
            var applied = player.AwardSkillUsageXP(skill, pp);

            if (debug)
            {
                // Shadowgain 119: print the RAW difficulty as well whenever the bound actually bit,
                // so the log shows what was faced AND what was paid for. Printing only the bounded
                // value would hide the very thing this fix exists to make visible - the same
                // blindness 109e removed from the line below it.
                var boundNote = effectiveDifficulty != difficulty ? $" (bounded from {difficulty})" : "";

                log.Info($"[PROFICIENCY] {player.Name} | {skill.Skill} | {(applied ? "AWARD" : "NOOP=maxRank")}" +
                         $" | difficulty={effectiveDifficulty}{boundNote} vs base={baseValue} ratio={ratio:N3}" +
                         $" | factor={difficultyFactor:N3} mult={multiplier:N2}" +
                         $" | pp={pp}" +
                         $" | rank {prevRank}->{skill.Ranks} init {prevInitLevel}->{skill.InitLevel} xp {prevXP}->{skill.TrueExperienceSpent}" +
                         $" | sinceLastUse={timeDiff:N1}s prevDifficulty={last_difficulty}");
            }
        }

        /// <summary>
        /// Shadowgain 119: THE one place that decides whether an action against another party trains
        /// anything. Skills ask via OnSuccessUse; the attribute and specialty awards at the same call
        /// sites ask directly, so a player-vs-player action cannot pay one of the three and not the
        /// others.
        ///
        /// Both Player_Combat.OnDamageTarget and OnEvade called OnSuccessUse with no check on who the
        /// other party was. Attack difficulty is the target's effective defence and evade difficulty
        /// is the attacker's weapon skill, so two accounts could train each other - and because a
        /// developed character has high defence, a low-skill alt hitting a high-skill main was
        /// precisely the unbounded-difficulty case above. The two exploits multiplied.
        ///
        /// Behind a dial rather than hardcoded: PvP training is a legitimate design choice somebody
        /// might want later, and this is a policy question, not a bug.
        ///
        /// Self-targeted actions are NOT blocked - `opponent == actor` is how self-buffs, self-heals
        /// and self-appraisal arrive here, and they are ordinary play rather than two-account farming.
        /// </summary>
        public static bool AllowsUsageGain(Player actor, WorldObject opponent)
        {
            if (opponent == null || ReferenceEquals(opponent, actor))
                return true;

            if (!(opponent is Player))
                return true;

            return PropertyManager.GetBool("skill_gain_allow_pvp").Item;
        }

        /// <summary>
        /// Shadowgain: the magic skills whose difficulty comes from a magic-side stat, and which
        /// therefore share the low-magnitude problem that skill_gain_magic_multiplier compensates for.
        /// </summary>
        private static bool IsMagicSkill(Skill skill)
        {
            switch (skill)
            {
                case Skill.WarMagic:
                case Skill.LifeMagic:
                case Skill.CreatureEnchantment:
                case Skill.ItemEnchantment:
                case Skill.VoidMagic:
                case Skill.ManaConversion:
                case Skill.MagicDefense:
                    return true;
                default:
                    return false;
            }
        }

        public static void OnSuccessUse(Player player, CreatureSkill skill, int difficulty, WorldObject opponent = null)
        {
            if (difficulty < 0)
            {
                log.Error($"Proficiency.OnSuccessUse({player.Name}, {skill.Skill}, {difficulty}) - difficulty cannot be negative");
                return;
            }
            OnSuccessUse(player, skill, (uint)difficulty, opponent: opponent);
        }
    }
}
