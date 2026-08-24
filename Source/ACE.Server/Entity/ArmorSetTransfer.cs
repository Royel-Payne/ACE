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
        // Shadowgain 209b: a DEDICATED tool, not the shared Intricate Carving Tool.
        //
        // The first TEST build used 9295 and could not be invoked at all: the retail client runs
        // ItemHolder::TargetCompatibleWithObject before it will even SEND a use request, ANDing the
        // tool's TargetType against the target's ItemType. 9295 carries TargetType 128 (Misc) for its
        // claw and tooth recipes; armour is ItemType.Armor (2); 128 & 2 == 0, so the server never saw
        // the attempt and no server-side hook could ever have fixed it.
        //
        // Widening 9295 in the world database worked but was wrong: shared item, and the change
        // persists whether the dial is on or off. A dedicated weenie keeps the whole feature behind
        // its dial. Priced at 50 to match the Armor Reduction Tools, which is the existing precedent
        // for a tailoring-counter tool.
        private const uint ExtractionToolWcid = 900209;

        // Shadowgain 213: the TIER 2 extractor. A separate weenie, not a mode of the 209 tool, because the
        // tool is the only thing that can carry the tier: by the time Extract runs, the donor and the
        // player look identical either way. Ships via worlddb/005 from the first commit - 209's was typed
        // in by hand on TEST and came within one step of deploying to LIVE as a dead feature.
        private const uint Tier2ExtractionToolWcid = 900213;



        // Fitted to the target curve against ACE's logistic 1/(1+exp(-factor*(skill-difficulty))).
        // 224/0.019 lands within 2.4 points at every stated point. A symmetric logistic CANNOT give both
        // 99% at 400 and 20% at 150; the flatter fit was chosen knowing 400 reads 96.6%, and the curve
        // keeps climbing above that - 425 is 97.9%, 450 is 98.7%, 99% arrives near 475.
        private const int SkillDifficulty = 224;
        private const float SkillFactor = 0.019f;

        public static bool TryHandle(Player player, WorldObject source, WorldObject target, bool confirmed = false)
        {
            var tier1 = PropertyManager.GetBool("armor_set_transfer_enabled").Item;
            var tier2 = PropertyManager.GetBool("armor_set_transfer_t2_enabled").Item;

            if (!tier1 && !tier2)
                return false;

            if (tier1 && source.WeenieClassId == ExtractionToolWcid && target.EquipmentSetId != null)
                return Extract(player, source, target, confirmed, false);

            // Shadowgain 213: same handler, coverage guard relaxed. The TOOL decides the tier.
            if (tier2 && source.WeenieClassId == Tier2ExtractionToolWcid && target.EquipmentSetId != null)
                return Extract(player, source, target, confirmed, true);

            // An applicator is a set-carrying item with NO coverage of its own. Armour always has
            // ValidLocations; a genuine tailoring applicator never has EquipmentSetId. Nothing else
            // can satisfy both halves, so this cannot collide with either.
            if (source.EquipmentSetId != null && source.ValidLocations == null)
                return Apply(player, source, target, confirmed);

            return false;
        }

        private static bool Extract(Player player, WorldObject tool, WorldObject donor, bool confirmed, bool anyCoverage)
        {
            if (donor.ValidLocations == null || donor.ValidLocations == 0)
                return Fail(player, "That has no armour coverage to match against.");

            var skill = player.GetCreatureSkill(Skill.ArmorTinkering);

            if (skill == null || skill.AdvancementClass < SkillAdvancementClass.Trained)
                return Fail(player, "You must be trained in Armor Tinkering to draw a set from armour.");

            // Current, not Base - buffs and gear count, which is what makes the number reachable.
            //
            // Shadowgain 213: tier 2 is priced as a REAL IMBUE rather than on 209's bespoke curve, so it
            // inherits the imbue economics the game already has instead of inventing parallel ones. This
            // reproduces RecipeManager.CheckRecipeSuccess exactly:
            //
            //     chance = GetSkillChance(skill.Current + LumAugSkilledCraft, difficulty) / 3
            //     chance += AugmentationBonusImbueChance * 0.05      // Charmed Smith, flat, skill-independent
            //
            // The /3 is where the famous "33%" comes from - it is a CEILING reached when raw skill chance
            // is ~100%, not a constant, so one difficulty produces both the qualifying wall and the roll.
            // It is inlined rather than called because CheckRecipeSuccess is private and needs a Recipe;
            // no recipe row can express "create an applicator carrying the donor's set" (see the header).
            double chance;

            if (anyCoverage)
            {
                var difficulty = (int)PropertyManager.GetLong("armor_set_transfer_t2_difficulty").Item;
                var effective = (int)skill.Current + player.LumAugSkilledCraft;

                chance = SkillCheck.GetSkillChance(effective, difficulty) / 3.0;

                if (player.AugmentationBonusImbueChance > 0)
                    chance += player.AugmentationBonusImbueChance * 0.05;

                chance = System.Math.Min(1.0, System.Math.Max(0.0, chance));
            }
            else
            {
                chance = SkillCheck.GetSkillChance((int)skill.Current, SkillDifficulty, SkillFactor);
            }

            // Shadowgain 209c: honour the stock 'Use Crafting Chance of Success Dialog' option.
            //
            // This matters more here than it does for ordinary crafting. A normal failed recipe wastes
            // a component; a failed extraction destroys the DONOR, which is a piece of set armour the
            // player went and earned. Seeing the number before committing is the difference between a
            // gamble and an ambush.
            //
            // Uses Confirmation_ArmorSetTransfer, NOT the stock Confirmation_ArmorSetTransfer: that
            // one's decline branch sends YouChickenOut and no UseDone, which left the client stuck on
            // the hourglass whenever this feature was cancelled. Ours releases the client on BOTH
            // branches, and calls the handler directly on accept.
            if (!confirmed && player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog))
            {
                var msg = "You determine that you have a " + (int)System.Math.Round(chance * 100)
                    + " percent chance to succeed.\n\nOn failure the " + donor.Name + " is DESTROYED.";

                // Stock ACE sends UseDone only when the enqueue FAILS - on success it stays silent so
                // the client waits on the dialog. The first build sent it on success, which left the
                // client's use-state machine hanging: the hourglass never cleared and nothing else
                // could be activated until relog.
                if (!player.ConfirmationManager.EnqueueSend(
                        new Confirmation_ArmorSetTransfer(player.Guid, tool.Guid, donor.Guid), msg))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                    return true;
                }

                return true;
            }

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
            {
                Tell(player, "You fail to draw the set, and the armour is destroyed. (" + (chance * 100).ToString("N1") + "% chance)");
                player.TryConsumeFromInventoryWithNetworking(donor, 1);
                player.SendUseDoneEvent();
                return true;
            }

            // Reuse TAILORING'S OWN per-coverage applicator weenie. Those already carry the green arrow
            // overlay, and SetArmorProperties copies the donor's icon, palette and setup onto it - so it
            // reads exactly like a tailoring applicator, which is the look Chris asked for. It also sets
            // TargetType = Armor|Clothing IN CODE, which is what lets the client send step 2 at all.
            var applicatorWcid = Tailoring.GetArmorWCID(donor.ValidLocations.Value);

            if (applicatorWcid == null)
                return Fail(player, "That armour covers an area no applicator exists for.");

            var applicator = WorldObjectFactory.CreateNewWorldObject(applicatorWcid.Value);

            if (applicator == null)
                return Fail(player, "Something went wrong creating the applicator.");

            Tailoring.SetArmorProperties(donor, applicator);

            // Then stamp OUR payload over it. The applicator carries both halves of the contract - which
            // set, and what coverage it may be applied to - so step 2 is a pure check with nothing to
            // look up. EquipmentSetId is also what distinguishes this from a real tailoring applicator.
            applicator.EquipmentSetId = donor.EquipmentSetId;
            // NO ValidLocations. The first build stamped the donor's coverage here to carry it to step
            // 2, which made the applicator WEARABLE - it equipped as gloves instead of acting as a
            // crafting step. The coverage never needed storing: GetArmorWCID chose this applicator's
            // weenie FROM the coverage, so the wcid already encodes it and step 2 recovers it by asking
            // the same question of the target.
            applicator.RemoveProperty(PropertyInt.ValidLocations);
            applicator.SetProperty(PropertyInt.WieldDifficulty, donor.GetProperty(PropertyInt.WieldDifficulty) ?? 0);
            applicator.Name = donor.EquipmentSetId + " Set Applicator";

            if (anyCoverage)
            {
                // Shadowgain 213: the flag is the ONLY thing separating this from a tier-1 applicator -
                // both are built from the same coverage-derived weenie. See PropertyBool 9103.
                applicator.SetProperty(PropertyBool.ShadowgainAnyCoverageApplicator, true);

                applicator.LongDesc = "Drawn from " + donor.Name + " with a tier 2 extractor. Apply this to "
                    + "ANY piece of armour to replace its attribute set with " + donor.EquipmentSetId
                    + ", regardless of what it covers. The target is never destroyed.";
            }
            else
            {
                applicator.LongDesc = "Drawn from " + donor.Name + ". Apply this to another piece of armour "
                    + "covering the same area to replace its attribute set with " + donor.EquipmentSetId
                    + ". The target is never destroyed.";
            }

            player.TryConsumeFromInventoryWithNetworking(donor, 1);

            if (!player.TryCreateInInventoryWithNetworking(applicator))
                return Fail(player, "You have no room for the applicator.");

            Tell(player, "You draw the " + donor.EquipmentSetId + " set into an applicator. (" + (chance * 100).ToString("N1") + "% chance)");
            player.SendUseDoneEvent();
            return true;
        }

        private static bool Apply(Player player, WorldObject applicator, WorldObject target, bool confirmed)
        {
            // Every guard below rejects rather than gambles - the player already paid the risk on
            // extraction, so nothing here may destroy anything.
            if (target.EquipmentSetId == null)
                return Fail(player, "That has no set to replace. A set can only be moved onto armour that already has one.");

            // Shadowgain 213: THE ONE GUARD TIER 2 DROPS. Everything else below still applies.
            //
            // Coverage match, DERIVED not stored: the applicator's weenie was chosen BY the donor's
            // coverage, so asking the same question of the target must return the same weenie. A tier-2
            // applicator carries ShadowgainAnyCoverageApplicator and skips exactly this check - which is
            // why the flag exists rather than the check simply being removed. 209 is untouched.
            var anyCoverage = applicator.GetProperty(PropertyBool.ShadowgainAnyCoverageApplicator) ?? false;

            if (target.ValidLocations == null)
                return Fail(player, "That has no armour coverage.");

            if (!anyCoverage && Tailoring.GetArmorWCID(target.ValidLocations.Value) != applicator.WeenieClassId)
                return Fail(player, "That does not cover the same area as the armour this set came from.");

            // Direction guard: a set may move to equally- or harder-to-wield armour, never down onto an
            // easier piece.
            var sourceReq = applicator.GetProperty(PropertyInt.WieldDifficulty) ?? 0;
            var targetReq = target.GetProperty(PropertyInt.WieldDifficulty) ?? 0;

            if (targetReq < sourceReq)
                return Fail(player, "That is easier to wield than the armour this set came from.");

            // Shadowgain 209f: confirm on APPLY too (Chris). This step cannot fail, but it does
            // overwrite the target's existing set irreversibly - and the applicator is spent either
            // way. Naming both sets is the point: a player who mixed up two similar gauntlets should
            // find out here, not afterwards.
            if (!confirmed && player.GetCharacterOption(CharacterOption.UseCraftingChanceOfSuccessDialog))
            {
                var applyMsg = "Replace the " + target.EquipmentSetId + " set on this armour with "
                    + applicator.EquipmentSetId + "? This cannot be undone, and the applicator is consumed.";

                if (!player.ConfirmationManager.EnqueueSend(
                        new Confirmation_ArmorSetTransfer(player.Guid, applicator.Guid, target.Guid), applyMsg))
                {
                    player.SendUseDoneEvent(WeenieError.ConfirmationInProgress);
                }

                return true;
            }

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
