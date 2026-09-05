using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Shadowgain 224: mule mode, the one-way combat brick.
        ///
        /// A character volunteers for this at The Muleskinner, behind a hard confirm, and it is
        /// PERMANENT - the same ratchet shape as ShadowgainForfeitedMarker. While set:
        ///   - CanDamage returns false, which refuses melee, missile, offensive spell targeting
        ///     and spell projectiles (gem-launched included) at their shared chokepoint
        ///   - the spellbook refuses War, Void and Life in both CreatePlayerSpell overloads;
        ///     item casts are deliberately left alone
        /// Creature/Item magic, defenses and every non-combat skill stay live - the mule must be
        /// able to buff itself and survive being jumped, just never to fight back.
        /// </summary>
        public bool ShadowgainMuleMode
        {
            get => GetProperty(PropertyBool.ShadowgainMuleMode) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ShadowgainMuleMode); else SetProperty(PropertyBool.ShadowgainMuleMode, value); }
        }

        /// <summary>
        /// Raised by Creature.ActOnUse when the used NPC carries ShadowgainMuleTrainer.
        /// The confirm dialog is the entire interaction - no item changes hands and nothing
        /// costs anything; the permanent combat brick is the price.
        /// </summary>
        public void HandleMuleTrainerUse(Creature trainer)
        {
            if (ShadowgainMuleMode)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{trainer.Name} tells you, \"Nothing left to teach. You're as fine a mule as I've ever trained.\"", ChatMessageType.Tell));
                return;
            }

            var warning = "Becoming a mule is PERMANENT and can never be undone. This character will never again be able to attack or to cast War, Void or Life magic. " +
                          "In exchange, at no cost: Strength raised to its maximum of 290, an eighth pack slot, greatly increased carrying capacity, fewer items dropped on death, " +
                          "and Creature and Item magic usable without foci. Are you absolutely sure?";

            if (!ConfirmationManager.EnqueueSend(new Confirmation_Custom(Guid, ApplyMuleConversion), warning))
                SendWeenieError(WeenieError.ConfirmationInProgress);
        }

        /// <summary>
        /// The conversion itself: pure property/attribute writes on the live player, no item, no XP.
        ///
        /// Strength goes to the fork ceiling as RANKS on the 10 innate - InitLevel is left alone
        /// because NormalizeAttributeStartingValues rewrites it to 10 every enter-world, which is
        /// exactly why the retail Reinforcement of the Lugians aug is not granted here (its +5s
        /// live in InitLevel and would be silently reverted).
        ///
        /// Benefits are applied before the brick so a failure can never leave a character bricked
        /// and unpaid. Every write only ever raises, so re-running is harmless.
        /// </summary>
        public void ApplyMuleConversion()
        {
            if (ShadowgainMuleMode)
                return;

            var maxRanks = (uint)AttributeMaxRanks();
            var strength = Attributes[PropertyAttribute.Strength];

            if (strength.Ranks < maxRanks)
            {
                var attributeXPTable = DatManager.PortalDat.XpTable.AttributeXpList;

                strength.Ranks = maxRanks;
                // the 013 stretch preserves the dat table's total cost, so its last entry is the
                // spend that legitimately buys every rank - keeps ExperienceLeft etc. honest
                strength.ExperienceSpent = attributeXPTable[attributeXPTable.Count - 1];

                Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, strength));
            }

            // vitals follow the attribute now instead of at next login (only ever raises)
            SyncVitalRanksToAttributes();

            RaiseAugmentation(PropertyInt.AugmentationExtraPackSlot, 1);
            RaiseAugmentation(PropertyInt.AugmentationLessDeathItemLoss, 3);
            RaiseAugmentation(PropertyInt.AugmentationIncreasedCarryingCapacity, 5);
            RaiseAugmentation(PropertyInt.AugmentationInfusedCreatureMagic, 1);
            RaiseAugmentation(PropertyInt.AugmentationInfusedItemMagic, 1);

            // ContainerCapacity is derived (7 + pack slot aug) at load; mirror the retail aug
            // purchase, which also sets it live and still needs a relog for the client to render
            // the new tab
            ContainerCapacity = (byte)(7 + AugmentationExtraPackSlot);
            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.ContainersCapacity, (int)ContainerCapacity));
            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.EncumbranceCapacity, GetEncumbranceCapacity()));

            ShadowgainMuleMode = true;
            ChangesDetected = true;

            Session.Network.EnqueueSend(new GameMessageSystemChat("You are now a mule, permanently. Your Strength surges to its limit and your packs feel roomier already - log out and back in to see the eighth pack slot and your updated vitals.", ChatMessageType.Broadcast));
            EnqueueBroadcast(new GameMessageSystemChat($"{Name} has given up the fighting life to become a mule!", ChatMessageType.Broadcast));

            SaveBiotaToDatabase();
        }

        /// <summary>
        /// Sets an augmentation count to at least target - never lowers one a character already
        /// bought - and tells the client.
        /// </summary>
        private void RaiseAugmentation(PropertyInt augProp, int target)
        {
            var current = GetProperty(augProp) ?? 0;

            if (current >= target)
                return;

            SetProperty(augProp, target);

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, augProp, target));
        }
    }
}
