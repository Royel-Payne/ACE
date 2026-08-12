using System;
using System.Linq;

using ACE.DatLoader;
using ACE.DatLoader.Entity;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects
{
    public class SkillAlterationDevice : WorldObject
    {
        public enum SkillAlterationType
        {
            Undef      = 0,
            Specialize = 1,
            Lower      = 2,
        }

        public SkillAlterationType TypeOfAlteration
        {
            get => (SkillAlterationType)(GetProperty(PropertyInt.TypeOfAlteration) ?? 0);
            set { if (value == 0) RemoveProperty(PropertyInt.TypeOfAlteration); else SetProperty(PropertyInt.TypeOfAlteration, (int)value); }
        }

        public Skill SkillToBeAltered
        {
            get => (Skill)(GetProperty(PropertyInt.SkillToBeAltered) ?? 0);
            set { if (value == 0) RemoveProperty(PropertyInt.SkillToBeAltered); else SetProperty(PropertyInt.SkillToBeAltered, (int)value); }
        }

        /// <summary>
        /// A new biota be created taking all of its values from weenie.
        /// </summary>
        public SkillAlterationDevice(Weenie weenie, ObjectGuid guid) : base(weenie, guid)
        {
            SetEphemeralValues();
        }

        /// <summary>
        /// Restore a WorldObject from the database.
        /// </summary>
        public SkillAlterationDevice(Biota biota) : base(biota)
        {
            SetEphemeralValues();
        }

        private void SetEphemeralValues()
        {
        }

        public override void ActOnUse(WorldObject activator)
        {
            ActOnUse(activator, false);
        }

        public void ActOnUse(WorldObject activator, bool confirmed)
        {
            if (!(activator is Player player))
                return;

            // verify skill
            var skill = player.GetCreatureSkill(SkillToBeAltered);

            if (skill == null)
            {
                player.Session.Network.EnqueueSend(new GameEventWeenieError(player.Session, WeenieError.YouFailToAlterSkill));
                return;
            }

            // get skill training / specialization costs
            var skillBase = DatManager.PortalDat.SkillTable.SkillBaseHash[(uint)skill.Skill];

            if (!VerifyRequirements(player, skill, skillBase))
                return;

            if (!confirmed)
            {
                var msg = "This action will ";
                switch (TypeOfAlteration)
                {
                    case SkillAlterationType.Specialize:
                        msg += $"specialize your {skill.Skill.ToSentence()} skill and cost {skillBase.UpgradeCostFromTrainedToSpecialized} credits.";
                        break;
                    case SkillAlterationType.Lower:
                        msg += $"lower your {skill.Skill.ToSentence()} skill from {(skill.AdvancementClass == SkillAdvancementClass.Specialized ? "specialized to trained" : "trained to untrained")} and refund the skill credits and experience invested in this skill.";
                        break;
                }

                if (!player.ConfirmationManager.EnqueueSend(new Confirmation_AlterSkill(player.Guid, Guid), msg))
                    player.SendWeenieError(WeenieError.ConfirmationInProgress);

                return;
            }

            AlterSkill(player, skill, skillBase);
        }

        public bool VerifyRequirements(Player player, CreatureSkill skill, SkillBase skillBase)
        {
            switch (TypeOfAlteration)
            {
                // Gem of Enlightenment
                case SkillAlterationType.Specialize:

                    // ensure skill is trained
                    if (skill.AdvancementClass != SkillAdvancementClass.Trained)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_SkillMustBeTrained, skill.Skill.ToSentence()));
                        return false;
                    }

                    // ensure player has enough available skill credits
                    if (player.AvailableSkillCredits < skillBase.UpgradeCostFromTrainedToSpecialized)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.NotEnoughSkillCreditsToSpecialize, skill.Skill.ToSentence()));
                        return false;
                    }

                    // ensure player won't exceed limit of 70 specialized credits after operation
                    //
                    // Shadowgain 090 item 6: counted against what the player PAYS - the upgrade cost -
                    // not the full SpecializedCost. In retail these were the same number: you trained
                    // a skill (paying TrainedCost) and then upgraded it (paying the difference), so
                    // your outlay was the full cost and the cap measured exactly your spending.
                    //
                    // Free training broke that alignment and nothing re-pointed the cap, so it ran
                    // ~2.5x faster than the wallet: a 2-credit specialization burned 6 of the 70.
                    // Measured against the dat, a breadth build was blocked after spending 28 of its
                    // ~50 lifetime credits, leaving 18 permanently unspendable behind a retail error
                    // message with no in-game explanation.
                    //
                    // Repointing restores retail's relationship rather than removing the guard: max
                    // lifetime spend is ~50 against a cap of 70, so it no longer binds, but it still
                    // catches any future source that pushes credits past 70.
                    //
                    // Heritage-adjusted costs are deliberately not consulted, matching the charge
                    // path - SpecializeSkill bills skillBase.UpgradeCostFromTrainedToSpecialized and
                    // no live path charges the heritage figure (same inconsistency 095c removed).
                    var specializedCost = skillBase.UpgradeCostFromTrainedToSpecialized;

                    if (GetTotalSpecializedCredits(player) + specializedCost > 70)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.TooManyCreditsInSpecializedSkills, skill.Skill.ToSentence()));
                        return false;
                    }
                    break;

                // Gem of Forgetfulness
                case SkillAlterationType.Lower:

                    // ensure skill is trained or specialized
                    if (skill.AdvancementClass < SkillAdvancementClass.Trained)
                    {
                        player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.Your_SkillIsAlreadyUntrained, skill.Skill.ToSentence()));
                        return false;
                    }

                    // Check for equipped items that have requirements in the skill we're lowering
                    if (CheckWieldedItems(player))
                    {
                        // Items are wielded which might be affected by a lowering operation
                        player.Session.Network.EnqueueSend(new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.CannotLowerSkillWhileWieldingItem, skill.Skill.ToSentence()));
                        return false;
                    }

                    break;

            }
            return true;
        }

        public void AlterSkill(Player player, CreatureSkill skill, SkillBase skillBase)
        {
            switch (TypeOfAlteration)
            {
                // Gem of Enlightenment
                case SkillAlterationType.Specialize:

                    if (player.SpecializeSkill(skill.Skill, skillBase.UpgradeCostFromTrainedToSpecialized, false))
                    {
                        var updateSkill = new GameMessagePrivateUpdateSkill(player, skill);
                        var availableSkillCredits = new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits, player.AvailableSkillCredits ?? 0);
                        var msg = new GameEventWeenieErrorWithString(player.Session, WeenieErrorWithString.YouHaveSucceededSpecializing_Skill, skill.Skill.ToSentence());

                        player.Session.Network.EnqueueSend(updateSkill, availableSkillCredits, msg);

                        player.TryConsumeFromInventoryWithNetworking(this, 1);
                    }
                    break;

                // Gem of Forgetfulness
                case SkillAlterationType.Lower:

                    // specialized => trained
                    if (skill.AdvancementClass == SkillAdvancementClass.Specialized)
                    {
                        var specializedViaAugmentation = player.IsSkillSpecializedViaAugmentation(skill.Skill, out var playerHasAugmentation) && playerHasAugmentation;

                        if (player.UnspecializeSkill(skill.Skill, skillBase.UpgradeCostFromTrainedToSpecialized))
                        {
                            var updateSkill = new GameMessagePrivateUpdateSkill(player, skill);
                            var availableSkillCredits = new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits, player.AvailableSkillCredits ?? 0);
                            var msg = specializedViaAugmentation ? WeenieErrorWithString.YouSucceededRecoveringXPFromSkill_AugmentationNotUntrainable : WeenieErrorWithString.YouHaveSucceededUnspecializing_Skill;
                            var message = new GameEventWeenieErrorWithString(player.Session, msg, skill.Skill.ToSentence());

                            player.Session.Network.EnqueueSend(updateSkill, availableSkillCredits, message);

                            player.TryConsumeFromInventoryWithNetworking(this, 1);
                        }
                    }

                    // trained => untrained
                    // in the case of skills which can't be untrained,
                    // keep trained, but recover the xp spent
                    else if (skill.AdvancementClass == SkillAdvancementClass.Trained)
                    {
                        var untrainable = Player.IsSkillUntrainable(skill.Skill);

                        // Shadowgain 095c: refund exactly what was PAID, which under
                        // all_skills_trained is zero - training was never bought with a spendable
                        // credit, so untraining cannot return one. This is 090 item 5: the refund
                        // was farmable (untrain -> +credits -> retrain free -> repeat), and Fianhe
                        // at Asheron's Castle refunds everything in one click.
                        if (player.UntrainSkill(skill.Skill, Player.GetTrainingCost(skillBase)))
                        {
                            var updateSkill = new GameMessagePrivateUpdateSkill(player, skill);
                            var availableSkillCredits = new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits, player.AvailableSkillCredits ?? 0);
                            var msg = untrainable ? WeenieErrorWithString.YouHaveSucceededUntraining_Skill : WeenieErrorWithString.CannotUntrain_SkillButRecoveredXP;
                            var message = new GameEventWeenieErrorWithString(player.Session, msg, skill.Skill.ToSentence());

                            player.Session.Network.EnqueueSend(updateSkill, availableSkillCredits, message);

                            player.TryConsumeFromInventoryWithNetworking(this, 1);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Calculates and returns the current total number of specialized credits.
        ///
        /// Shadowgain 090 item 6: counts the UPGRADE cost of each specialization - what the player
        /// actually paid - rather than the full SpecializedCost. See the matching note at the cap
        /// check above for why the two diverged and why this restores retail's behaviour.
        /// </summary>
        private int GetTotalSpecializedCredits(Player player)
        {
            var specializedCreditsTotal = 0;

            foreach (var kvp in player.Skills)
            {
                if (kvp.Value.AdvancementClass == SkillAdvancementClass.Specialized)
                {
                    switch (kvp.Key)
                    {
                        // exclude None/Undef skill
                        case Skill.None:

                        // exclude aug specs
                        case Skill.ArmorTinkering:
                        case Skill.ItemTinkering:
                        case Skill.MagicItemTinkering:
                        case Skill.WeaponTinkering:
                        case Skill.Salvaging:
                            continue;
                    }

                    var skill = DatManager.PortalDat.SkillTable.SkillBaseHash[(uint)kvp.Key];

                    // the upgrade cost is what was charged for this specialization, so it is what
                    // the cap must count - see the note at the check above
                    specializedCreditsTotal += skill.UpgradeCostFromTrainedToSpecialized;
                }
            }

            return specializedCreditsTotal;
        }

        /// <summary>
        /// Checks wielded items and their requirements to see if they'd be violated by an impending skill lowering operation
        /// </summary>
        private bool CheckWieldedItems(Player player)
        {
            foreach (var equippedItem in player.EquippedObjects.Values)
            {
                if (CheckWieldRequirement(player, equippedItem.WieldRequirements, equippedItem.WieldSkillType) ||
                    CheckWieldRequirement(player, equippedItem.WieldRequirements2, equippedItem.WieldSkillType2) ||
                    CheckWieldRequirement(player, equippedItem.WieldRequirements3, equippedItem.WieldSkillType3) ||
                    CheckWieldRequirement(player, equippedItem.WieldRequirements4, equippedItem.WieldSkillType4))
                {
                    return true;
                }
            }
            return false;
        }

        private bool CheckWieldRequirement(Player player, WieldRequirement itemWieldReq, int? wieldSkillType)
        {
            if (itemWieldReq != WieldRequirement.RawSkill && itemWieldReq != WieldRequirement.Skill)
                return false;

            return player.ConvertToMoASkill((Skill)(wieldSkillType ?? 0)) == SkillToBeAltered;
        }
    }
}
