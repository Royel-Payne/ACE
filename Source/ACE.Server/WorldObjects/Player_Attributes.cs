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
        /// never be derived from the attribute being raised. If it were, difficulty/base would sit at a
        /// constant 1.0 and every award would equal the whole attribute and grow as it grows.
        /// The denominator being the attribute's Base is fine and intended: that is what makes gain
        /// self-limiting as the attribute climbs.
        ///
        /// Returns true if XP was applied.
        /// </summary>
        public bool AwardAttributeUsageXP(PropertyAttribute attribute, uint difficulty, bool isSecondary = false, double weightOverride = 0.0)
        {
            if (difficulty == 0)
                return false;

            if (!Attributes.TryGetValue(attribute, out var creatureAttribute))
                return false;

            var debug = PropertyManager.GetBool("attribute_debug_logging").Item;

            var attributeXPTable = DatManager.PortalDat.XpTable.AttributeXpList;
            var tableMaxXP = attributeXPTable[attributeXPTable.Count - 1];

            // Shadowgain 005: attributes stay CAPPED by default, unlike skills. 004's
            // vitals-follow-attributes math is built on the 190-attribute / 196-vital ceilings, so
            // uncapping attributes silently breaks the proportion vitals are derived from.
            // The toggle exists for completeness; past the cap gains are deliberately brutal.
            var overcapAllowed = PropertyManager.GetBool("attribute_overcap_allow").Item;

            var atCap = creatureAttribute.ExperienceSpent >= tableMaxXP;

            if (atCap && !overcapAllowed)
            {
                if (debug)
                    log.Info($"[ATTRIBUTE] {Name} | {attribute} | NOOP=maxRank | difficulty={difficulty}");

                return false;
            }

            var maxXP = overcapAllowed ? uint.MaxValue : tableMaxXP;

            if (creatureAttribute.ExperienceSpent >= maxXP)
                return false;

            var floor = PropertyManager.GetDouble("attribute_gain_difficulty_floor").Item;
            var cap = PropertyManager.GetDouble("attribute_gain_difficulty_cap").Item;
            var minAward = (uint)Math.Max(0, PropertyManager.GetLong("attribute_gain_min_award").Item);

            var multiplier = PropertyManager.GetDouble("attribute_gain_multiplier").Item;

            // Greylock wanted the mental attributes slowest. Shipped at 1.0 - the dial exists,
            // the operator picks the value; we do not bake in a balance target.
            if (attribute == PropertyAttribute.Focus || attribute == PropertyAttribute.Self)
                multiplier *= PropertyManager.GetDouble("attribute_gain_mental_multiplier").Item;

            // overlapping mapping: an action feeds a primary attribute fully and a related one partially.
            // weightOverride lets a caller set its own fraction (011 uses it so spell-aiming Coordination
            // can be tuned independently of the melee overlap - magic difficulty already runs about half
            // melee's, so sharing one factor would compound that disadvantage).
            if (weightOverride > 0.0)
                multiplier *= weightOverride;
            else if (isSecondary)
                multiplier *= PropertyManager.GetDouble("attribute_gain_overlap_factor").Item;

            // past the table cap (only reachable with attribute_overcap_allow on) gains crawl
            if (atCap)
                multiplier *= PropertyManager.GetDouble("attribute_overcap_multiplier").Item;

            // Base, deliberately NOT Current - see the matching note in Proficiency.cs. Current applies
            // enchantments and vitae, which made being buffed shrink your gain. Buffing is the normal
            // state in AC, so that penalised standard play and made measurements buff-dependent.
            var baseValue = Math.Max(1u, creatureAttribute.Base);
            var ratio = (double)difficulty / baseValue;
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
            creatureAttribute.Ranks = (ushort)Math.Max(0, overcapAllowed
                ? CalcAttributeRankUncapped(creatureAttribute.ExperienceSpent)
                : CalcAttributeRank(creatureAttribute.ExperienceSpent));

            // CreatureAttribute's setters, unlike CreatureSkill's, do NOT flag the biota as dirty -
            // without this the gain is never persisted.
            ChangesDetected = true;

            if (debug)
            {
                log.Info($"[ATTRIBUTE] {Name} | {attribute} | AWARD{(isSecondary ? "=secondary" : "")}" +
                         $" | difficulty={difficulty} vs base={baseValue} ratio={ratio:N3}" +
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

            var vitalXPTable = DatManager.PortalDat.XpTable.VitalXpList;

            // Shadowgain 013: the attribute rank ceiling is no longer the table's 190. With every
            // attribute starting at 10, reaching attribute_max_value takes ~280 ranks, so the
            // proportion below has to be measured against THAT ceiling or vitals would max out long
            // before their attribute does - at 190/280 of the way there.
            var attributeMaxRanks = AttributeMaxRanks();
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
        /// <summary>
        /// Shadowgain 019: which attributes a weapon skill trains, read from the GAME'S OWN skill
        /// formula in client_portal.dat rather than a hand-written table.
        ///
        /// The hand-written version was wrong, and Greylock found it by playing: Light Weapons
        /// trained Quickness and no Strength at all. I had mapped it on the intuition that "light
        /// means fast". The dat says Light Weapons is Strength + Coordination - identical to Heavy
        /// Weapons. Four of the six weapon mappings were wrong to some degree:
        ///
        ///     skill             dat formula              what I had written
        ///     LightWeapons      Strength + Coordination  Quickness + Coordination   WRONG
        ///     FinesseWeapons    Quickness + Coordination Coordination + Quickness   swapped
        ///     MissileWeapons    Coordination only        Coordination + Quickness   extra
        ///     DualWield         Coordination             Quickness + Coordination   WRONG
        ///
        /// Reading the dat removes the whole class of error: the attributes a skill trains are now
        /// the attributes that skill is actually computed from, by construction, and this cannot
        /// drift from the game again.
        ///
        /// Attr2 is skipped when absent (Missile Weapons has none) or identical to Attr1 (Dual Wield
        /// lists Coordination twice), so neither is double-awarded.
        /// </summary>
        public void AwardAttributesForWeaponSkill(Skill skill, uint difficulty)
        {
            if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
                return;

            var primary = (PropertyAttribute)skillBase.Formula.Attr1;
            var secondary = (PropertyAttribute)skillBase.Formula.Attr2;

            if (primary == PropertyAttribute.Undef)
                return;

            AwardAttributeUsageXP(primary, difficulty);

            if (secondary != PropertyAttribute.Undef && secondary != primary)
                AwardAttributeUsageXP(secondary, difficulty, true);
        }

        /// <summary>
        /// Shadowgain 004: maps a magic school (or Healing) to Focus or Self.
        /// Difficulty comes from the caller - target MagicDefense or heal difficulty - never from the
        /// attribute being raised.
        /// </summary>
        /// <summary>
        /// Shadowgain 004: overload for the spell paths, which carry MagicSchool rather than Skill.
        /// War/void/enchantment are Focus; life magic is Self.
        ///
        /// NOTE (019): this deliberately does NOT follow the dat formula, unlike the weapon mapping
        /// beside it. The dat makes every magic school Focus primary + Self secondary, which would
        /// leave Self as nothing but a 0.25-weight passenger on every cast - it is the primary
        /// attribute of no school at all. Routing Life Magic to Self instead gives it a real path,
        /// and measures healthy in play (714 awards / 11,942 xp on a live character).
        ///
        /// So this divergence is intentional and load-bearing. Do not "correct" it to match the dat
        /// without first checking what happens to Self.
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
                    AwardAttributeUsageXP(PropertyAttribute.Self, difficulty);
                    break;

                // Healing's own formula is (Focus + Coordination) / 3 - confirmed against the AC
                // wiki - so using a healing kit should train THOSE, not Self. 004 mapped it to Self,
                // which was wrong.
                //
                // This also closes the last stranded-attribute edge: a pure melee character had no
                // Focus path at all, and healing kits are near-universal. General principle worth
                // reusing - a skill's use should train the attributes that skill is BUILT from.
                case Skill.Healing:
                    AwardAttributeUsageXP(PropertyAttribute.Focus, difficulty);
                    AwardAttributeUsageXP(PropertyAttribute.Coordination, difficulty, true);
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
        /// <summary>
        /// Shadowgain 005: attribute rank extended past the table top, mirroring
        /// CalcSkillRankUncapped. Only reachable when attribute_overcap_allow is on, which it is
        /// NOT by default - 004 ties vital ranks to the attribute ceiling.
        /// </summary>
        public static int CalcAttributeRankUncapped(uint xpAmount)
        {
            var rankXpTable = DatManager.PortalDat.XpTable.AttributeXpList;

            if (rankXpTable == null || rankXpTable.Count < 2)
                return CalcAttributeRank(xpAmount);

            var topRank = rankXpTable.Count - 1;
            var topXp = rankXpTable[topRank];

            if (xpAmount < topXp)
                return CalcAttributeRank(xpAmount);

            // same reasoning as CalcSkillRankUncapped - the table's own final step exceeds the
            // remaining uint headroom, so the overflow cost is a config value instead
            var overcapCost = PropertyManager.GetDouble("skill_overcap_rank_cost").Item;

            if (overcapCost < 1.0)
                overcapCost = 1.0;

            var growth = PropertyManager.GetDouble("skill_overcap_growth").Item;
            if (growth < 1.0) growth = 1.0;

            var extra = (double)xpAmount - topXp;

            double extraRanks;

            if (growth - 1.0 < 0.000001)
                extraRanks = extra / overcapCost;
            else
                extraRanks = Math.Log(1.0 + extra * (growth - 1.0) / overcapCost) / Math.Log(growth);

            if (double.IsNaN(extraRanks) || extraRanks < 0)
                extraRanks = 0;

            return (int)Math.Min(topRank + (long)extraRanks, ushort.MaxValue - 1);
        }

        public static int CalcAttributeRank(uint xpAmount)
        {
            if (PropertyManager.GetBool("attributes_start_at_ten").Item)
                return CalcAttributeRankScaled(xpAmount);

            var rankXpTable = DatManager.PortalDat.XpTable.AttributeXpList;

            for (var i = rankXpTable.Count - 1; i >= 0; i--)
            {
                var rankAmount = rankXpTable[i];
                if (xpAmount >= rankAmount)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Shadowgain 013: how many ranks an attribute must be able to earn so that, starting from
        /// the uniform value of 10, it reaches attribute_max_value.
        ///
        /// Attribute value is StartingValue + Ranks, so a start-10 attribute needs
        /// (attribute_max_value - 10) ranks - about 280 for the retail ceiling of 290, against a dat
        /// table that only defines 190.
        /// </summary>
        public static int AttributeMaxRanks()
        {
            var tableMax = DatManager.PortalDat.XpTable.AttributeXpList.Count - 1;

            if (!PropertyManager.GetBool("attributes_start_at_ten").Item)
                return tableMax;

            var maxValue = PropertyManager.GetLong("attribute_max_value").Item;

            var ranks = (int)(maxValue - AttributeStartingValue);

            return ranks > 0 ? ranks : tableMax;
        }

        /// <summary>
        /// Shadowgain 013: cost to reach a given attribute rank, with the dat table STRETCHED across
        /// the wider rank range instead of extended past its end.
        ///
        /// Why stretch rather than extend: the table's own final step is 308,765,680 while the entire
        /// remaining uint headroom above its 4,019,438,644 total is only 275,528,651. Continuing at
        /// the table's pace therefore buys **zero** further ranks - the identical trap that made
        /// 005's first skill-overcap attempt produce no extra ranks at all.
        ///
        /// Stretching keeps the total cost of a maxed attribute exactly what retail charges
        /// (4,019,438,644, which fits), keeps cost per rank monotonically increasing, and simply
        /// spreads the same climb over more, smaller ranks. The alternative - a cheap flat tail past
        /// rank 190 - would make the last 90 ranks about 4% of the grind and invert the curve.
        ///
        /// Interpolates linearly between table entries so the curve stays smooth rather than stepping
        /// wherever two ranks would land on the same index.
        /// </summary>
        public static uint AttributeRankCost(int rank)
        {
            var table = DatManager.PortalDat.XpTable.AttributeXpList;
            var tableMax = table.Count - 1;

            if (rank <= 0)
                return 0;

            var maxRanks = AttributeMaxRanks();

            if (rank >= maxRanks)
                return table[tableMax];

            // position of this rank on the table's own scale
            var t = (double)rank * tableMax / maxRanks;

            var lower = (int)Math.Floor(t);
            var frac = t - lower;

            if (lower >= tableMax)
                return table[tableMax];

            var cost = table[lower] + frac * (table[lower + 1] - table[lower]);

            if (cost < 0)
                return 0;

            return cost >= uint.MaxValue ? uint.MaxValue : (uint)Math.Round(cost);
        }

        /// <summary>
        /// Shadowgain 013: highest rank affordable with this much attribute XP, under the stretched
        /// curve. Binary search - the cost function is monotonic by construction.
        /// </summary>
        public static int CalcAttributeRankScaled(uint xpAmount)
        {
            var maxRanks = AttributeMaxRanks();

            if (xpAmount < AttributeRankCost(1))
                return 0;

            var lo = 0;
            var hi = maxRanks;

            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;

                if (AttributeRankCost(mid) <= xpAmount)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            return lo;
        }
    }
}
