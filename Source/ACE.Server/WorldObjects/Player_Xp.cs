using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common.Extensions;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// A player earns XP through natural progression, ie. kills and quests completed
        /// </summary>
        /// <param name="amount">The amount of XP being added</param>
        /// <param name="xpType">The source of XP being added</param>
        /// <param name="shareable">True if this XP can be shared with Fellowship</param>
        public void EarnXP(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            //Console.WriteLine($"{Name}.EarnXP({amount}, {sharable}, {fixedAmount})");

            // apply xp modifiers.  Quest XP is multiplicative with general XP modification
            var questModifier = PropertyManager.GetDouble("quest_xp_modifier").Item;
            var modifier = PropertyManager.GetDouble("xp_modifier").Item;
            if (xpType == XpType.Quest)
                modifier *= questModifier;

            // Shadowgain 021: scale kill XP by the same lane speed as skill/attribute gain, kept in
            // PROPORTION on purpose. Level grants no power here (verified: every read of Level is
            // content-gating, death cost, vitae or housing), so scaling XP alone would make a
            // high-level character with beginner skills. Scaling both keeps level an honest proxy
            // and unlocks content-gates in step with actual capability.
            modifier *= ProgressionSpeed;

            // should this be passed upstream to fellowship / allegiance?
            var enchantment = GetXPAndLuminanceModifier(xpType);

            var m_amount = (long)Math.Round(amount * enchantment * modifier);

            if (m_amount < 0)
            {
                log.Warn($"{Name}.EarnXP({amount}, {shareType})");
                log.Warn($"modifier: {modifier}, enchantment: {enchantment}, m_amount: {m_amount}");
                return;
            }

            GrantXP(m_amount, xpType, shareType);
        }

        /// <summary>
        /// Directly grants XP to the player, without the XP modifier
        /// </summary>
        /// <param name="amount">The amount of XP to grant to the player</param>
        /// <param name="xpType">The source of the XP being granted</param>
        /// <param name="shareable">If TRUE, this XP can be shared with fellowship members</param>
        /// <summary>
        /// Shadowgain 193: UNIFIED PROGRESSION. Feed use-based XP into TotalExperience, so character
        /// level is derived from skill+attribute use rather than from kills.
        ///
        /// 192 established the design: level already comes from TotalExperience via
        /// CheckForLevelup's walk over CharacterLevelXPList, so the cheapest correct change is not to
        /// re-point the level derivation - it is to change what FEEDS TotalExperience. That keeps
        /// TotalExperience the single source of truth for the client XP bar, /xp, fellowship and
        /// Enlightenment, all of which keep working untouched. Additive, not structural.
        ///
        /// GrantXP, not EarnXP, and deliberately: EarnXP would re-apply xp_modifier AND
        /// ProgressionSpeed, but the caller's award has already been through ProgressionSpeed in
        /// Proficiency/Player_Attributes. Passing through EarnXP would square the lane speed.
        ///
        /// ShareType.None because use-based XP is PERSONAL - you earned it by swinging. Splitting it
        /// to a fellowship would pay people for someone else's practice, and passing it up an
        /// allegiance would do the same. XpType.Proficiency also keeps it out of GrantItemXP, which
        /// only fires for Kill and Quest.
        /// </summary>
        public void GrantUnifiedProgressXP(long amount)
        {
            if (amount <= 0) return;

            if (!PropertyManager.GetBool("unified_progression_enabled").Item)
                return;

            var scale = PropertyManager.GetDouble("unified_progression_scale").Item;

            if (double.IsNaN(scale) || scale <= 0.0)
                return;

            var granted = (long)Math.Round(amount * scale);

            if (granted <= 0) return;

            // NOT GrantXP, and this was a real bug for one build: GrantXP -> UpdateXpAndLevel does
            // `AvailableExperience += addAmount` alongside TotalExperience, and also calls
            // AwardLeadershipUse. So routing use-XP through it inflated the AUGMENTATION currency
            // (AugmentationDevice spends AvailableExperience directly) and trained Leadership off
            // skill swings - two side effects, neither intended, both invisible until Chris asked why
            // the unassigned pool was grossly inflated.
            //
            // Skill XP is already SPENT by definition - it went into a skill. It must raise the level
            // total and nothing else, so this does exactly that and then re-uses CheckForLevelup.
            var maxLevelXp = (long)DatManager.PortalDat.XpTable.CharacterLevelXPList.Last();
            var room = maxLevelXp - (TotalExperience ?? 0);

            if (room <= 0) return;

            if (granted > room)
                granted = room;

            TotalExperience += granted;

            if (Session != null)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(
                    this, PropertyInt64.TotalExperience, TotalExperience ?? 0));

            CheckForLevelup();
        }

        public void GrantXP(long amount, XpType xpType, ShareType shareType = ShareType.All)
        {
            if (IsOlthoiPlayer)
            {
                if (HasVitae)
                    UpdateXpVitae(amount);

                return;
            }

            if (Fellowship != null && Fellowship.ShareXP && shareType.HasFlag(ShareType.Fellowship))
            {
                // this will divy up the XP, and re-call this function
                // with ShareType.Fellowship removed
                Fellowship.SplitXp((ulong)amount, xpType, shareType, this);
                return;
            }

            // Make sure UpdateXpAndLevel is done on this players thread
            EnqueueAction(new ActionEventDelegate(() => UpdateXpAndLevel(amount, xpType)));

            // for passing XP up the allegiance chain,
            // this function is only called at the very beginning, to start the process.
            if (shareType.HasFlag(ShareType.Allegiance))
                UpdateXpAllegiance(amount);

            // only certain types of XP are granted to items
            if (xpType == XpType.Kill || xpType == XpType.Quest)
                GrantItemXP(amount);
        }

        /// <summary>
        /// Adds XP to a player's total XP, handles triggers (vitae, level up)
        /// </summary>
        private void UpdateXpAndLevel(long amount, XpType xpType)
        {
            // Shadowgain 092: a quest turn-in ALSO grants attribute progress. Additive - the level
            // grant below is untouched, so content gates and the roll metric behave exactly as before.
            //
            // THIS IS THE SOLVER-ONLY GUARD, and it is load-bearing. It sits here, at the recipient,
            // rather than at the emote call sites, because this is the one place every path that
            // credits a player's own XP converges. Fellowship shares arrive re-typed as
            // XpType.Fellowship (Fellowship.SplitXp: `player == member ? XpType.Quest :
            // XpType.Fellowship`) and allegiance pass-up never makes a Quest-typed grant at all, so
            // testing xpType here is precisely "the person who solved it".
            //
            // Do not move this to a path that also sees Fellowship or Allegiance XP.
            if (xpType == XpType.Quest)
                AwardQuestAttributeXp(amount);

            // Shadowgain 193 (step 3): QUEST XP BUYS AUGMENTATIONS, IT DOES NOT BUY LEVELS.
            //
            // This is the point of the change, not a side effect: high-tier quest XP is precisely how
            // a character reaches the cap fast, and under unified progression level is supposed to
            // mean accumulated USE. Letting quests feed it would reopen the exact gap 190 measured.
            //
            // NO NEW PROPERTY WAS NEEDED. AvailableExperience already IS this pool: it is retail's
            // unassigned XP, AugmentationDevice.cs already spends it directly, and the level-up
            // message already tells players it is 'spendable only on augmentation gems'. The reserve
            // pool Chris wanted has existed all along - it just had kill XP pouring into it.
            //
            // Deliberately BEFORE the TotalExperience block, and returns, so quest XP never touches
            // level, Leadership or the level-up path.
            if (xpType == XpType.Quest && PropertyManager.GetBool("quest_xp_to_reserve_only").Item)
            {
                AvailableExperience += amount;

                if (Session != null)
                    Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(
                        this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0));

                return;
            }

            // until we are max level we must make sure that we send
            var xpTable = DatManager.PortalDat.XpTable;

            var maxLevel = GetMaxLevel();
            var maxLevelXp = xpTable.CharacterLevelXPList.Last();

            if (Level != maxLevel)
            {
                var addAmount = amount;

                var amountLeftToEnd = (long)maxLevelXp - TotalExperience ?? 0;
                if (amount > amountLeftToEnd)
                    addAmount = amountLeftToEnd;

                // Shadowgain: AvailableExperience is deliberately NOT suppressed, despite skills (003),
                // attributes and vitals (004) all being raised by use now.
                //
                // It looked like dead weight, but AugmentationDevice.cs:127 spends it directly
                // (player.AvailableExperience -= AugmentationCost) - augmentation gems are bought with
                // unassigned XP. Zeroing the pool would silently disable augmentations entirely.
                //
                // So the pool still has exactly one purpose, and the level-up message says so rather
                // than claiming it raises skills or attributes, which it no longer can.
                AvailableExperience += addAmount;
                TotalExperience += addAmount;

                // Shadowgain 007: Leadership trains on XP earned while fellowed with your own vassals.
                AwardLeadershipUse(addAmount);

                var xpTotalUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.TotalExperience, TotalExperience ?? 0);
                var xpAvailUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0);
                Session.Network.EnqueueSend(xpTotalUpdate, xpAvailUpdate);

                CheckForLevelup();
            }

            if (xpType == XpType.Quest)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You've earned {amount:N0} experience.", ChatMessageType.Broadcast));

            if (HasVitae && xpType != XpType.Allegiance)
                UpdateXpVitae(amount);
        }

        /// <summary>
        /// Optionally passes XP up the Allegiance tree
        /// </summary>
        private void UpdateXpAllegiance(long amount)
        {
            if (!HasAllegiance) return;

            AllegianceManager.PassXP(AllegianceNode, (ulong)amount, true);

            // Shadowgain 007: Loyalty trains on what you pass up to your patron.
            AwardLoyaltyUse(amount);
        }

        /// <summary>
        /// Handles updating the vitae penalty through earned XP
        /// </summary>
        /// <param name="amount">The amount of XP to apply to the vitae penalty</param>
        private void UpdateXpVitae(long amount)
        {
            var vitae = EnchantmentManager.GetVitae();

            if (vitae == null)
            {
                log.Error($"{Name}.UpdateXpVitae({amount}) vitae null, likely due to cross-thread operation or corrupt EnchantmentManager cache. Please report this.");
                log.Error(Environment.StackTrace);
                return;
            }

            var vitaePenalty = vitae.StatModValue;
            var startPenalty = vitaePenalty;

            var maxPool = (int)VitaeCPPoolThreshold(vitaePenalty, DeathLevel.Value);
            var curPool = VitaeCpPool + amount;
            while (curPool >= maxPool)
            {
                curPool -= maxPool;
                vitaePenalty = EnchantmentManager.ReduceVitae();
                if (vitaePenalty == 1.0f)
                    break;
                maxPool = (int)VitaeCPPoolThreshold(vitaePenalty, DeathLevel.Value);
            }
            VitaeCpPool = (int)curPool;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.VitaeCpPool, VitaeCpPool.Value));

            if (vitaePenalty != startPenalty)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your experience has reduced your Vitae penalty!", ChatMessageType.Magic));
                EnchantmentManager.SendUpdateVitae();
            }

            if (vitaePenalty.EpsilonEquals(1.0f) || vitaePenalty > 1.0f)
            {
                var actionChain = new ActionChain();
                actionChain.AddDelaySeconds(2.0f);
                actionChain.AddAction(this, () =>
                {
                    var vitae = EnchantmentManager.GetVitae();
                    if (vitae != null)
                    {
                        var curPenalty = vitae.StatModValue;
                        if (curPenalty.EpsilonEquals(1.0f) || curPenalty > 1.0f)
                            EnchantmentManager.RemoveVitae();
                    }
                });
                actionChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Returns the maximum possible character level
        /// </summary>
        public static uint GetMaxLevel()
        {
            return (uint)DatManager.PortalDat.XpTable.CharacterLevelXPList.Count - 1;
        }

        /// <summary>
        /// Returns TRUE if player >= MaxLevel
        /// </summary>
        public bool IsMaxLevel => Level >= GetMaxLevel();

        /// <summary>
        /// Returns the remaining XP required to reach a level
        /// </summary>
        public long? GetRemainingXP(uint level)
        {
            var maxLevel = GetMaxLevel();
            if (level < 1 || level > maxLevel)
                return null;

            var levelTotalXP = DatManager.PortalDat.XpTable.CharacterLevelXPList[(int)level];

            return (long)levelTotalXP - TotalExperience.Value;
        }

        /// <summary>
        /// Returns the remaining XP required to the next level
        /// </summary>
        public ulong GetRemainingXP()
        {
            var maxLevel = GetMaxLevel();
            if (Level >= maxLevel)
                return 0;

            var nextLevelTotalXP = DatManager.PortalDat.XpTable.CharacterLevelXPList[Level.Value + 1];
            return nextLevelTotalXP - (ulong)TotalExperience.Value;
        }

        /// <summary>
        /// Returns the total XP required to reach a level
        /// </summary>
        public static ulong GetTotalXP(int level)
        {
            var maxLevel = GetMaxLevel();
            if (level < 0 || level > maxLevel)
                return 0;

            return DatManager.PortalDat.XpTable.CharacterLevelXPList[level];
        }

        /// <summary>
        /// Returns the total amount of XP required for a player reach max level
        /// </summary>
        public static long MaxLevelXP
        {
            get
            {
                var xpTable = DatManager.PortalDat.XpTable.CharacterLevelXPList;

                return (long)xpTable[xpTable.Count - 1];
            }
        }

        /// <summary>
        /// Returns the XP required to go from level A to level B
        /// </summary>
        public ulong GetXPBetweenLevels(int levelA, int levelB)
        {
            // special case for max level
            var maxLevel = (int)GetMaxLevel();

            levelA = Math.Clamp(levelA, 1, maxLevel - 1);
            levelB = Math.Clamp(levelB, 1, maxLevel);

            var levelA_totalXP = DatManager.PortalDat.XpTable.CharacterLevelXPList[levelA];
            var levelB_totalXP = DatManager.PortalDat.XpTable.CharacterLevelXPList[levelB];

            return levelB_totalXP - levelA_totalXP;
        }

        public ulong GetXPToNextLevel(int level)
        {
            return GetXPBetweenLevels(level, level + 1);
        }

        /// <summary>
        /// Determines if the player has advanced a level
        /// </summary>
        private void CheckForLevelup()
        {
            var xpTable = DatManager.PortalDat.XpTable;

            var maxLevel = GetMaxLevel();

            if (Level >= maxLevel) return;

            var startingLevel = Level;
            bool creditEarned = false;

            // increases until the correct level is found
            while ((ulong)(TotalExperience ?? 0) >= xpTable.CharacterLevelXPList[(Level ?? 0) + 1])
            {
                Level++;

                // increase the skill credits if the chart allows this level to grant a credit
                //
                // Shadowgain 013: not when everything is auto-trained and specialization is off -
                // credits then buy literally nothing. The reconcile zeroes them at creation and at
                // login, but this line re-granted them on every level-up, so they quietly
                // accumulated during play (found on Chris's fresh character: 6 credits by level 7)
                // and the client kept offering to spend them on a dead economy. Suppressed at the
                // source rather than mopped up afterwards, so the "you have N credits" message
                // never appears either.
                var creditsAreSpendable = !(PropertyManager.GetBool("all_skills_trained").Item
                                            && PropertyManager.GetBool("disable_specialization").Item);

                if (creditsAreSpendable && xpTable.CharacterLevelSkillCreditList[Level ?? 0] > 0)
                {
                    AvailableSkillCredits += (int)xpTable.CharacterLevelSkillCreditList[Level ?? 0];
                    TotalSkillCredits += (int)xpTable.CharacterLevelSkillCreditList[Level ?? 0];
                    creditEarned = true;
                }

                // break if we reach max
                if (Level == maxLevel)
                {
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    break;
                }
            }

            if (Level > startingLevel)
            {
                var message = (Level == maxLevel) ? $"You have reached the maximum level of {Level}!" : $"You are now level {Level}!";

                // Shadowgain: the retail wording ("experience available to raise skills and attributes")
                // is wrong on this server. 003 removed spending XP on skills; 004 removed it on
                // attributes AND vitals - so pooled experience now raises NOTHING and is pure residue.
                //
                // Built from the toggles rather than hardcoded, so it stays honest if an operator
                // switches any back on. The previous version said experience could still raise
                // attributes: true when 003 shipped, false the moment 004 landed. This construction is
                // specifically to stop that drift happening again.
                var skillsByUse = PropertyManager.GetBool("skill_gain_usage_only").Item;
                var attribsByUse = PropertyManager.GetBool("attribute_gain_usage_only").Item;
                var vitalsByUse = PropertyManager.GetBool("vital_gain_usage_only").Item;

                // Shadowgain 027: skill credits are vestigial when everything is auto-trained and
                // specialization is off - they buy nothing, are not granted at level-up, and are
                // zeroed on login. Anything that mentions or promises them is misleading.
                var creditsMeaningful = !(PropertyManager.GetBool("all_skills_trained").Item
                                          && PropertyManager.GetBool("disable_specialization").Item);

                if (skillsByUse || attribsByUse || vitalsByUse)
                {
                    // only advertise experience as spendable on whatever it can genuinely still buy
                    var spendable = new List<string>();
                    if (!skillsByUse) spendable.Add("skills");
                    if (!attribsByUse) spendable.Add("attributes");
                    if (!vitalsByUse) spendable.Add("health, stamina and mana");

                    if (spendable.Count > 0)
                        message += $"\nYou have {AvailableExperience:#,###0} experience points available to raise {string.Join(" and ", spendable)}.";
                    else
                        message += $"\nYou have {AvailableExperience:#,###0} experience points, spendable only on augmentation gems.";

                    if (AvailableSkillCredits > 0)
                        message += $"\nYou have {AvailableSkillCredits} skill credit{(AvailableSkillCredits == 1 ? "" : "s")} available to train new skills.";

                    var byUse = new List<string>();
                    if (skillsByUse) byUse.Add("Skills");
                    if (attribsByUse) byUse.Add("attributes");
                    if (vitalsByUse) byUse.Add("vitals");

                    message += $"\n{string.Join(", ", byUse)} rise through use, not experience.";
                }
                else
                    message += (AvailableSkillCredits > 0) ? $"\nYou have {AvailableExperience:#,###0} experience points and {AvailableSkillCredits} skill credits available to raise skills and attributes." : $"\nYou have {AvailableExperience:#,###0} experience points available to raise skills and attributes.";

                var levelUp = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.Level, Level ?? 1);
                var currentCredits = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0);

                // Shadowgain 027: don't promise a credit that will never arrive. Chris caught this
                // at level 34 - the text still ended with "You will earn another skill credit at
                // level 35", which on this world is simply false. The first two lines were reworded
                // for Shadowgain; this stock line was missed.
                if (creditsMeaningful && Level != maxLevel && !creditEarned)
                {
                    var nextLevelWithCredits = 0;

                    for (int i = (Level ?? 0) + 1; i <= maxLevel; i++)
                    {
                        if (xpTable.CharacterLevelSkillCreditList[i] > 0)
                        {
                            nextLevelWithCredits = i;
                            break;
                        }
                    }
                    message += $"\nYou will earn another skill credit at level {nextLevelWithCredits}.";
                }

                if (Fellowship != null)
                    Fellowship.OnFellowLevelUp(this);

                if (AllegianceNode != null)
                    AllegianceNode.OnLevelUp();

                Session.Network.EnqueueSend(levelUp);

                SetMaxVitals();

                // play level up effect
                PlayParticleEffect(PlayScript.LevelUp, Guid);

                Session.Network.EnqueueSend(new GameMessageSystemChat(message, ChatMessageType.Advancement), currentCredits);
            }
        }

        /// <summary>
        /// Spends the amount of XP specified, deducting it from available experience
        /// </summary>
        public bool SpendXP(long amount, bool sendNetworkUpdate = true)
        {
            if (amount > AvailableExperience)
                return false;

            AvailableExperience -= amount;

            if (sendNetworkUpdate)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0));

            return true;
        }

        /// <summary>
        /// Tries to spend all of the players Xp into Attributes, Vitals and Skills
        /// </summary>
        public void SpendAllXp(bool sendNetworkUpdate = true)
        {
            SpendAllAvailableAttributeXp(Strength, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Endurance, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Coordination, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Quickness, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Focus, sendNetworkUpdate);
            SpendAllAvailableAttributeXp(Self, sendNetworkUpdate);

            SpendAllAvailableVitalXp(Health, sendNetworkUpdate);
            SpendAllAvailableVitalXp(Stamina, sendNetworkUpdate);
            SpendAllAvailableVitalXp(Mana, sendNetworkUpdate);

            foreach (var skill in Skills)
            {
                if (skill.Value.AdvancementClass >= SkillAdvancementClass.Trained)
                    SpendAllAvailableSkillXp(skill.Value, sendNetworkUpdate);
            }
        }

        /// <summary>
        /// Gives available XP of the amount specified, without increasing total XP
        /// </summary>
        public void RefundXP(long amount)
        {
            AvailableExperience += amount;

            var xpUpdate = new GameMessagePrivateUpdatePropertyInt64(this, PropertyInt64.AvailableExperience, AvailableExperience ?? 0);
            Session.Network.EnqueueSend(xpUpdate);
        }

        public void HandleMissingXp()
        {
            var verifyXp = GetProperty(PropertyInt64.VerifyXp) ?? 0;
            if (verifyXp == 0) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                var xpType = verifyXp > 0 ? "unassigned experience" : "experience points";

                var msg = $"This character was missing some {xpType} --\nYou have gained an additional {Math.Abs(verifyXp).ToString("N0")} {xpType}!";

                Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

                if (verifyXp < 0)
                {
                    // add to character's total XP
                    TotalExperience -= verifyXp;

                    CheckForLevelup();
                }

                RemoveProperty(PropertyInt64.VerifyXp);
            });

            actionChain.EnqueueChain();
        }

        /// <summary>
        /// Returns the total amount of XP required to go from vitae to vitae + 0.01
        /// </summary>
        /// <param name="vitae">The current player life force, ie. 0.95f vitae = 5% penalty</param>
        /// <param name="level">The player DeathLevel, their level on last death</param>
        private double VitaeCPPoolThreshold(float vitae, int level)
        {
            return (Math.Pow(level, 2.5) * 2.5 + 20.0) * Math.Pow(vitae, 5.0) + 0.5;
        }

        /// <summary>
        /// Raise the available XP by a percentage of the current level XP or a maximum
        /// </summary>
        public void GrantLevelProportionalXp(double percent, long min, long max)
        {
            var nextLevelXP = GetXPBetweenLevels(Level.Value, Level.Value + 1);

            var scaledXP = (long)Math.Round(nextLevelXP * percent);

            if (max > 0)
                scaledXP = Math.Min(scaledXP, max);

            if (min > 0)
                scaledXP = Math.Max(scaledXP, min);

            // apply xp modifiers?
            EarnXP(scaledXP, XpType.Quest, ShareType.Allegiance);
        }

        /// <summary>
        /// The player earns XP for items that can be leveled up
        /// by killing creatures and completing quests,
        /// while those items are equipped.
        /// </summary>
        public void GrantItemXP(long amount)
        {
            foreach (var item in EquippedObjects.Values.Where(i => i.HasItemLevel))
                GrantItemXP(item, amount);
        }

        public void GrantItemXP(WorldObject item, long amount)
        {
            var prevItemLevel = item.ItemLevel.Value;
            var addItemXP = item.AddItemXP(amount);

            if (addItemXP > 0)
                Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(item, PropertyInt64.ItemTotalXp, item.ItemTotalXp.Value));

            // handle item leveling up
            var newItemLevel = item.ItemLevel.Value;
            if (newItemLevel > prevItemLevel)
            {
                OnItemLevelUp(item, prevItemLevel);

                var actionChain = new ActionChain();
                actionChain.AddAction(this, () =>
                {
                    var msg = $"Your {item.Name} has increased in power to level {newItemLevel}!";
                    Session.Network.EnqueueSend(new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

                    EnqueueBroadcast(new GameMessageScript(Guid, PlayScript.AetheriaLevelUp));
                });
                actionChain.EnqueueChain();
            }
        }

        /// <summary>
        /// Returns the multiplier to XP and Luminance from Trinkets and Augmentations
        /// </summary>
        public float GetXPAndLuminanceModifier(XpType xpType)
        {
            var enchantmentBonus = EnchantmentManager.GetXPBonus();

            var augBonus = 0.0f;
            if (xpType == XpType.Kill && AugmentationBonusXp > 0)
                augBonus = AugmentationBonusXp * 0.05f;

            var modifier = 1.0f + enchantmentBonus + augBonus;
            //Console.WriteLine($"XPAndLuminanceModifier: {modifier}");

            return modifier;
        }
    }
}
