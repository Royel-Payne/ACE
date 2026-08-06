using System;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        public bool HandleActionRaiseAttribute(PropertyAttribute attribute, uint amount)
        {
            if (!Attributes.TryGetValue(attribute, out var creatureAttribute))
            {
                log.Warn($"{Name}.HandleActionRaiseAttribute({attribute}, {amount}) - invalid attribute");
                return false;
            }

            if (amount > AvailableExperience)
            {
                log.Warn($"{Name}.HandleActionRaiseAttribute({attribute}, {amount}) - amount > AvaiableExperience ({AvailableExperience})");
                return false;
            }

            var prevRank = creatureAttribute.Ranks;

            if (!SpendAttributeXp(creatureAttribute, amount))
            {
                ChatPacket.SendServerMessage(Session, $"Your attempt to raise {attribute} has failed.", ChatMessageType.Broadcast);
                return false;
            }

            Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, creatureAttribute));

            if (prevRank != creatureAttribute.Ranks)
            {
                // checks if max rank is achieved and plays fireworks w/ special text
                var suffix = "";
                if (creatureAttribute.IsMaxRank)
                {
                    // fireworks
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    suffix = " and has reached its upper limit";
                }

                var sound = new GameMessageSound(Guid, Sound.RaiseTrait);
                var msg = new GameMessageSystemChat($"Your base {attribute} is now {creatureAttribute.Base}{suffix}!", ChatMessageType.Advancement);

                Session.Network.EnqueueSend(sound, msg);

                if (attribute == PropertyAttribute.Endurance)
                {
                    // this packet appears to trigger client to update both health and stamina
                    var updateHealth = new GameMessagePrivateUpdateVital(this, Health);

                    Session.Network.EnqueueSend(updateHealth);
                }
                else if (attribute == PropertyAttribute.Self)
                {
                    var updateMana = new GameMessagePrivateUpdateVital(this, Mana);

                    Session.Network.EnqueueSend(updateMana);
                }

                // retail was missing the 'raise attribute' runrate hook here
                if ((attribute == PropertyAttribute.Strength || attribute == PropertyAttribute.Quickness) && PropertyManager.GetBool("runrate_add_hooks").Item)
                    HandleRunRateUpdate();
            }

            return true;
        }

        private bool SpendAttributeXp(CreatureAttribute creatureAttribute, uint amount, bool sendNetworkUpdate = true)
        {
            // ensure attribute is not already max rank
            if (creatureAttribute.IsMaxRank)
            {
                log.Warn($"{Name}.SpendAttributeXp({creatureAttribute.Attribute}, {amount}) - player tried to raise attribute beyond max rank");
                return false;
            }

            // the client should already handle this naturally,
            // but ensure player can't spend xp beyond the max rank
            var amountToEnd = creatureAttribute.ExperienceLeft;

            if (amount > amountToEnd)
            {
                log.Warn($"{Name}.SpendAttributeXp({creatureAttribute.Attribute}, {amount}) - player tried to raise attribute beyond {amountToEnd} experience");
                return false;   // returning error here, instead of setting amount to amountToEnd
            }

            // everything looks good at this point,
            // spend xp on attribute
            if (!SpendXP(amount, sendNetworkUpdate))
            {
                log.Warn($"{Name}.SpendAttributeXp({creatureAttribute.Attribute}, {amount}) - SpendXP failed");
                return false;
            }

            creatureAttribute.ExperienceSpent += amount;

            // calculate new rank
            creatureAttribute.Ranks = (ushort)CalcAttributeRank(creatureAttribute.ExperienceSpent);

            return true;
        }

        /// <summary>
        /// Shadowgain 004: writes usage-based attribute XP DIRECTLY into the attribute, mirroring
        /// Player.AwardSkillUsageXP. Bypasses SpendAttributeXp/SpendXP and therefore the unassigned-XP
        /// pool and the level cap.
        ///
        /// ANTI-RUNAWAY RULE (from the 003 Shield bug): <paramref name="difficulty"/> must ALWAYS come
        /// from something external - the target, the attacker, or the magnitude of the action. It must
        /// never be derived from the attribute being raised. If it were, difficulty/current would sit at
        /// a constant 1.0 and every award would equal the whole attribute and grow as it grows.
        /// The denominator being the attribute is fine and intended: that is what makes gain
        /// self-limiting as the attribute climbs.
        ///
        /// Returns true if XP was applied.
        /// </summary>
        public bool AwardAttributeUsageXP(PropertyAttribute attribute, uint difficulty, bool isSecondary = false)
        {
            if (difficulty == 0)
                return false;

            if (!Attributes.TryGetValue(attribute, out var creatureAttribute))
                return false;

            var debug = PropertyManager.GetBool("attribute_debug_logging").Item;

            var attributeXPTable = DatManager.PortalDat.XpTable.AttributeXpList;
            var maxXP = attributeXPTable[attributeXPTable.Count - 1];

            if (creatureAttribute.ExperienceSpent >= maxXP)
            {
                if (debug)
                    log.Info($"[ATTRIBUTE] {Name} | {attribute} | NOOP=maxRank | difficulty={difficulty}");

                return false;
            }

            var floor = PropertyManager.GetDouble("attribute_gain_difficulty_floor").Item;
            var cap = PropertyManager.GetDouble("attribute_gain_difficulty_cap").Item;
            var minAward = (uint)Math.Max(0, PropertyManager.GetLong("attribute_gain_min_award").Item);

            var multiplier = PropertyManager.GetDouble("attribute_gain_multiplier").Item;

            // Greylock wanted the mental attributes slowest. Shipped at 1.0 - the dial exists,
            // the operator picks the value; we do not bake in a balance target.
            if (attribute == PropertyAttribute.Focus || attribute == PropertyAttribute.Self)
                multiplier *= PropertyManager.GetDouble("attribute_gain_mental_multiplier").Item;

            // overlapping mapping: an action feeds a primary attribute fully and a related one partially
            if (isSecondary)
                multiplier *= PropertyManager.GetDouble("attribute_gain_overlap_factor").Item;

            var current = Math.Max(1u, creatureAttribute.Current);
            var ratio = (double)difficulty / current;
            var difficultyFactor = Math.Clamp(ratio, Math.Min(floor, cap), Math.Max(floor, cap));

            var awarded = difficulty * difficultyFactor * multiplier;

            // knobs are operator-settable live; clamp before casting so a silly value cannot wrap
            if (double.IsNaN(awarded) || awarded < 0)
                awarded = 0;

            var pp = (uint)Math.Min(uint.MaxValue, Math.Max(minAward, Math.Round(awarded)));

            var prevRank = creatureAttribute.Ranks;
            var prevXP = creatureAttribute.ExperienceSpent;

            var newXP = Math.Min((ulong)creatureAttribute.ExperienceSpent + pp, maxXP);

            creatureAttribute.ExperienceSpent = (uint)newXP;
            creatureAttribute.Ranks = (ushort)Math.Max(0, CalcAttributeRank(creatureAttribute.ExperienceSpent));

            // CreatureAttribute's setters, unlike CreatureSkill's, do NOT flag the biota as dirty -
            // without this the gain is never persisted.
            ChangesDetected = true;

            if (debug)
            {
                log.Info($"[ATTRIBUTE] {Name} | {attribute} | AWARD{(isSecondary ? "=secondary" : "")}" +
                         $" | difficulty={difficulty} vs current={current} ratio={ratio:N3}" +
                         $" | factor={difficultyFactor:N3} mult={multiplier:N3}" +
                         $" | pp={pp}" +
                         $" | rank {prevRank}->{creatureAttribute.Ranks} xp {prevXP}->{creatureAttribute.ExperienceSpent}");
            }

            if (Session == null)
                return true;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, creatureAttribute));

            if (prevRank != creatureAttribute.Ranks)
            {
                var suffix = "";
                if (creatureAttribute.IsMaxRank)
                {
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    suffix = " and has reached its upper limit";
                }

                Session.Network.EnqueueSend(
                    new GameMessageSound(Guid, Sound.RaiseTrait),
                    new GameMessageSystemChat($"Your base {attribute} is now {creatureAttribute.Base}{suffix}!", ChatMessageType.Advancement));

                // Deliberately mirrors HandleActionRaiseAttribute: send the vital update so the client
                // re-reads the new maximum. NOT SetMaxVitals() - Endurance gain fires on TAKING damage,
                // so refilling vitals here would heal the player every time they were hit.
                if (attribute == PropertyAttribute.Endurance)
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(this, Health));
                else if (attribute == PropertyAttribute.Self)
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(this, Mana));

                if ((attribute == PropertyAttribute.Strength || attribute == PropertyAttribute.Quickness) && PropertyManager.GetBool("runrate_add_hooks").Item)
                    HandleRunRateUpdate();
            }

            if (prevRank != creatureAttribute.Ranks)
                SyncVitalRanksToAttributes();

            return true;
        }

        /// <summary>
        /// Shadowgain 004: keeps vital ranks in step with the attributes that govern them.
        ///
        /// A vital's maximum is <c>StartingValue + Ranks + attributeDerivedComponent</c>. Ranks were
        /// normally purchased with pooled XP, which is now disabled - and simply losing them would leave
        /// characters permanently far below what retail content difficulty is balanced against, because
        /// that content assumes players raised BOTH the attribute and the vital.
        ///
        /// So rather than being bought, ranks are earned implicitly: a vital is held at the same
        /// PROPORTION of its rank ceiling as its governing attribute is of the attribute ceiling. Max
        /// the attribute and the vital maxes at exactly the same moment. The ratio is computed from the
        /// live dat tables rather than hardcoded, so it stays correct whatever those tables contain.
        ///
        /// Only ever raises, never lowers - a character who legitimately bought ranks keeps them.
        /// </summary>
        public void SyncVitalRanksToAttributes()
        {
            if (!PropertyManager.GetBool("vital_ranks_follow_attributes").Item)
                return;

            var attributeXPTable = DatManager.PortalDat.XpTable.AttributeXpList;
            var vitalXPTable = DatManager.PortalDat.XpTable.VitalXpList;

            var attributeMaxRanks = attributeXPTable.Count - 1;
            var vitalMaxRanks = vitalXPTable.Count - 1;

            if (attributeMaxRanks <= 0 || vitalMaxRanks <= 0)
                return;

            uint RanksOf(PropertyAttribute a) => Attributes.TryGetValue(a, out var ca) ? ca.Ranks : 0;

            var endurance = RanksOf(PropertyAttribute.Endurance);

            // Health follows Endurance and Mana follows Self - the same attributes the game's own vital
            // formulas key off.
            SyncVital(PropertyAttribute2nd.MaxHealth, endurance);
            SyncVital(PropertyAttribute2nd.MaxMana, RanksOf(PropertyAttribute.Self));

            // Stamina is the one with a real choice. Retail derives it from Endurance alone, but under
            // usage-based gain Endurance only rises from BEING HIT - so a character who evades well
            // would be starved of the very resource their attacking spends fastest. Tracking the best of
            // Endurance/Strength/Coordination means any active playstyle feeds it.
            var stamina = endurance;

            if (PropertyManager.GetBool("vital_stamina_multi_source").Item)
                stamina = Math.Max(endurance, Math.Max(RanksOf(PropertyAttribute.Strength), RanksOf(PropertyAttribute.Coordination)));

            SyncVital(PropertyAttribute2nd.MaxStamina, stamina);

            void SyncVital(PropertyAttribute2nd vitalType, uint attributeRanks)
            {
                if (!Vitals.TryGetValue(vitalType, out var vital))
                    return;

                var target = (uint)Math.Round((double)attributeRanks * vitalMaxRanks / attributeMaxRanks);

                if (target > vitalMaxRanks)
                    target = (uint)vitalMaxRanks;

                if (target <= vital.Ranks)
                    return;     // never strip ranks a player already had

                vital.Ranks = target;
                vital.ExperienceSpent = vitalXPTable[(int)target];

                ChangesDetected = true;

                if (PropertyManager.GetBool("attribute_debug_logging").Item)
                    log.Info($"[ATTRIBUTE] {Name} | {vitalType} | VITAL-SYNC | attributeRanks={attributeRanks}/{attributeMaxRanks} -> vitalRanks={target}/{vitalMaxRanks} | newMax={vital.MaxValue}");

                if (Session != null)
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(this, vital));
            }
        }

        /// <summary>
        /// Shadowgain 004: maps a weapon skill to the attributes its use exercises.
        /// Difficulty is the caller's already-computed target-derived value, so it is external by
        /// construction. Primary gets the full award, the related attribute a fraction.
        /// </summary>
        public void AwardAttributesForWeaponSkill(Skill skill, uint difficulty)
        {
            switch (skill)
            {
                case Skill.HeavyWeapons:
                case Skill.TwoHandedCombat:
                    AwardAttributeUsageXP(PropertyAttribute.Strength, difficulty);
                    AwardAttributeUsageXP(PropertyAttribute.Coordination, difficulty, true);
                    break;

                case Skill.LightWeapons:
                case Skill.DualWield:
                    AwardAttributeUsageXP(PropertyAttribute.Quickness, difficulty);
                    AwardAttributeUsageXP(PropertyAttribute.Coordination, difficulty, true);
                    break;

                case Skill.FinesseWeapons:
                    AwardAttributeUsageXP(PropertyAttribute.Coordination, difficulty);
                    AwardAttributeUsageXP(PropertyAttribute.Quickness, difficulty, true);
                    break;

                case Skill.MissileWeapons:
                    AwardAttributeUsageXP(PropertyAttribute.Coordination, difficulty);
                    AwardAttributeUsageXP(PropertyAttribute.Quickness, difficulty, true);
                    break;
            }
        }

        /// <summary>
        /// Shadowgain 004: maps a magic school (or Healing) to Focus or Self.
        /// Difficulty comes from the caller - target MagicDefense or heal difficulty - never from the
        /// attribute being raised.
        /// </summary>
        /// <summary>
        /// Shadowgain 004: overload for the spell paths, which carry MagicSchool rather than Skill.
        /// War/void/enchantment are Focus; life magic is Self.
        /// </summary>
        public void AwardAttributesForMagicSkill(MagicSchool school, uint difficulty)
        {
            switch (school)
            {
                case MagicSchool.WarMagic:
                case MagicSchool.VoidMagic:
                case MagicSchool.ItemEnchantment:
                case MagicSchool.CreatureEnchantment:
                    AwardAttributeUsageXP(PropertyAttribute.Focus, difficulty);
                    break;

                case MagicSchool.LifeMagic:
                    AwardAttributeUsageXP(PropertyAttribute.Self, difficulty);
                    break;
            }
        }

        public void AwardAttributesForMagicSkill(Skill school, uint difficulty)
        {
            switch (school)
            {
                case Skill.WarMagic:
                case Skill.VoidMagic:
                case Skill.ItemEnchantment:
                case Skill.CreatureEnchantment:
                case Skill.ArcaneLore:
                    AwardAttributeUsageXP(PropertyAttribute.Focus, difficulty);
                    break;

                case Skill.LifeMagic:
                case Skill.ManaConversion:
                case Skill.Healing:
                    AwardAttributeUsageXP(PropertyAttribute.Self, difficulty);
                    break;
            }
        }

        public void SpendAllAvailableAttributeXp(CreatureAttribute creatureAttribute, bool sendNetworkUpdate = true)
        {
            var amountRemaining = creatureAttribute.ExperienceLeft;

            if (amountRemaining > AvailableExperience)
                amountRemaining = (uint)AvailableExperience;

            SpendAttributeXp(creatureAttribute, amountRemaining, sendNetworkUpdate);
        }

        /// <summary>
        /// Returns the maximum rank that can be purchased with an xp amount
        /// </summary>
        /// <param name="xpAmount">The amount of xp used to make the purchase</param>
        public static int CalcAttributeRank(uint xpAmount)
        {
            var rankXpTable = DatManager.PortalDat.XpTable.AttributeXpList;

            for (var i = rankXpTable.Count - 1; i >= 0; i--)
            {
                var rankAmount = rankXpTable[i];
                if (xpAmount >= rankAmount)
                    return i;
            }
            return -1;
        }
    }
}
