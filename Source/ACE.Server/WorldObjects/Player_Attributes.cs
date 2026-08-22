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

            // Shadowgain 193: attributes count toward character level too (Chris's 193 decision -
            // both are use-based, so both should drive level). This is what moves the projected
            // recompute from the 'skill only' column to '+attributes' - Adramelech 172 -> 183.
            GrantUnifiedProgressXP(amount);

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

            // Shadowgain 021: progression lane - see Player_Progression.
            multiplier *= ProgressionSpeed;

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

            // Shadowgain 119: bound the difficulty against the attribute's own Base, exactly as
            // Proficiency.OnSuccessUse now does for skills - see the long note there.
            //
            // This path carries its OWN copy of the unbounded-difficulty bug. 118 and the 119 handoff
            // both describe the fix as touching "the XP hot path that every skill and attribute award
            // flows through", but attributes do not flow through Proficiency at all - they have this
            // parallel formula, with the same clamp on the ratio and the same free `difficulty` out
            // front. Fixing only Proficiency would have left the attribute half of the exploit wide
            // open, which is the half Apex actually demonstrated: "Can have it 200+ attributes in
            // minutes".
            var effectiveDifficulty = difficulty;

            var bound = PropertyManager.GetDouble("attribute_gain_difficulty_bound").Item;

            if (bound > 0)
            {
                var ceiling = baseValue * bound;

                if (ceiling < effectiveDifficulty)
                    effectiveDifficulty = (uint)Math.Max(1, Math.Round(ceiling));
            }

            var ratio = (double)effectiveDifficulty / baseValue;
            var difficultyFactor = Math.Clamp(ratio, Math.Min(floor, cap), Math.Max(floor, cap));

            var awarded = effectiveDifficulty * difficultyFactor * multiplier;

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

            // Shadowgain 199: THE USE-BASED AWARD DRIVES CHARACTER LEVEL TOO. It did not, and that was
            // a straight omission rather than a decision - 193 wired the unified grant into the skill
            // path (Proficiency) and into SpendAttributeXp, the MANUAL raise-with-unassigned-XP path,
            // and missed this one. Under unified progression AvailableExperience comes only from quests
            // and buys augmentations, so the manual path is essentially never taken: in practice
            // attributes contributed NOTHING to level while 193's own comment on the other call site
            // says they should, and while TryGetUseXp counted them when 194 set everyone's level.
            //
            // Measured on LIVE 2026-08-22 over 4 minutes and 8 characters: dTotalExperience equalled
            // dSkillXP exactly for every one of them, while 6,448,828 attribute xp was earned and
            // credited to nobody - 20.6% of all use-XP in the window, and 57.7% for the character who
            // leaned on attributes hardest. Levels were SET on a skill+attribute basis and then grew on
            // skills alone, so the gap widened with every hour played.
            //
            // GRANT WHAT LANDED, NOT WHAT WAS CHARGED. newXP is clamped by maxXP above, so at the
            // ceiling `pp` is computed but little or none of it is absorbed. Granting `pp` would level a
            // character off experience their attribute could not take - exactly what the `applied` gate
            // on the skill side exists to prevent. The delta is the only honest amount.
            var granted = (long)(newXP - prevXP);

            if (granted > 0)
                GrantUnifiedProgressXP(granted);

            if (debug)
            {
                // Shadowgain 119: show the raw difficulty too when the bound bit - see Proficiency.
                var boundNote = effectiveDifficulty != difficulty ? $" (bounded from {difficulty})" : "";

                log.Info($"[ATTRIBUTE] {Name} | {attribute} | AWARD{(isSecondary ? "=secondary" : "")}" +
                         $" | difficulty={effectiveDifficulty}{boundNote} vs base={baseValue} ratio={ratio:N3}" +
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
        /// Shadowgain 092: a quest turn-in grants ATTRIBUTE progress, on top of the level XP it
        /// already pays. Skills stay use-only and are untouched.
        ///
        /// **Why quests needed anything.** This redesign removed the thing quest XP bought, so a
        /// quest reward became near-worthless and questing was strictly a worse way to play.
        /// Greylock reported it cheerfully as a personality trait rather than as a problem, which is
        /// how a design flaw becomes permanent.
        ///
        /// **The base grant is denominated in RANKS, not XP** (092 addendum 3, superseding the
        /// original "percentage of quest XP" idea). Measured against `ace_world`, quest payouts span
        /// 75 to 1,700,000,000 xp - twenty-three million to one - so ANY single percentage grants the
        /// modal 1k-10k quest zero ranks while a top-tier turn-in grants around a hundred. Removing
        /// XP magnitude from the base step is what makes the feature meaningful at both ends.
        ///
        ///     fraction(R) = quest_attribute_rank_fraction x (1 - R / attributeMaxRanks) ^ decay
        ///
        /// evaluated per attribute against ITS OWN current rank, so a low attribute gains more per
        /// turn-in than a high one. That is 092's self-correcting property made explicit and tunable
        /// rather than merely emergent, and it lands on Quickness, the measured laggard.
        ///
        /// **Solver only.** The caller hooks strictly on `xpType == XpType.Quest` at the recipient.
        /// Verified in source rather than assumed: `Fellowship.SplitXp` re-types every non-solver
        /// share as `XpType.Fellowship` (`Fellowship.cs`: `player == member ? XpType.Quest :
        /// XpType.Fellowship`), and allegiance pass-up accrues to `AllegianceXPCached` without ever
        /// making a Quest-typed grant. Repeat farming is already blocked by `QuestManager`'s
        /// `MinDelta` / `MaxSolves`.
        ///
        /// Returns the number of attributes that gained at least some XP.
        /// </summary>
        public int AwardQuestAttributeXp(long questXp)
        {
            if (!PropertyManager.GetBool("quest_attribute_xp_enabled").Item)
                return 0;

            var attributeMaxRanks = AttributeMaxRanks();

            if (attributeMaxRanks <= 0)
                return 0;

            var baseFraction = PropertyManager.GetDouble("quest_attribute_rank_fraction").Item;
            var decay = PropertyManager.GetDouble("quest_attribute_rank_decay").Item;
            var ceiling = PropertyManager.GetDouble("quest_attribute_max_ranks").Item;

            if (baseFraction <= 0)
                return 0;

            if (decay < 0) decay = 0;

            // The ceiling is NON-OPTIONAL and is the single thing preventing a big-tier quest from
            // undoing the whole fix, so a nonsensical value must not disable it.
            if (double.IsNaN(ceiling) || ceiling <= 0) ceiling = 1.0;

            var tierMultiplier = GetQuestAttributeTierMultiplier(questXp);

            var overcapAllowed = PropertyManager.GetBool("attribute_overcap_allow").Item;
            var debug = PropertyManager.GetBool("attribute_debug_logging").Item;

            var gained = 0;
            var rankUps = 0;

            foreach (var kvp in Attributes)
            {
                var creatureAttribute = kvp.Value;

                if (creatureAttribute == null)
                    continue;

                var rank = (int)creatureAttribute.Ranks;

                // headroom fraction: 1.0 at rank 0, 0.0 at the ceiling
                var headroom = 1.0 - (double)rank / attributeMaxRanks;

                if (headroom <= 0)
                    continue;               // maxed - fraction is zero by construction

                var step = baseFraction * Math.Pow(headroom, decay);

                step *= tierMultiplier;

                // HARD per-turn-in, per-attribute ceiling. Applied AFTER the tier multiplier and
                // never bypassable - see 092 addendum 3.
                step = Math.Min(step, ceiling);

                if (double.IsNaN(step) || step <= 0)
                    continue;

                // Convert the fractional rank into XP on the STRETCHED curve at this rank.
                // AttributeRankCost is the function that actually defines rank here; it degrades to
                // the raw dat table when attributes_start_at_ten is off, so this is correct in both
                // configurations. (The addendum wrote this as AttributeXpList[R+1] - AttributeXpList[R];
                // that raw-table form would grant the wrong fraction of a rank under the stretch.)
                var costHere = AttributeRankCost(rank);
                var costNext = AttributeRankCost(rank + 1);

                if (costNext <= costHere)
                    continue;

                var xpToNext = costNext - costHere;

                var pp = (uint)Math.Min(uint.MaxValue, Math.Round(step * xpToNext));

                if (pp == 0)
                    continue;

                var prevRank = creatureAttribute.Ranks;

                creatureAttribute.ExperienceSpent = (uint)Math.Min((ulong)creatureAttribute.ExperienceSpent + pp, uint.MaxValue);

                creatureAttribute.Ranks = (ushort)Math.Max(0, overcapAllowed
                    ? CalcAttributeRankUncapped(creatureAttribute.ExperienceSpent)
                    : CalcAttributeRank(creatureAttribute.ExperienceSpent));

                // CreatureAttribute's setters do NOT flag the biota dirty - without this the grant
                // is never persisted. Same trap as the usage path above.
                ChangesDetected = true;

                gained++;

                if (debug)
                {
                    log.Info($"[092] {Name} | {kvp.Key} | questXp={questXp:N0} tier={tierMultiplier:N3}" +
                             $" | R={rank} headroom={headroom:N3} step={step:N4} rank" +
                             $" | pp={pp:N0} of {xpToNext:N0} | rank {prevRank}->{creatureAttribute.Ranks}");
                }

                if (Session == null)
                    continue;

                Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(this, creatureAttribute));

                if (prevRank != creatureAttribute.Ranks)
                {
                    rankUps++;

                    Session.Network.EnqueueSend(
                        new GameMessageSound(Guid, Sound.RaiseTrait),
                        new GameMessageSystemChat($"Your base {kvp.Key} is now {creatureAttribute.Base}!", ChatMessageType.Advancement));
                }
            }

            if (rankUps > 0)
                SyncVitalRanksToAttributes();

            return gained;
        }

        /// <summary>
        /// Shadowgain 092: optional multiplier letting bigger quests grant more, WITHOUT
        /// reintroducing the windfall the rank-denominated model exists to prevent.
        ///
        /// Off by default and returns exactly 1.0 when off, so the base behaviour is the flat
        /// per-turn-in fractional gain. Bounded by construction - it maps the quest's payout onto the
        /// seven order-of-magnitude bands actually present in `ace_world` (under 1k, 1k-10k, ... 100M+)
        /// and scales linearly across them, so the 23-million-to-one payout spread becomes at most a
        /// `1 + quest_attribute_tier_factor` multiplier. The hard ceiling clamps it regardless.
        /// </summary>
        public static double GetQuestAttributeTierMultiplier(long questXp)
        {
            if (!PropertyManager.GetBool("quest_attribute_tier_scaling_enabled").Item)
                return 1.0;

            var factor = PropertyManager.GetDouble("quest_attribute_tier_factor").Item;

            if (double.IsNaN(factor) || factor <= 0)
                return 1.0;

            if (questXp < 1)
                return 1.0;

            // band 0 = under 1k, band 6 = 100M and above
            var band = Math.Clamp((int)Math.Floor(Math.Log10(questXp)) - 2, 0, 6);

            return 1.0 + factor * (band / 6.0);
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

                var proportion = 1.0;

                // Shadowgain 171: HEALTH ONLY, and it exists because Endurance cannot be raised fast
                // enough to matter. The 013 stretch spreads a 190-entry dat table over 280 ranks, so
                // the server leader sits at 230 Endurance ranks having earned 8.8% of the XP the
                // ceiling costs - the remaining 50 ranks cost 3.67 BILLION, ten times everything he
                // has earned. That is 5,859 hours for him and 30,051 for the character behind him, so
                // no multiplier on the GAIN RATE can reach it: doubling endurance_damage_multiplier
                // halved a number still measured in years.
                //
                // Chris: 'end can't be a gate keeper to higher tier content. We need this to reach
                // near max so players even have enough health'. So this pays health at the Endurance
                // players can actually reach, instead of racing an exponential.
                //
                // Stamina and Mana are deliberately untouched - 'I don't want to change the other
                // attributes, they seem good'. Health is the one that gates content.
                if (vitalType == PropertyAttribute2nd.MaxHealth)
                    proportion = PropertyManager.GetDouble("health_rank_proportion").Item;

                if (proportion <= 0.0)
                    proportion = 1.0;   // a nonsensical value must not silently zero anyone's health

                var target = (uint)Math.Round((double)attributeRanks * vitalMaxRanks / attributeMaxRanks * proportion);

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
        public void AwardAttributesForWeaponSkill(Skill skill, uint difficulty, double weight = 1.0)
        {
            AwardAttributesForSkill(skill, difficulty, weight);
        }

        /// <summary>
        /// Shadowgain 022: THE single mapping from a skill to the attributes its use trains, read
        /// from the game's own skill formula. Weapons, magic, healing and everything else now go
        /// through here.
        ///
        /// Greylock's principle, and the reason this is data-driven rather than a switch: a skill's
        /// use should train the attributes that skill is actually BUILT from. Every hand-written
        /// version of this table has been wrong. 019 found four of six weapon mappings wrong
        /// (Light Weapons trained Quickness when the game says Strength). This audit found every
        /// magic school wrong in the same way - the dat makes all five Focus + Self, while the code
        /// awarded exactly one of the two:
        ///
        ///     school                dat formula      what the code did
        ///     LifeMagic             Focus + Self     Self only    <- Greylock found this one
        ///     WarMagic              Focus + Self     Focus only
        ///     VoidMagic             Focus + Self     Focus only
        ///     CreatureEnchantment   Focus + Self     Focus only
        ///     ItemEnchantment       Focus + Self     Focus only
        ///     ManaConversion        Focus + Self     Self only
        ///     ArcaneLore            Focus            Focus        ok
        ///     Healing               Focus + Coord    Focus + Coord ok
        ///
        /// Attr2 is skipped when absent (Missile Weapons, Arcane Lore, Run) or identical to Attr1
        /// (Dual Wield lists Coordination twice), so nothing is double-awarded.
        ///
        /// The secondary gets attribute_gain_overlap_factor (0.25) of a full award.
        /// </summary>
        /// <param name="weight">
        /// Shadowgain 168: scales BOTH halves, for callers paying a partial award - currently the
        /// failed-resist path for Magic Defense. At the default 1.0 the behaviour is unchanged.
        ///
        /// It cannot simply be forwarded as `weightOverride`, and that is a trap worth naming:
        /// downstream, `weightOverride` is an ELSE-IF against `isSecondary`, so passing the weight
        /// raw would pay the secondary the weight (0.10) INSTEAD of the overlap factor (0.25)
        /// rather than on top of it - quietly paying Focus four times what it should, on the very
        /// path that exists to stop Self starving.
        /// </param>
        public void AwardAttributesForSkill(Skill skill, uint difficulty, double weight = 1.0)
        {
            var (primary, secondary) = GetSkillAttributeFormula(skill);

            if (primary == PropertyAttribute.Undef)
                return;

            // 0.0 means "no override" downstream, which is what keeps the full-award path identical.
            var partial = weight < 1.0;
            var overlap = PropertyManager.GetDouble("attribute_gain_overlap_factor").Item;

            AwardAttributeUsageXP(primary, difficulty, false, partial ? weight : 0.0);

            if (secondary != PropertyAttribute.Undef && secondary != primary)
                AwardAttributeUsageXP(secondary, difficulty, true, partial ? overlap * weight : 0.0);
        }

        /// <summary>
        /// Shadowgain 147: THE one place a skill's attribute formula is decided - dat first, then this
        /// project's single deliberate override on top. Extracted so the award path and the
        /// sg-skillattrs diagnostic cannot disagree: before this the diagnostic read the dat
        /// directly and would have reported Mana Conversion as Focus-primary while the server
        /// awarded Self - a tool contradicting the code it explains, which is precisely how 019's
        /// stale warning survived three entries.
        /// </summary>
        public static (PropertyAttribute Primary, PropertyAttribute Secondary) GetSkillAttributeFormula(Skill skill)
        {
            if (skill == Skill.None || !DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
                return (PropertyAttribute.Undef, PropertyAttribute.Undef);

            var primary = (PropertyAttribute)skillBase.Formula.Attr1;
            var secondary = (PropertyAttribute)skillBase.Formula.Attr2;

                // Shadowgain 147: THE ONE DELIBERATE DEPARTURE FROM THE DAT, and it restores something
                // 022 removed by accident.
                //
                // The dat makes every magic school Focus primary + Self secondary, so once 022 made this
                // mapping data-driven, Self became the primary of NOTHING a caster chooses to do - its
                // only primary is Magic Defense, which is something done TO you. 019 had routed Life
                // Magic and Mana Conversion to Self precisely to avoid that, and left a comment saying
                // "do not correct it to match the dat without first checking what happens to Self". 022
                // corrected it to match the dat. The warning was still in the file, describing behaviour
                // that no longer existed.
                //
                // MEASURED on LIVE 2026-08-15 before changing anything: Self averaged rank 104 against
                // Focus at 121, on 10.4M xp against 28.7M - weighted award units 35,816 to Focus's
                // 81,603, a 2.28x gap. Chris: *"Self feels like it should gain with magic use the same
                // as focus."*
                //
                // Mana Conversion ALONE, not Life Magic as well: it is 41% of all magic award events on
                // the server (38,700 of ~94,000 in six hours), so flipping it moves the ratio to 0.81x
                // while flipping both overshoots to 0.69x and simply makes Focus the new laggard.
                // Thematically it is also the cleanest of the two - Self governs Mana, and Mana
                // Conversion is drawing on your own reserves.
                if (skill == Skill.ManaConversion && PropertyManager.GetBool("mana_conversion_trains_self").Item)
                    (primary, secondary) = (secondary, primary);

            return (primary, secondary);
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
        /// ~~NOTE (019): this deliberately does NOT follow the dat formula ... Routing Life Magic to
        /// Self instead gives it a real path ... Do not "correct" it to match the dat without first
        /// checking what happens to Self.~~
        ///
        /// **SUPERSEDED, and left struck through because the warning came true.** 022 made this
        /// mapping data-driven and did exactly what 019 said not to: it corrected to the dat, which
        /// made every school Focus primary + Self secondary and reduced Self to a 0.25-weight
        /// passenger with no primary of its own except Magic Defense. The warning stayed in the file
        /// for three entries, describing a routing that no longer existed - so it read as
        /// reassurance that the problem was handled.
        ///
        /// 147 restores the intent for Mana Conversion only (see AwardAttributesForSkill), measured
        /// rather than assumed. Life Magic is deliberately NOT restored: flipping both overshoots.
        ///
        /// The lesson is not "022 was wrong" - data-driven was the right call and fixed four broken
        /// weapon mappings. It is that a general rule silently swallowed a deliberate exception, and
        /// the comment recording the exception was not enough to stop it.
        /// </summary>
        public void AwardAttributesForMagicSkill(MagicSchool school, uint difficulty)
        {
            AwardAttributesForSkill(SchoolToSkill(school), difficulty);
        }

        public void AwardAttributesForMagicSkill(Skill school, uint difficulty)
        {
            AwardAttributesForSkill(school, difficulty);
        }

        private static Skill SchoolToSkill(MagicSchool school)
        {
            switch (school)
            {
                case MagicSchool.CreatureEnchantment: return Skill.CreatureEnchantment;
                case MagicSchool.ItemEnchantment:     return Skill.ItemEnchantment;
                case MagicSchool.LifeMagic:           return Skill.LifeMagic;
                case MagicSchool.WarMagic:            return Skill.WarMagic;
                case MagicSchool.VoidMagic:           return Skill.VoidMagic;
                default:                              return Skill.None;
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
