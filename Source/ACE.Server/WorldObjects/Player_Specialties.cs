using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Shadowgain 007: trains the combat specialty skills that previously had no usage path.
        ///
        /// Recklessness, Sneak Attack, Dirty Fighting, Dual Wield and Deception all fire constantly
        /// during normal play - Chris's combat log is full of them - but none of them trained
        /// itself, because upstream only ever hooked the weapon skill and the defense skill.
        ///
        /// Called once per landed attack with the resolved <see cref="DamageEvent"/>, so each skill
        /// is credited only when its effect actually applied, not merely when it was evaluated.
        ///
        /// Difficulty is the target's effective defense throughout - external to every skill being
        /// raised, per the anti-runaway rule.
        /// </summary>
        public void AwardCombatSpecialtyUse(DamageEvent damageEvent, WorldObject target)
        {
            if (damageEvent == null || target == null)
                return;

            if (!PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var difficulty = GetTargetEffectiveDefenseSkill(target);

            if (difficulty == 0)
                return;

            // Recklessness applied to this hit - the mod is left at 1.0 when it did not fire, and is
            // also reset to 1.0 on criticals (Recklessness deliberately does not apply to those).
            if (damageEvent.RecklessnessMod != 1.0f && damageEvent.RecklessnessMod != 0.0f)
                TryAwardSpecialty(Skill.Recklessness, difficulty);

            if (damageEvent.SneakAttackMod != 1.0f && damageEvent.SneakAttackMod != 0.0f)
            {
                TryAwardSpecialty(Skill.SneakAttack, difficulty);

                // Deception shares the sneak-attack event rather than getting its own hook: it is
                // what grants the chance to sneak attack from the FRONT, and its Current value scales
                // that chance (Creature_Combat.cs). A landed sneak attack is therefore a genuine use
                // of Deception, and it has no other in-combat expression to hook.
                TryAwardSpecialty(Skill.Deception, difficulty);
            }

            // Dual Wield trains when actually fighting with two weapons. GetCurrentWeaponSkill()
            // already reports DualWield in that case (Player_Combat.cs), so the weapon-skill hook
            // covers the attack itself - this credits the dual-wield skill as well.
            if (IsDualWieldAttack)
                TryAwardSpecialty(Skill.DualWield, difficulty);
        }

        /// <summary>
        /// Shadowgain 007: Dirty Fighting, called from Creature_Combat.FightDirty once its proc roll
        /// has actually succeeded - so a failed roll trains nothing.
        /// </summary>
        public void AwardDirtyFightingUse(WorldObject target)
        {
            if (target == null || !PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var difficulty = GetTargetEffectiveDefenseSkill(target);

            if (difficulty > 0)
                TryAwardSpecialty(Skill.DirtyFighting, difficulty);
        }

        /// <summary>
        /// Shadowgain 007: Arcane Lore, from successfully activating a magic item.
        ///
        /// On its OWN multiplier, deliberately slow. Arcane Lore's Current gates item activation
        /// (WorldObject_Use), so if it outgrows the character it unlocks item effects far too early -
        /// the one skill here where over-fast growth actively breaks progression rather than just
        /// being generous. Chris's target: not maxed before ~level 40-50, few items still a
        /// challenge by ~80-90.
        /// </summary>
        public void AwardArcaneLoreUse(uint itemDifficulty)
        {
            if (itemDifficulty == 0 || !PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(Skill.ArcaneLore);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            var mult = PropertyManager.GetDouble("arcane_lore_gain_multiplier").Item;

            if (mult <= 0.0)
                return;

            var difficulty = (uint)System.Math.Max(1, System.Math.Round(itemDifficulty * mult));

            Proficiency.OnSuccessUse(this, skill, difficulty);
        }

        /// <summary>
        /// Shadowgain 007: Assess Creature / Assess Person, from a successful appraisal.
        ///
        /// Difficulty is the target's Deception - which is exactly what the appraisal roll is made
        /// against - so it is external to the assessing skill and scales with how hard the target
        /// was to read. These are not idle skills: a target's Assess Person reduces incoming
        /// sneak-attack damage from the front (Creature_Combat.cs), so they defend against the
        /// specialties hooked above.
        /// </summary>
        public void AwardAssessUse(Skill assessSkill, uint targetDeception)
        {
            if (!PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(assessSkill);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            // an undeceptive target still teaches something, so floor rather than skip
            var difficulty = System.Math.Max(1u, targetDeception);

            Proficiency.OnSuccessUse(this, skill, difficulty);
        }

        /// <summary>
        /// Shadowgain 007: Loyalty, from experience passed UP to your patron.
        ///
        /// Loyalty is a vassal's skill, so the trigger is the vassal's own passup - the more you
        /// contribute to your patron, the more loyal you demonstrably are. Difficulty is the XP
        /// passed up (divided down: XP runs in the thousands while every other difficulty in this
        /// system is a skill value in the tens - the same scale mismatch that made burden pay ten
        /// ranks a tick in 009).
        ///
        /// Tenure bonus rewards genuine long-term vassalage rather than allegiance-hopping: gain is
        /// multiplied by how long you have been sworn to your CURRENT patron
        /// (AllegianceSwearTimestamp, which resets when you re-swear). Capped so it cannot run away.
        ///
        /// UNTUNED - the knobs exist precisely because calibrating this needs far more play than we
        /// can generate solo. Defaults are a starting point, not a balance target.
        /// </summary>
        public void AwardLoyaltyUse(long xpPassedUp)
        {
            if (xpPassedUp <= 0 || !PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(Skill.Loyalty);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            var divisor = System.Math.Max(1.0, PropertyManager.GetDouble("loyalty_gain_xp_divisor").Item);

            var difficulty = xpPassedUp / divisor;

            // tenure: how long under the CURRENT patron, in days
            var swornAt = GetProperty(ACE.Entity.Enum.Properties.PropertyFloat.AllegianceSwearTimestamp) ?? 0;

            if (swornAt > 0)
            {
                var days = (Time.GetUnixTime() - swornAt) / 86400.0;

                if (days > 0)
                {
                    var perDay = PropertyManager.GetDouble("loyalty_tenure_bonus_per_day").Item;
                    var cap = System.Math.Max(1.0, PropertyManager.GetDouble("loyalty_tenure_bonus_cap").Item);

                    difficulty *= System.Math.Min(1.0 + days * perDay, cap);
                }
            }

            if (double.IsNaN(difficulty) || difficulty < 0)
                return;

            var award = (uint)System.Math.Min(uint.MaxValue, System.Math.Max(1, System.Math.Round(difficulty)));

            Proficiency.OnSuccessUse(this, skill, award);
        }

        /// <summary>
        /// Shadowgain 007: Leadership, the mirror of Loyalty - earned by leading, not by following.
        ///
        /// Fires when you earn experience while fellowed with at least one of your OWN vassals.
        /// Simply having vassals is not leadership; adventuring alongside them is. Difficulty is the
        /// XP earned, divided down for the same scale reason as Loyalty, and scaled by how many of
        /// your vassals are actually present.
        ///
        /// UNTUNED - same caveat as Loyalty.
        /// </summary>
        public void AwardLeadershipUse(long xpEarned)
        {
            if (xpEarned <= 0 || !PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            if (Fellowship == null || AllegianceNode == null || !AllegianceNode.HasVassals)
                return;

            var skill = GetCreatureSkill(Skill.Leadership);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            // count how many of my own vassals are in the fellowship with me
            var vassalsPresent = 0;

            foreach (var member in Fellowship.GetFellowshipMembers().Values)
            {
                if (member != null && member != this && AllegianceNode.Vassals.ContainsKey(member.Guid.Full))
                    vassalsPresent++;
            }

            if (vassalsPresent == 0)
                return;

            var divisor = System.Math.Max(1.0, PropertyManager.GetDouble("leadership_gain_xp_divisor").Item);

            var difficulty = (xpEarned / divisor) * vassalsPresent;

            if (double.IsNaN(difficulty) || difficulty < 0)
                return;

            var award = (uint)System.Math.Min(uint.MaxValue, System.Math.Max(1, System.Math.Round(difficulty)));

            Proficiency.OnSuccessUse(this, skill, award);
        }

        /// <summary>
        /// Shadowgain 007: award a specialty only if the player actually has it trained.
        /// Proficiency enforces this too, but checking here keeps the debug log free of a
        /// BLOCKED=untrained line on every single swing for skills most characters never train.
        /// </summary>
        private void TryAwardSpecialty(Skill skill, uint difficulty)
        {
            var creatureSkill = GetCreatureSkill(skill);

            if (creatureSkill == null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            Proficiency.OnSuccessUse(this, creatureSkill, difficulty);
        }
    }
}
