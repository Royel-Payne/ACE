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
