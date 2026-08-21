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

            // Shadowgain 119: PvP gate. Every specialty below takes the TARGET's defence as its
            // difficulty, so without this they trained off another player exactly as the weapon skill
            // did - and Recklessness/SneakAttack/Deception all fire on a landed hit.
            if (!Proficiency.AllowsUsageGain(this, target))
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

            // Shadowgain 119: PvP gate, as AwardCombatSpecialtyUse.
            if (!Proficiency.AllowsUsageGain(this, target))
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
        /// Shadowgain 007: Summoning, from actually putting a pet into the world.
        ///
        /// Called from PetDevice.ActOnUse once a summon has succeeded and a charge is spent, so a
        /// spent device or a refused click teaches nothing. Difficulty is the device's own skill
        /// requirement, external to Summoning.
        ///
        /// This is the SMALLER of the two Summoning paths - see <see cref="AwardSummoningFromPet"/>.
        /// </summary>
        public void AwardSummoningUse(uint deviceRequirement)
        {
            if (deviceRequirement == 0 || !PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(Skill.Summoning);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            var mult = PropertyManager.GetDouble("summoning_gain_summon_multiplier").Item;

            if (mult <= 0.0)
                return;

            var difficulty = (uint)System.Math.Max(1, System.Math.Round(deviceRequirement * mult));

            Proficiency.OnSuccessUse(this, skill, difficulty);
            // Shadowgain 172: Summoning is Endurance-primary / Self-secondary in the dat and paid
            // NO attribute at all - the same gap the defence skills had, because the attribute
            // hook lives only on the attack path. Chris asked for it directly: could Summoning be
            // used to raise Endurance.
            //
            // FULL WEIGHT, unlike the evade path. The difference is frequency: evades fire 154,437
            // times per 9h and needed damping, this fires 3,567 times per 7h across 6 characters,
            // so it is wired the way every other attribute-awarding skill already is.
            var summoningWeight = PropertyManager.GetDouble("summoning_attribute_weight").Item;

            if (summoningWeight > 0.0)
                AwardAttributesForSkill(Skill.Summoning, difficulty, summoningWeight);
        }

        /// <summary>
        /// Shadowgain 007: Summoning, from the pet's own share of a kill.
        ///
        /// The primary path. Device activation alone could never bridge the gap between the entry
        /// essence (Summoning 50) and the next tier (220) - there is nothing in between, and only a
        /// handful of charges per device.
        ///
        /// Called from Creature_Death.OnDeath_GrantXP on the PET's damage history entry, so the
        /// award is already proportional to how much of the kill the summon did. A pet that never
        /// engages earns nothing.
        ///
        /// Difficulty is that XP share divided down - kill XP runs in the thousands while every
        /// other difficulty here is a skill value in the tens, the same scale mismatch that made
        /// burden pay ten ranks a tick in 009.
        ///
        /// UNTUNED. summoning_gain_xp_divisor is the dial, and the concrete test is how many kills
        /// 50 -> 220 actually takes.
        /// </summary>
        public void AwardSummoningFromPet(double petXpShare)
        {
            if (petXpShare <= 0 || !PropertyManager.GetBool("specialty_gain_from_pet_kills").Item)
                return;

            if (!PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(Skill.Summoning);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            var divisor = System.Math.Max(1.0, PropertyManager.GetDouble("summoning_gain_xp_divisor").Item);

            var difficulty = petXpShare / divisor;

            if (double.IsNaN(difficulty) || difficulty < 1.0)
                return;

            var award = (uint)System.Math.Min(uint.MaxValue, System.Math.Round(difficulty));

            // Shadowgain 119: NOT bounded against Summoning's Base. The 119 bound assumes difficulty
            // and Base are the same kind of number - a creature's skill value - and here it is a share
            // of kill XP divided by summoning_gain_xp_divisor, which the skill's Base says nothing
            // about. Bounding it would be an unrelated balance change smuggled in behind an exploit
            // fix. Measured on LIVE 2026-08-13: this path runs at ratio 12-45, entirely because of
            // that unit mismatch, and it is dial-limited already.
            Proficiency.OnSuccessUse(this, skill, award, boundDifficulty: false);
            // Shadowgain 172: Summoning is Endurance-primary / Self-secondary in the dat and paid
            // NO attribute at all - the same gap the defence skills had, because the attribute
            // hook lives only on the attack path. Chris asked for it directly: could Summoning be
            // used to raise Endurance.
            //
            // FULL WEIGHT, unlike the evade path. The difference is frequency: evades fire 154,437
            // times per 9h and needed damping, this fires 3,567 times per 7h across 6 characters,
            // so it is wired the way every other attribute-awarding skill already is.
            //
            // The SKILL award above skips the 119 bound because a share of kill XP is not a skill
            // value. The ATTRIBUTE award does NOT skip it - AwardAttributeUsageXP bounds difficulty
            // at 3x Endurance Base, turning Adramelech's average 9,895 into 570. That clamp is what
            // stops the unit mismatch becoming a runaway on this path.
            var summoningWeight = PropertyManager.GetDouble("summoning_attribute_weight").Item;

            if (summoningWeight > 0.0)
                AwardAttributesForSkill(Skill.Summoning, award, summoningWeight);
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
        /// <remarks>
        /// Shadowgain 122: DELIBERATELY NOT PvP-GATED, having been gated in 119 and reverted within
        /// the hour on LIVE.
        ///
        /// Assess Person's ONLY award path is appraising another Player - that is what the skill IS.
        /// So gating "the other party is a Player" did not slow the skill down, it deleted it: the
        /// skill became untrainable by any means except appraising yourself.
        ///
        /// 119 gated it because it showed the shard's most extreme difficulty/base ratio, 123x. That
        /// observation was real but the gate was the wrong instrument, and redundant: the 119
        /// difficulty bound ALREADY caps that exact case, since a low-skill alt appraising a
        /// developed main now takes its difficulty at base x K rather than the main's full Deception.
        ///
        /// What remains is that appraisal can be repeated at will for a fresh award - which is not a
        /// PvP problem at all. It is the SAME missing repeat-use cooldown that makes Arcane Lore
        /// farmable (118 P2), and it belongs to that decision, once, for every appraisal skill.
        /// </remarks>
        public void AwardAssessUse(Skill assessSkill, uint targetDeception)
        {
            if (!PropertyManager.GetBool("specialty_gain_from_use").Item)
                return;

            var skill = GetCreatureSkill(assessSkill);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return;

            // an undeceptive target still teaches something, so floor rather than skip
            var difficulty = System.Math.Max(1u, targetDeception);

            // No opponent passed - see the remarks above. The 119 difficulty bound is what protects
            // this path; the PvP gate is not, and cost it its entire existence.
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

            // Shadowgain 111: A MONARCH PASSES NOTHING UP, so a monarch earns no Loyalty.
            //
            // The caller guards on HasAllegiance, which is true for everyone IN an allegiance -
            // including the monarch at the top, who has no patron and is loyal to nobody. So a
            // monarch was paid Loyalty on their OWN earnings, at full rate, forever.
            //
            // This is why Loyalty was the first skill on the shard to reach the old ceiling, and
            // why it read as a runaway passive: for the monarch it was not scaled by anything they
            // passed up, because they passed up nothing - it was a second copy of every point of
            // experience they earned. 109 treated the symptom twice (the divisor, then the
            // ceiling) before anyone checked WHO was being paid.
            //
            // Guarded here rather than at the call site so it holds for any future caller: "no
            // patron, no loyalty" is what the skill MEANS, not a property of one code path.
            if (PatronId == null)
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

            // Shadowgain 119: NOT bounded - difficulty here is passed-up XP over
            // loyalty_gain_xp_divisor, not a skill value. See AwardSummoningFromPet.
            Proficiency.OnSuccessUse(this, skill, award, boundDifficulty: false);
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

            // Shadowgain 119: NOT bounded - difficulty here is earned XP over
            // leadership_gain_xp_divisor, not a skill value. See AwardSummoningFromPet.
            Proficiency.OnSuccessUse(this, skill, award, boundDifficulty: false);
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

            // Shadowgain 187b (#1, RESCOPED AND NOW SHIPPING ON): restore the Coordination that #2
            // redistributed away from dual-wielders. DELIBERATELY LIMITED TO DualWield.
            //
            // Originally this covered all five combat specialties, on the reasoning that they pay skill
            // XP and no attribute XP - the gap 172 closed for Summoning. THAT REASONING WAS WRONG HERE,
            // and the TEST A/B is what showed it: DualWield awards matched HeavyWeapons awards
            // minute-for-minute, exactly, proving they are the SAME SWING. Summoning was a player's only
            // attribute source for that activity; these specialties ride on a swing whose weapon skill
            // already pays. Paying all five would not close a gap, it would multiply - and it would land
            // on every melee build via Recklessness and DirtyFighting, not on the dual-wielders this is
            // meant to help. So the other four stay unpaid, on purpose.
            //
            // WHY DualWield ALONE NEEDS IT. #2 moved off-hand credit to the weapon skill, which is
            // correct - but DualWield is Coordination-PRIMARY while every weapon skill pays Coordination
            // only as SECONDARY (Heavy/Light are Strength-primary, Finesse is Quickness-primary; measured
            // from TEST logs 2026-08-21, primary mult 0.900, secondary 0.225). So #2 hands dual-wielders
            // a large Strength/Quickness gain and takes Coordination away as a side effect. Left alone
            // that is a NEW complaint from the same players who reported the original bug.
            //
            // THE WEIGHT IS CALIBRATED, NOT GUESSED. With off-hand share f, #2's Coordination deficit is
            // f * (0.9 - 0.225) per swing, while this pays 0.9 * weight on EVERY dual-wield swing, so
            // weight = 0.75 * f. Measured on TEST with the dial off: 48 main-hand vs 33 off-hand swings,
            // f = 0.407, giving weight = 0.305. Cross-checked against the raw counts: old Coordination
            // 48*0.225 + 33*0.9 = 40.5; with #2 alone 81*0.225 = 18.2; deficit 22.3; restored by
            // 81*0.9*w -> w = 0.306. Hence the 0.30 default. An earlier 0.25 came from a GUESSED 35%
            // share and is superseded.
            //
            // It also removes a cadence dependency rather than reproducing one. Under the old code only
            // players who actually landed off-hand swings earned this Coordination, so it varied with
            // click rate and the repeat-attacks setting (see #3). Paying it per dual-wield swing gives
            // every dual-wielder the same rate, which is the same thing #2 did for weapon skills.
            //
            // STILL SEPARATE FROM #2: this adds a CALLER to AwardAttributesForSkill and changes nothing
            // inside it, so every existing caller - including #2's - is untouched by construction.
            if (skill != Skill.DualWield)
                return;

            if (!PropertyManager.GetBool("dualwield_specialty_attribute_gain_enabled").Item)
                return;

            var weight = PropertyManager.GetDouble("dualwield_specialty_attribute_weight").Item;

            if (double.IsNaN(weight) || weight <= 0.0)
                return;

            AwardAttributesForSkill(skill, difficulty, weight);
        }
    }
}
