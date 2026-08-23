using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Shadowgain 209 [EXPERIMENTAL, dial-gated OFF]: move an armour Attribute Set from a donor piece
    /// onto another piece of the SAME COVERAGE, replacing the target's set.
    ///
    /// Borrows Tailoring's two-step shape rather than inventing one: use the carving tool on the DONOR to
    /// pull its set into an applicator, then use the applicator on the piece you actually want to keep.
    /// Tailoring is non-reversible and requires coverage to match, and so is this.
    ///
    /// WHY THIS IS A HANDLER AND NOT A RECIPE. The transfer itself is data-shaped - a set is one
    /// PropertyInt (EquipmentSetId) and RecipeManager already implements CopyFromSourceToTarget. Two
    /// things stop it being data:
    ///   1. cook_book keys recipes by an explicit (source wcid, target wcid) PAIR - 44,755 rows today -
    ///      so any-armour-onto-any-armour is combinatorial in data.
    ///   2. Recipe requirements are verified per SIDE against fixed values (VerifyRequirements runs
    ///      separately for Source, Target and Player), so "the two pieces must match each other" cannot
    ///      be expressed at all.
    ///
    /// THE RISK MODEL, which is the whole balance of the feature: the skill check is on EXTRACTION only.
    /// Fail it and the DONOR is destroyed with no applicator - that lost donor is the entire gamble.
    /// Application is then guaranteed, so the piece you care about is never at risk.
    /// </summary>
    public static class ArmorSetTransfer
    {
        // Intricate Carving Tool - the armour-tinkering carving tool Tailoring already uses.
        private const uint ExtractionToolWcid = 9295;

        // Armor Tailoring Kit, restamped as the applicator. Tailoring builds its applicators dynamically
        // too; there is no static applicator weenie to borrow.
        private const uint ApplicatorWcid = 41956;

        // Fitted to the target curve against ACE's logistic 1/(1+exp(-factor*(skill-difficulty))).
        // 224/0.019 lands within 2.4 points at every stated point. A symmetric logistic CANNOT give both
        // 99% at 400 and 20% at 150; the flatter fit was chosen knowing 400 reads 96.6%, and the curve
        // keeps climbing above that - 425 is 97.9%, 450 is 98.7%, 99% arrives near 475.
        private const int SkillDifficulty = 224;
        private const float SkillFactor = 0.019f;

        public static bool TryHandle(Player player, WorldObject source, WorldObject target)
        {
            if (!PropertyManager.GetBool("armor_set_transfer_enabled").Item)
                return false;

            if (source.WeenieClassId == ExtractionToolWcid && target.EquipmentSetId != null)
                return Extract(player, target);

            if (source.WeenieClassId == ApplicatorWcid && source.EquipmentSetId != null)
                return Apply(player, source, target);

            return false;
        }

        private static bool Extract(Player player, WorldObject donor)
        {
            if (donor.ValidLocations == null || donor.ValidLocations == 0)
                return Fail(player, "That has no armour coverage to match against.");

            var skill = player.GetCreatureSkill(Skill.ArmorTinkering);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return Fail(player, "You must be trained in Armor Tinkering to draw a set from armour.");

            // Current, not Base - buffs and gear count, which is what makes the number reachable.
            var chance = SkillCheck.GetSkillChance((int)skill.Current, SkillDifficulty, SkillFactor);

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
            {
                Tell(player, "You fail to draw the set, and the armour is destroyed. (" + (chance * 100).ToString("N1") + "% chance)");
                player.TryConsumeFromInventoryWithNetworking(donor, 1);
                player.SendUseDoneEvent();
                return true;
            }

            var applicator = WorldObjectFactory.CreateNewWorldObject(ApplicatorWcid);

            if (applicator == null)
                return Fail(player, "Something went wrong creating the applicator.");

            // The applicator carries BOTH halves of the contract: which set, and what coverage it may be
            // applied to. Keeping the coverage on the item is what lets the second step be a pure check.
            applicator.EquipmentSetId = donor.EquipmentSetId;
            applicator.SetProperty(PropertyInt.ValidLocations, (int)donor.ValidLocations.Value);
            applicator.SetProperty(PropertyInt.WieldDifficulty, donor.GetProperty(PropertyInt.WieldDifficulty) ?? 0);
            applicator.Name = "Set Applicator (" + donor.EquipmentSetId + ")";

            player.TryConsumeFromInventoryWithNetworking(donor, 1);

            if (!player.TryCreateInInventoryWithNetworking(applicator))
                return Fail(player, "You have no room for the applicator.");

            Tell(player, "You draw the " + donor.EquipmentSetId + " set into an applicator. (" + (chance * 100).ToString("N1") + "% chance)");
            player.SendUseDoneEvent();
            return true;
        }

        private static bool Apply(Player player, WorldObject applicator, WorldObject target)
        {
            // Every guard below rejects rather than gambles - the player already paid the risk on
            // extraction, so nothing here may destroy anything.
            if (target.EquipmentSetId == null)
                return Fail(player, "That has no set to replace. A set can only be moved onto armour that already has one.");

            if (target.ValidLocations == null || applicator.ValidLocations == null || target.ValidLocations != applicator.ValidLocations)
                return Fail(player, "That does not cover the same area as the armour this set came from.");

            // Direction guard: a set may move to equally- or harder-to-wield armour, never down onto an
            // easier piece.
            var sourceReq = applicator.GetProperty(PropertyInt.WieldDifficulty) ?? 0;
            var targetReq = target.GetProperty(PropertyInt.WieldDifficulty) ?? 0;

            if (targetReq < sourceReq)
                return Fail(player, "That is easier to wield than the armour this set came from.");

            var previous = target.EquipmentSetId;

            target.EquipmentSetId = applicator.EquipmentSetId;
            target.ChangesDetected = true;

            player.TryConsumeFromInventoryWithNetworking(applicator, 1);

            Tell(player, "It now belongs to the " + target.EquipmentSetId + " set, replacing " + previous + ".");
            player.SendUseDoneEvent();
            return true;
        }

        private static void Tell(Player player, string message)
        {
            player.Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Craft));
        }

        private static bool Fail(Player player, string message)
        {
            Tell(player, message);
            player.SendUseDoneEvent();
            return true;
        }
    }
}
