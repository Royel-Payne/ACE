using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Handles the GameAction 0x46 - RaiseSkill network message from client
        /// </summary>
        public bool HandleActionRaiseSkill(Skill skill, uint amount)
        {
            var creatureSkill = GetCreatureSkill(skill, false);

            if (creatureSkill == null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
            {
                log.Warn($"{Name}.HandleActionRaiseSkill({skill}, {amount}) - trained or specialized skill not found");
                return false;
            }

            if (amount > AvailableExperience)
            {
                log.Warn($"{Name}.HandleActionRaiseSkill({skill}, {amount}) - amount > AvailableExperience ({AvailableExperience})");
                return false;
            }

            var prevRank = creatureSkill.Ranks;

            if (!SpendSkillXp(creatureSkill, amount))
                return false;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, creatureSkill));

            if (prevRank != creatureSkill.Ranks)
            {
                // if the skill ranks out at the top of our xp chart
                // then we will start fireworks effects and have special text!
                var suffix = "";
                if (creatureSkill.IsMaxRank)
                {
                    // fireworks on rank up is 0x8D
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    suffix = $" and has reached its upper limit";
                }

                var sound = new GameMessageSound(Guid, Sound.RaiseTrait);
                var msg = new GameMessageSystemChat($"Your base {skill.ToSentence()} skill is now {creatureSkill.Base}{suffix}!", ChatMessageType.Advancement);

                Session.Network.EnqueueSend(sound, msg);

                // retail was missing the 'raise skill' runrate hook here
                if (skill == Skill.Run && PropertyManager.GetBool("runrate_add_hooks").Item)
                    HandleRunRateUpdate();
            }

            return true;
        }

        private bool SpendSkillXp(CreatureSkill creatureSkill, uint amount, bool sendNetworkUpdate = true)
        {
            var skillXPTable = GetSkillXPTable(creatureSkill.AdvancementClass);
            if (skillXPTable == null)
            {
                log.Warn($"{Name}.SpendSkillXp({creatureSkill.Skill}, {amount}) - player tried to raise {creatureSkill.AdvancementClass} skill");
                return false;
            }

            // ensure skill is not already max rank
            if (creatureSkill.IsMaxRank)
            {
                log.Warn($"{Name}.SpendSkillXp({creatureSkill.Skill}, {amount}) - player tried to raise skill beyond max rank");
                return false;
            }

            // the client should already handle this naturally,
            // but ensure player can't spend xp beyond the max rank
            var amountToEnd = creatureSkill.ExperienceLeft;

            if (amount > amountToEnd)
            {
                //log.Warn($"{Name}.SpendSkillXp({creatureSkill.Skill}, {amount}) - player tried to raise skill beyond {amountToEnd} experience");
                return false;   // returning error here, instead of setting amount to amountToEnd
            }

            // everything looks good at this point,
            // spend xp on skill
            if (!SpendXP(amount, sendNetworkUpdate))
            {
                log.Warn($"{Name}.SpendSkillXp({creatureSkill.Skill}, {amount}) - SpendXP failed");
                return false;
            }

            // 109: deliberately still the uint shadow. This path is fenced off above by IsMaxRank
            // and ExperienceLeft, both of which stop at the TOP OF THE TABLE - so it is unreachable
            // for any skill carrying overflow, and below the table top the shadow is the truth.
            creatureSkill.ExperienceSpent += amount;

            // calculate new rank
            creatureSkill.Ranks = (ushort)CalcSkillRank(creatureSkill.AdvancementClass, creatureSkill.ExperienceSpent);

            return true;
        }

        /// <summary>
        /// Shadowgain 109c: is this character currently a god?
        ///
        /// /god writes a snapshot of the real skill and attribute values into GodState and then
        /// overwrites the live ones; /ungod parses that string back. The leading "1" is the flag -
        /// this is the same test DoGodMode itself uses, kept in one place so the skill code and the
        /// admin code cannot disagree about who is a god.
        ///
        /// Anything that RE-DERIVES a skill's Ranks or InitLevel has to skip these characters, or it
        /// silently rewrites the very fields /ungod is holding a restore for.
        /// </summary>
        public bool IsInGodMode => GodState != null && GodState.StartsWith("1");

        /// <summary>
        /// Shadowgain: writes usage-based skill XP DIRECTLY into the skill.
        ///
        /// Deliberately does NOT go through HandleActionRaiseSkill/SpendSkillXp. That path spends
        /// from AvailableExperience, which (a) couples usage gain to the level-XP pool we are
        /// decoupling from, and (b) is the same path we disable for players under usage-only mode -
        /// routing usage through it would kill usage along with the shortcut.
        ///
        /// Leveling is unaffected: Level derives from TotalExperience, which this never touches.
        ///
        /// Returns true if any XP was applied.
        ///
        /// Shadowgain 109: with uncapping on there is no longer any cap to apply. XP accumulates
        /// into CreatureSkill.TrueExperienceSpent, a 64-bit value the client never sees, and the
        /// uint the packet carries becomes a clamped shadow of it. Before this, gains were silently
        /// DISCARDED once a skill reached 4,294,967,295 - which Loyalty had already done on 43 live
        /// characters, five days in.
        /// </summary>
        public bool AwardSkillUsageXP(CreatureSkill creatureSkill, uint amount)
        {
            if (amount == 0 || creatureSkill == null)
                return false;

            if (creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
                return false;

            var skillXPTable = GetSkillXPTable(creatureSkill.AdvancementClass);
            if (skillXPTable == null)
                return false;

            // Shadowgain 005: with uncapping on, the XP table's top is no longer a ceiling - the
            // rank formula continues past it. Without it, behaviour is unchanged from 003.
            var uncapped = PropertyManager.GetBool("skill_uncap_ranks").Item;

            // 109: the only remaining ceiling is what a PropertyInt64 can physically store, and it
            // is ~2 billion times further away than the one it replaced. Capped mode is unchanged.
            var maxXP = uncapped ? MaxTrueSkillXp : skillXPTable[skillXPTable.Count - 1];

            var currentXP = creatureSkill.TrueExperienceSpent;

            if (currentXP >= maxXP)
                return false;

            var prevRank = creatureSkill.Ranks;

            // Past the cap the overflow lives in InitLevel, so Ranks stops moving. Track both, or the
            // "your base skill is now N" message would go silent exactly when uncapped progression
            // starts - the player would see the number climb with no feedback that it had.
            var prevInitLevel = creatureSkill.InitLevel;

            var newXP = maxXP - currentXP < amount ? maxXP : currentXP + amount;

            creatureSkill.TrueExperienceSpent = newXP;

            var computedRank = uncapped
                ? CalcSkillRankUncapped(creatureSkill.AdvancementClass, newXP)
                : CalcSkillRank(creatureSkill.AdvancementClass, (uint)newXP);

            var tableMaxRank = skillXPTable.Count - 1;

            // Shadowgain 005: carry overflow ranks in InitLevel, NOT in Ranks.
            //
            // Measured live: the client CLAMPS Ranks at its own table maximum but HONOURS InitLevel.
            // With rank 269 / InitLevel 10 the panel showed 305 (= 69 attr + 10 + 226 clamped), while
            // the server had 348. Moving the overflow into InitLevel raised the panel to 431 - visible
            // progression past the cap, with no client modification.
            //
            // Base is attrFormula + InitLevel + Ranks either way, so the server-side value is
            // identical; this only changes which field carries it so the client will render it.
            //
            // Shadowgain 109c: BOTH fields are now written on every award, enforcing
            //
            //     Ranks     = min(rank, tableMax)
            //     InitLevel = baseInitLevel + max(0, rank - tableMax)
            //
            // The old shape only assigned InitLevel on the way UP, so a skill whose rank FELL back
            // to the table max kept its stale overflow forever - the field is not re-derived from
            // anything, so nothing would ever clear it. 109b made that reachable in bulk: every
            // skill sitting at the old uint ceiling re-derives to 208, takes the non-overflow path,
            // and would have kept the 91 phantom ranks in InitLevel while @myskills correctly
            // reported 208. Base and rank would have disagreed permanently, and the base is what
            // the player's panel shows.
            //
            // Safe because InitLevel carries nothing else FOR A MORTAL: measured on TEST, trained is
            // 0 on 1,931 rows across 52 characters and specialized is 10 on all 17. Augmentation
            // bonuses go through GetAugBonus_Base, not this field.
            //
            // THE EXCEPTION IS GOD MODE, and it is why this is guarded rather than unconditional.
            // /god parks Ranks = 226 and InitLevel = 5000 on every skill and snapshots the real
            // values into GodState for /ungod to restore. Re-deriving either field from XP would
            // collapse a god's skills on their very next kill, and leave the restore describing a
            // state that no longer exists. The pre-109c code never hit this because /god's XP
            // (4,100,490,438) derives to rank 207 on the trained curve - under the table max - so
            // the overflow branch never fired. Making the write unconditional is exactly what would
            // have exposed it. Chris: *"the /god switch may poison the results"*.
            //
            // XP still accumulates truthfully underneath; only the derived fields are left alone.
            if (uncapped && !IsInGodMode)
            {
                var baseInitLevel = creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized ? 10u : 0u;

                var overflowRanks = computedRank > tableMaxRank ? computedRank - tableMaxRank : 0;

                creatureSkill.Ranks = (ushort)Math.Min(computedRank, tableMaxRank);
                creatureSkill.InitLevel = baseInitLevel + (uint)overflowRanks;
            }
            else if (!IsInGodMode)
                creatureSkill.Ranks = (ushort)computedRank;

            if (Session == null)
                return true;    // still applied; just nobody to tell (logging out, etc.)

            Session.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(this, creatureSkill));

            if (prevRank != creatureSkill.Ranks || prevInitLevel != creatureSkill.InitLevel)
            {
                var suffix = "";

                // With uncapping on there is no upper limit, so never claim one was reached -
                // IsMaxRank still reports true past the table top, which would be a lie now.
                if (creatureSkill.IsMaxRank && !uncapped)
                {
                    PlayParticleEffect(PlayScript.WeddingBliss, Guid);
                    suffix = $" and has reached its upper limit";
                }

                Session.Network.EnqueueSend(
                    new GameMessageSound(Guid, Sound.RaiseTrait),
                    new GameMessageSystemChat($"Your base {creatureSkill.Skill.ToSentence()} skill is now {creatureSkill.Base}{suffix}!", ChatMessageType.Advancement));

                // same runrate hook the manual raise path applies
                if (creatureSkill.Skill == Skill.Run && PropertyManager.GetBool("runrate_add_hooks").Item)
                    HandleRunRateUpdate();
            }

            return true;
        }

        /// <summary>
        /// Handles the GameAction 0x47 - TrainSkill network message from client
        /// </summary>
        public bool HandleActionTrainSkill(Skill skill, int creditsSpent)
        {
            // get the actual cost to train the skill.
            if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
            {
                log.Warn($"{Name}.HandleActionTrainSkill({skill}, {creditsSpent}) - couldn't find skill base");
                return false;
            }

            // Shadowgain 095c: the SERVER decides the price, not the client. The client reads
            // TrainedCost out of the dat and will always send that, so under free training its
            // number and ours legitimately disagree - rejecting on the mismatch (as stock ACE does)
            // would make the skill panel's Train button silently fail. Charge our cost and ignore
            // theirs. Still logged when they differ, because outside all_skills_trained a mismatch
            // is the client-tampering signal the original check was there to catch.
            var trainingCost = GetTrainingCost(skillBase);

            if (creditsSpent != trainingCost)
                log.Debug($"{Name}.HandleActionTrainSkill({skill}, {creditsSpent}) - client value differs from server cost ({trainingCost}); charging the server value");

            // affordability is checked against OUR price, after it is known. Checking the client's
            // figure first (as stock ACE does) rejected free training outright, because the client
            // sends the dat's cost while the player may hold 0 credits.
            if (trainingCost > (AvailableSkillCredits ?? 0))
            {
                log.Warn($"{Name}.HandleActionTrainSkill({skill}) - not enough skill credits ({AvailableSkillCredits}) for cost {trainingCost}");
                return false;
            }

            // attempt to train the specified skill
            var success = TrainSkill(skill, trainingCost);

            var availableSkillCredits = $"You now have {AvailableSkillCredits} credits available.";

            if (success)
            {
                var updateSkill = new GameMessagePrivateUpdateSkill(this, GetCreatureSkill(skill));
                var skillCredits = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0);

                var msg = new GameMessageSystemChat($"{skill.ToSentence()} trained. {availableSkillCredits}", ChatMessageType.Advancement);

                Session.Network.EnqueueSend(updateSkill, skillCredits, msg);
            }
            else
                Session.Network.EnqueueSend(new GameMessageSystemChat($"Failed to train {skill.ToSentence()}! {availableSkillCredits}", ChatMessageType.Advancement));

            return success;
        }

        /// <summary>
        /// Shadowgain 095c: what training a skill COSTS in skill credits.
        ///
        /// The governing rule: *"training needs to be free, spec is the only thing with fees or
        /// credits."* Under all_skills_trained the heritage grant (52, or 68 Olthoi) is considered
        /// consumed at creation paying for every skill at once, so no individual training ever
        /// debits again - and, symmetrically, untraining refunds nothing, because nothing was paid.
        ///
        /// **This must never be done by zeroing TrainedCost itself.** The specialization price is
        /// DERIVED - `UpgradeCostFromTrainedToSpecialized => SpecializedCost - TrainedCost` - so
        /// zeroing the trained column would silently promote every specialization to the full
        /// column: War Magic 12 -> 28, Melee Defense 10 -> 20, Two Handed 8 -> 16. Against a
        /// 46-credit lifetime pool that roughly halves what a player can ever specialize, with no
        /// error and no log line. The cost is zeroed at the CHARGE SITES only; the dat is untouched
        /// and every consumer of the upgrade cost keeps seeing the real number.
        ///
        /// Gated on all_skills_trained: with that dial off this is stock ACE again, the 52 really is
        /// the player's to spend, and training should cost what the table says.
        ///
        /// Why free training also removes a whole class of hazard: nothing ever debits for training,
        /// so "credits used" is identically "credits spent on specialization" - exactly what
        /// BackfillLevelSkillCredits computes. There is no deficit for any checker to find. Training
        /// all 38 forced skills would cost 190 credits at dat prices against a 52 grant, and any
        /// audit that sums the trained column sees a ~138-credit hole (see the guards on
        /// verify-skill-credits / verify-skills).
        /// </summary>
        public static int GetTrainingCost(ACE.DatLoader.Entity.SkillBase skillBase)
        {
            if (PropertyManager.GetBool("all_skills_trained").Item)
                return 0;

            return skillBase.TrainedCost;
        }

        /// <summary>
        /// Shadowgain 093: the skills this character has DELIBERATELY untrained.
        ///
        /// All 38 skills being auto-trained means VTank buffs all 38, so buff cycles are long.
        /// Players asked to drop skills they never use to shorten it - legitimate QoL, and the
        /// tradeoff is entirely theirs: a pruned skill cannot gain from use.
        ///
        /// Stored as a comma-separated id list in a single PropertyString rather than a bool per
        /// skill, so it costs one row per character regardless of how many are pruned.
        /// </summary>
        public HashSet<Skill> GetPrunedSkills()
        {
            var pruned = new HashSet<Skill>();

            var raw = GetProperty(PropertyString.ShadowgainPrunedSkills);

            if (string.IsNullOrWhiteSpace(raw))
                return pruned;

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var id) && Enum.IsDefined(typeof(Skill), id))
                    pruned.Add((Skill)id);
            }

            return pruned;
        }

        public void SetPrunedSkills(HashSet<Skill> pruned)
        {
            if (pruned == null || pruned.Count == 0)
                RemoveProperty(PropertyString.ShadowgainPrunedSkills);
            else
                SetProperty(PropertyString.ShadowgainPrunedSkills, string.Join(",", pruned.Select(s => (int)s).OrderBy(i => i)));

            ChangesDetected = true;
        }

        public bool IsSkillPruned(Skill skill) => GetPrunedSkills().Contains(skill);

        /// <summary>
        /// Shadowgain 093: bring a pruned skill back. Identical to the spec-to-trained demote - set
        /// Trained and recompute the rank from the XP that was preserved - so it shares that
        /// implementation deliberately rather than keeping a second copy that could drift.
        /// </summary>
        public void RestoreSkillToTrained(CreatureSkill creatureSkill) => DemoteSkillToTrained(creatureSkill);

        public bool TrainSkill(Skill skill)
        {
            // get the amount of skill credits required to train this skill
            if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
            {
                log.Error($"{Name}.TrainSkill({skill}) - couldn't find skill base");
                return false;
            }

            // attempt to train the specified skill
            return TrainSkill(skill, GetTrainingCost(skillBase));
        }

        /// <summary>
        /// Sets the skill to trained status for a character
        /// </summary>
        public bool TrainSkill(Skill skill, int creditsSpent, bool applyCreationBonusXP = false)
        {
            var creatureSkill = GetCreatureSkill(skill);

            if (creatureSkill.AdvancementClass >= SkillAdvancementClass.Trained || creditsSpent > AvailableSkillCredits)
                return false;

            // Shadowgain 093: RE-TRAINING A PRUNED SKILL is a restore, not a fresh train. The XP was
            // frozen when the player pruned it, so the rank is recomputed from that rather than the
            // skill being reset to zero - which is what makes pruning "always free and fully
            // reversible, nothing lost". Must come before the reset below, or the freeze is pointless.
            var prunedSkills = GetPrunedSkills();

            if (prunedSkills.Remove(skill))
            {
                SetPrunedSkills(prunedSkills);

                RestoreSkillToTrained(creatureSkill);

                AvailableSkillCredits -= creditsSpent;      // zero while training is free

                if (IsSkillSpecializedViaAugmentation(skill, out var hasAug) && hasAug)
                    SpecializeSkill(skill, 0, false);

                return true;
            }

            creatureSkill.AdvancementClass = SkillAdvancementClass.Trained;
            creatureSkill.Ranks = 0;
            creatureSkill.InitLevel = 0;

            if (applyCreationBonusXP)
            {
                creatureSkill.ExperienceSpent = 526;
                creatureSkill.Ranks = 5;
            }
            else
                creatureSkill.ExperienceSpent = 0;

            AvailableSkillCredits -= creditsSpent;

            // Tinkering skills can be reset at Asheron's Castle and Enlightenment, so if player has the augmentation when they train the skill again immediately specialize it again.
            if (IsSkillSpecializedViaAugmentation(skill, out var playerHasAugmentation) && playerHasAugmentation)
                SpecializeSkill(skill, 0, false);

            return true;
        }

        public bool SpecializeSkill(Skill skill, bool resetSkill = true)
        {
            // get the amount of skill credits required to upgrade this skill
            // from trained -> specialized
            if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
            {
                log.Error($"{Name}.SpecializeSkill({skill}, {resetSkill}) - couldn't find skill base");
                return false;
            }

            // attempt to specialize the specified skill
            return SpecializeSkill(skill, skillBase.UpgradeCostFromTrainedToSpecialized);
        }

        /// <summary>
        /// Sets the skill to specialized status
        /// </summary>
        /// <param name="resetSkill">only set to TRUE during character creation. set to FALSE during temple / asheron's castle</param>
        public bool SpecializeSkill(Skill skill, int creditsSpent, bool resetSkill = true)
        {
            // Shadowgain 013: specialization is gone. Blocked at the single chokepoint every path
            // funnels through - character creation, the skill temple and Asheron's Castle all reach
            // specialization here - so nothing can re-enter spec after the reconcile normalises it.
            if (PropertyManager.GetBool("disable_specialization").Item)
            {
                if (Session != null)
                    Session.Network.EnqueueSend(new GameMessageSystemChat("Specialization is disabled on this world - every skill is Trained, and rises only by use.", ChatMessageType.Broadcast));

                return false;
            }

            var creatureSkill = GetCreatureSkill(skill);

            if (creatureSkill.AdvancementClass != SkillAdvancementClass.Trained || creditsSpent > AvailableSkillCredits)
                return false;

            if (resetSkill)
            {
                // this path only during char creation - a fresh skill, no XP and so no overflow
                creatureSkill.Ranks = 0;
                creatureSkill.ExperienceSpent = 0;
                creatureSkill.InitLevel = 10;
                creatureSkill.AdvancementClass = SkillAdvancementClass.Specialized;
            }
            else
            {
                // this path only during temple / asheron's castle
                PromoteSkillToSpecialized(creatureSkill);
            }

            AvailableSkillCredits -= creditsSpent;

            return true;
        }

        /// <summary>
        /// Shadowgain 090 item 1: the ONE way an EXISTING skill goes Trained -> Specialized.
        ///
        /// The exact mirror of DemoteSkillToTrained, and shared for the same reason - the two
        /// promote sites (the Temple / Asheron's Castle branch of SpecializeSkill, and the
        /// skill-specializing AUGMENTATION) had identical copies of this logic and identical bugs.
        ///
        /// **The bug being fixed.** Both hard-set `InitLevel = 10` and recomputed the rank with the
        /// CAPPED CalcSkillRank. Under 005 a skill ground past the top of the dat table carries its
        /// overflow ranks in InitLevel, so specializing such a skill overwrote the overflow with a
        /// flat 10 and clamped the rank - silently deleting every rank earned past the cap. The
        /// augmentation's own `// handle overages?` comment IS this bug.
        ///
        /// Uses the same overflow-aware shape AddSkillXp uses: compute uncapped, and past the table
        /// top pin Ranks at the table maximum and carry the remainder in InitLevel on top of the
        /// specialization's base 10. The client clamps Ranks at its own table max but honours
        /// InitLevel, which is why the overflow has to live there to be visible at all.
        ///
        /// Rank is recomputed from ExperienceSpent rather than carried across, which is correct:
        /// rank is a function of XP and curve, and the specialized curve is cheaper, so the same XP
        /// legitimately buys more ranks once specialized.
        /// </summary>
        public void PromoteSkillToSpecialized(CreatureSkill creatureSkill)
        {
            creatureSkill.AdvancementClass = SkillAdvancementClass.Specialized;

            var uncapped = PropertyManager.GetBool("skill_uncap_ranks").Item;

            // 109: from the TRUE total, not the uint shadow - a skill past 4,294,967,295 would
            // otherwise re-derive from a clamped number and lose every overflow rank on specializing
            var computedRank = uncapped
                ? CalcSkillRankUncapped(SkillAdvancementClass.Specialized, creatureSkill.TrueExperienceSpent)
                : CalcSkillRank(SkillAdvancementClass.Specialized, creatureSkill.ExperienceSpent);

            var specTable = GetSkillXPTable(SkillAdvancementClass.Specialized);

            var tableMaxRank = specTable != null ? specTable.Count - 1 : computedRank;

            if (uncapped && computedRank > tableMaxRank)
            {
                creatureSkill.Ranks = (ushort)tableMaxRank;
                creatureSkill.InitLevel = 10u + (uint)(computedRank - tableMaxRank);
            }
            else
            {
                creatureSkill.Ranks = (ushort)computedRank;
                creatureSkill.InitLevel = 10;
            }
        }

        /// <summary>
        /// Shadowgain 090 item 2: the ONE way a skill goes Specialized -> Trained.
        ///
        /// Both callers - the login/creation reconcile (DemoteSpecializedSkills) and the Temple's
        /// Gem of Forgetfulness (UnspecializeSkill) - route through here so they cannot drift apart.
        /// They had drifted: the reconcile preserved ranks while the Temple wiped them, and a player
        /// who unspecialized lost every rank ground into the skill with no warning (found on TEST,
        /// Two Handed Combat rank 39 -> 0).
        ///
        /// Two subtleties, both load-bearing:
        ///
        /// 1. **InitLevel is not purely the spec bonus.** SpecializeSkill sets it to 10, but 005 also
        ///    uses InitLevel to carry rank OVERFLOW past the top of the dat table. So this subtracts
        ///    the spec bonus rather than zeroing the field, which would silently erase overflow.
        ///
        /// 2. **Rank is recomputed from XP on the trained curve** (Chris, 2026-08-11) - it is NOT
        ///    carried across, and the XP is NOT topped up.
        ///
        /// That second point replaced the original "keep the rank, top the XP up to match" rule,
        /// which was farmable. Specializing recomputes rank on the CHEAPER specialized curve, so the
        /// rank jumps; topping XP up on the way back made that jump permanent, and the credit refund
        /// made the round trip free. Simulated against the real dat curves, five spec/unspec cycles
        /// took a skill from rank 100 to 226 - the table maximum - with no XP earned and no credits
        /// spent:
        ///
        ///     100 -> spec 113 -> keep 113 -> spec 131 -> keep 131 -> spec 159 -> ... -> 226
        ///
        /// Recomputing closes it by construction: 100 -> spec 113 -> trained 100. Nothing is
        /// confiscated - the player keeps every point of XP they earned. What they give up is the
        /// specialization BONUS, which is precisely what specialization is, and which they are
        /// choosing to sell back for the credits.
        ///
        /// The gain RATE needs nothing here: spec_gain_multiplier is read live off AdvancementClass
        /// (Proficiency.cs:108), so it reverts to 1.0x the moment the class changes.
        /// </summary>
        public void DemoteSkillToTrained(CreatureSkill creatureSkill)
        {
            creatureSkill.AdvancementClass = SkillAdvancementClass.Trained;

            var uncapped = PropertyManager.GetBool("skill_uncap_ranks").Item;

            // 109: from the TRUE total - see PromoteSkillToSpecialized
            var computedRank = uncapped
                ? CalcSkillRankUncapped(SkillAdvancementClass.Trained, creatureSkill.TrueExperienceSpent)
                : CalcSkillRank(SkillAdvancementClass.Trained, creatureSkill.ExperienceSpent);

            var trainedTable = GetSkillXPTable(SkillAdvancementClass.Trained);

            var tableMaxRank = trainedTable != null ? trainedTable.Count - 1 : computedRank;

            // past the table top the remainder lives in InitLevel, whose base is 0 when Trained -
            // the +10 specialization bonus is exactly what is being given up here
            if (uncapped && computedRank > tableMaxRank)
            {
                creatureSkill.Ranks = (ushort)tableMaxRank;
                creatureSkill.InitLevel = (uint)(computedRank - tableMaxRank);
            }
            else
            {
                creatureSkill.Ranks = (ushort)computedRank;
                creatureSkill.InitLevel = 0;
            }
        }

        /// <summary>
        /// Sets the skill to untrained status
        /// </summary>
        public bool UntrainSkill(Skill skill, int creditsSpent)
        {
            var creatureSkill = GetCreatureSkill(skill);

            if (creatureSkill == null || creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized)
                return false;

            if (creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
            {
                // only used to initialize untrained skills for character creation?
                creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;       // should this always be Untrained? what about Inactive?
                creatureSkill.InitLevel = 0;
                creatureSkill.Ranks = 0;
                creatureSkill.ExperienceSpent = 0;
            }
            else
            {
                // Shadowgain 090 item 2: NO XP REFUND. Pooled experience is already astronomical
                // here (nine figures on a levelled character) and buys nothing but augmentations, so
                // the refund was never a reward - it was just a second currency leaking out of a
                // skill reset. Removed at the source rather than special-cased.

                // An always-trained skill cannot be untrained at all; retail's consolation prize was
                // recovering the XP, and with no refund that leaves this doing nothing but wiping
                // ranks for free. Refuse rather than destroy progress in exchange for nothing.
                if (!IsSkillUntrainable(skill))
                {
                    if (Session != null)
                        Session.Network.EnqueueSend(new GameMessageSystemChat($"{skill.ToSentence()} cannot be untrained, and there is no experience to recover - training costs nothing on this world.", ChatMessageType.Broadcast));

                    return false;
                }

                // Shadowgain 093: DELIBERATE PRUNE. This replaces the temporary refusal added in 096
                // item 4, which existed only because untraining was a pure-loss button - the reconcile
                // re-trained the skill at the next login and every rank was gone.
                //
                // Recording the skill here is what makes the difference: EnsureAllSkillsTrained and
                // the sg-reconcile-skills sweep both skip anything on this list, so the prune
                // survives. Ranks and XP are FROZEN, not discarded, so re-training restores the skill
                // exactly as it was. Always free, fully reversible, nothing lost.
                //
                // No exploit surface: no XP reaches the pool, no credits move, ranks do not rise. The
                // only effect is a shorter VTank buff cycle at the cost of use-gain on that skill -
                // the player's chosen tradeoff.
                if (PropertyManager.GetBool("all_skills_trained").Item)
                {
                    var pruned = GetPrunedSkills();
                    pruned.Add(skill);
                    SetPrunedSkills(pruned);

                    creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;
                    creatureSkill.InitLevel = 0;

                    // Ranks and ExperienceSpent are deliberately left ALONE - that is the freeze.

                    // Deliberately silent. The CALLER announces the result - the Gem of Forgetfulness
                    // sends retail's own "You have succeeded in untraining your <skill> skill!"
                    // (WeenieErrorWithString.YouHaveSucceededUntraining_Skill). Chris asked for that
                    // original wording kept rather than replaced or crowded by a second line.

                    return true;
                }

                creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;
                creatureSkill.InitLevel = 0;
                AvailableSkillCredits += creditsSpent;

                // Ranks and XP ARE discarded here, deliberately - this is the one place progress is
                // lost, and it is the designed deterrent: untraining refunds nothing (training was
                // free) and costs every rank ground into the skill, so there is nothing to farm.
                // Contrast DemoteSkillToTrained, where the player keeps what they earned.
                creatureSkill.Ranks = 0;
                creatureSkill.ExperienceSpent = 0;
            }

            return true;
        }

        /// <summary>
        /// Lowers a skill from Specialized back to Trained, refunding the specialization CREDITS.
        ///
        /// Shadowgain 090 item 2. Stock ACE refunded the credits AND the invested XP, then zeroed
        /// Ranks and ExperienceSpent - so unspecializing wiped every rank the player had ground into
        /// the skill. On a server where skills rise only by use, that is the player's entire
        /// investment, and it happened silently: the client shows attribute-derived skill VALUE, so
        /// a level-275 character losing 39 ranks still reads plausibly. Caught on TEST by checking
        /// the database rather than the panel.
        ///
        /// Now: no XP refund, no wipe. The rank stands, the XP is made consistent with it under the
        /// trained curve, and only the credits come back - because credits are the only thing
        /// specialization ever cost.
        /// </summary>
        public bool UnspecializeSkill(Skill skill, int creditsSpent)
        {
            var creatureSkill = GetCreatureSkill(skill);

            if (creatureSkill == null || creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                return false;

            // Skills specialized through an AUGMENTATION cannot be lowered here - the augmentation
            // still grants it, so the class would simply be restored. Retail's fallback was
            // recovering the XP; with no XP refund that leaves nothing but a rank wipe, so refuse
            // outright instead of taking the ranks and giving nothing back.
            if (IsSkillSpecializedViaAugmentation(skill, out var playerHasAugmentation) && playerHasAugmentation)
            {
                if (Session != null)
                    Session.Network.EnqueueSend(new GameMessageSystemChat($"{skill.ToSentence()} is specialized by an augmentation and cannot be lowered here.", ChatMessageType.Broadcast));

                return false;
            }

            DemoteSkillToTrained(creatureSkill);

            AvailableSkillCredits += creditsSpent;

            return true;
        }

        /// <summary>
        /// Increases a skill by some amount of points
        /// </summary>
        public void AwardSkillPoints(Skill skill, uint amount)
        {
            var creatureSkill = GetCreatureSkill(skill);

            for (var i = 0; i < amount; i++)
            {
                // get skill xp required for next rank
                var xpToNextRank = GetXpToNextRank(creatureSkill);

                if (xpToNextRank != null)
                    AwardSkillXP(skill, xpToNextRank.Value);
                else
                    return;
            }
        }

        /// <summary>
        /// Wrapper method used for increasing totalXP and then using the amount granted by HandleActionRaiseSkill
        /// </summary>
        public void AwardSkillXP(Skill skill, uint amount, bool alertPlayer = false)
        {
            var playerSkill = GetCreatureSkill(skill);

            if (playerSkill.AdvancementClass < SkillAdvancementClass.Trained || playerSkill.IsMaxRank)
                return;

            amount = Math.Min(amount, playerSkill.ExperienceLeft);

            GrantXP(amount, XpType.Emote, ShareType.None);
            var raiseChain = new ActionChain();
            raiseChain.AddDelayForOneTick();
            raiseChain.AddAction(this, () =>
            {
                HandleActionRaiseSkill(skill, amount);
            });
            raiseChain.EnqueueChain();

            if (alertPlayer)
                Session.Network.EnqueueSend(new GameMessageSystemChat($"You've earned {amount:N0} experience in your {playerSkill.Skill.ToSentence()} skill.", ChatMessageType.Broadcast));
        }

        public void SpendAllAvailableSkillXp(CreatureSkill creatureSkill, bool sendNetworkUpdate = true)
        {
            var amountRemaining = creatureSkill.ExperienceLeft;

            if (amountRemaining > AvailableExperience)
                amountRemaining = (uint)AvailableExperience;

            SpendSkillXp(creatureSkill, amountRemaining, sendNetworkUpdate);
        }

        /// <summary>
        /// Grants skill XP proportional to the player's skill level
        /// </summary>
        public void GrantLevelProportionalSkillXP(Skill skill, double percent, long min, long max)
        {
            var creatureSkill = GetCreatureSkill(skill, false);
            if (creatureSkill == null || creatureSkill.IsMaxRank)
                return;

            var nextLevelXP = GetXPBetweenSkillLevels(creatureSkill.AdvancementClass, creatureSkill.Ranks, creatureSkill.Ranks + 1);
            if (nextLevelXP == null)
                return;

            var amount = (uint)Math.Round(nextLevelXP.Value * percent);

            if (max > 0 && max <= uint.MaxValue)
                amount = Math.Min(amount, (uint)max);

            amount = Math.Min(amount, creatureSkill.ExperienceLeft);

            if (min > 0)
                amount = Math.Max(amount, (uint)min);

            //Console.WriteLine($"{Name}.GrantLevelProportionalSkillXP({skill}, {percent}, {max:N0})");
            //Console.WriteLine($"Amount: {amount:N0}");

            AwardSkillXP(skill, amount, true);
        }

        /// <summary>
        /// Returns the remaining XP required to the next skill level
        /// </summary>
        public uint? GetXpToNextRank(CreatureSkill skill)
        {
            if (skill.AdvancementClass < SkillAdvancementClass.Trained || skill.IsMaxRank)
                return null;

            var skillXPTable = GetSkillXPTable(skill.AdvancementClass);

            return skillXPTable[skill.Ranks + 1] - skill.ExperienceSpent;
        }

        /// <summary>
        /// Returns the XP curve table based on trained or specialized skill
        /// </summary>
        public static List<uint> GetSkillXPTable(SkillAdvancementClass status)
        {
            var xpTable = DatManager.PortalDat.XpTable;

            switch (status)
            {
                case SkillAdvancementClass.Trained:
                    return xpTable.TrainedSkillXpList;

                case SkillAdvancementClass.Specialized:
                    return xpTable.SpecializedSkillXpList;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns the skill XP required to go between fromRank and toRank
        /// </summary>
        public ulong? GetXPBetweenSkillLevels(SkillAdvancementClass status, int fromRank, int toRank)
        {
            var skillXPTable = GetSkillXPTable(status);
            if (skillXPTable == null)
                return null;

            return skillXPTable[toRank] - skillXPTable[fromRank];
        }

        /// <summary>
        /// Shadowgain 108: the INVERSE of CalcSkillRankUncapped - the total experience required to
        /// REACH a given rank. The two must agree EXACTLY, or "@myskills" tells a player they need
        /// 0 more experience for a rank that has not ticked over.
        ///
        /// 109 stopped mirroring the closed-form maths and started SEARCHING the forward function
        /// instead. The mirror was not exact: at growth &gt; 1.0 the Math.Pow/Math.Log pair loses low
        /// bits, and the truncation lands a hair BELOW the threshold - measured 1,741 disagreeing
        /// ranks in the first 5,000 past the wall, off by 1 experience at the near end and by
        /// ~867,000 out where the numbers get large. A binary search over the forward function
        /// cannot drift from it by construction, costs ~63 iterations of cheap arithmetic on a
        /// command a player types, and deletes the duplicated curve entirely.
        ///
        /// Returns null when the rank is genuinely unreachable - which now means the 64-bit store,
        /// or the ushort the client's rank field is, rather than the uint packet ceiling 109 removed.
        /// </summary>
        public static ulong? CalcSkillXpForRank(SkillAdvancementClass sac, int rank)
        {
            var rankXpTable = GetSkillXPTable(sac);

            if (rankXpTable == null || rankXpTable.Count < 2 || rank < 0)
                return null;

            var topRank = rankXpTable.Count - 1;

            if (rank <= topRank)
                return rankXpTable[rank];

            if (!PropertyManager.GetBool("skill_uncap_ranks").Item)
                return null;        // past the table and uncapping is off: no such rank exists

            if (CalcSkillRankUncapped(sac, MaxTrueSkillXp) < rank)
                return null;

            // smallest xp whose rank is >= the one asked for
            ulong lo = rankXpTable[topRank];
            ulong hi = MaxTrueSkillXp;

            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;

                if (CalcSkillRankUncapped(sac, mid) >= rank)
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return lo;
        }

        /// <summary>
        /// Shadowgain 109: the largest true skill experience that can be STORED.
        ///
        /// PropertyInt64 is signed, so this is long.MaxValue rather than ulong's. It is not a design
        /// ceiling in any meaningful sense - at the flat overcap cost it is ~9.2 trillion ranks - it
        /// exists so accumulation can never wrap into a negative and re-derive a nonsense rank.
        /// </summary>
        public const ulong MaxTrueSkillXp = long.MaxValue;

        /// <summary>
        /// Shadowgain 109b: how many of the table's own final steps are averaged to get the ratio
        /// the curve continues at. The trained table is geometric to six decimal places, so any
        /// window gives 1.078750; the specialized table's tail is noisier (single-step ratios swing
        /// 1.078 - 1.128), and averaging is what stops one ragged step from setting the slope of
        /// every rank thereafter.
        /// </summary>
        private const int OvercapRatioWindow = 20;

        private static readonly Dictionary<SkillAdvancementClass, (double LastStep, double Ratio)> overcapCurves = new();

        /// <summary>
        /// Shadowgain 109b: the shape of progression past the top of the dat XP table - which is
        /// simply THE TABLE'S OWN SHAPE, CONTINUED.
        ///
        ///     cost of rank (top + k) = lastTableStep * ratio^k
        ///
        /// Both numbers are read from the dat, so the curve past the table is the same curve as the
        /// table: rank 209 costs 1.079x what rank 208 cost, exactly as rank 208 cost 1.079x rank
        /// 207. There is no seam, nothing to tune, and nothing to get wrong.
        ///
        /// **This is what 005 originally described and could not deliver.** The trained table's
        /// final step is 306,860,483 while the old uint wire format left only 91,147,799 of
        /// headroom above it - the honest next step did not FIT, so anchoring to it yielded zero
        /// extra ranks in a live test. 1,000,000 flat was reverse-engineered to make ~91 ranks fit
        /// the space that existed, and the result was a ~300x COST COLLAPSE at the table top: rank
        /// 208 cost 306,860,483 and rank 209 cost 1,000,000. The grind got two orders of magnitude
        /// EASIER at exactly the point it should have got harder.
        ///
        /// 109 removed the uint ceiling, which removed the reason for the workaround. Chris:
        /// *"they need to be transparent to the user and just appear as a continuation of the first
        /// 299/420, 300 should cost more to reach than 299 and 421 should cost more than 420 took."*
        ///
        /// **This re-derives ranks that only ever existed because of the collapse**, and that was
        /// accepted deliberately (2026-08-13): a skill sitting at the old ceiling drops from rank
        /// 299 to 208, because 91,147,799 of overflow does not buy even one honest 331,024,096 rank.
        /// Nothing at or below the table top moves. The alternative - grandfathering a hidden
        /// per-skill credit - would have made rank stop being a function of XP, so two characters
        /// with identical experience would show different ranks with no way to see why.
        ///
        /// Cached because the dat is immutable at runtime and CalcSkillXpForRank binary-searches
        /// this ~63 times per call.
        /// </summary>
        private static (double LastStep, double Ratio) GetOvercapCurve(SkillAdvancementClass sac)
        {
            lock (overcapCurves)
            {
                if (overcapCurves.TryGetValue(sac, out var cached))
                    return cached;

                var table = GetSkillXPTable(sac);

                var top = table != null ? table.Count - 1 : 0;

                if (table == null || top < 2)
                    return (1.0, 1.0);

                var lastStep = (double)table[top] - table[top - 1];

                // geometric mean over the tail: ratio^n = lastStep / step(top - n)
                var n = Math.Min(OvercapRatioWindow, top - 1);

                var olderStep = (double)table[top - n] - table[top - n - 1];

                var ratio = olderStep > 0 ? Math.Pow(lastStep / olderStep, 1.0 / n) : 1.0;

                // A table that flattens or reverses must not make ranks free or negative, and a
                // ragged one must not make them unreachable. Neither guard fires on the real dat.
                if (double.IsNaN(ratio) || ratio < 1.0) ratio = 1.0;
                if (ratio > 2.0) ratio = 2.0;

                if (lastStep < 1.0) lastStep = 1.0;

                var curve = (lastStep, ratio);

                overcapCurves[sac] = curve;

                return curve;
            }
        }

        /// <summary>
        /// Shadowgain 005: rank from XP, extended PAST the top of the dat XP table. Upstream rank is
        /// a table lookup, so a skill hard-stops at <c>table.Count - 1</c>; for "effectively
        /// unlimited" progression the curve has to continue past the table's end.
        ///
        /// Shadowgain 109: takes the TRUE 64-bit experience. It was a uint, which quietly made this
        /// function the ceiling itself - rank is a function of XP, so an XP limit was a rank limit,
        /// and skills were never actually unlimited. See CreatureSkill.TrueExperienceSpent.
        ///
        /// Shadowgain 109b: ONE region, no seam. The continuation is the table's own final step
        /// compounding at the table's own ratio - see <see cref="GetOvercapCurve"/>. Inverting
        ///
        ///     extra = lastStep * ratio * (ratio^n - 1) / (ratio - 1)
        ///
        /// for n gives the closed form below.
        ///
        /// ATTRIBUTES ARE DELIBERATELY NOT UNCAPPED (Chris, 2026-08-06) - 004's
        /// vitals-follow-attributes math is built on the 190/196 ceilings.
        /// </summary>
        public static int CalcSkillRankUncapped(SkillAdvancementClass sac, ulong xpAmount)
        {
            var rankXpTable = GetSkillXPTable(sac);

            if (rankXpTable == null || rankXpTable.Count < 2)
                return CalcSkillRank(sac, ClampToUint(xpAmount));

            var topRank = rankXpTable.Count - 1;
            var topXp = rankXpTable[topRank];

            if (xpAmount < topXp)
                return CalcSkillRank(sac, (uint)xpAmount);

            var curve = GetOvercapCurve(sac);

            var extra = (double)xpAmount - topXp;

            // first step past the table, already carrying one ratio - rank 209 costs MORE than 208
            var firstStep = curve.LastStep * curve.Ratio;

            var extraRanks = curve.Ratio <= 1.0 + RatioEpsilon
                ? extra / firstStep
                : Math.Log(1.0 + extra * (curve.Ratio - 1.0) / firstStep) / Math.Log(curve.Ratio);

            if (double.IsNaN(extraRanks) || extraRanks < 0)
                extraRanks = 0;

            // Ranks is a ushort on the wire - stay well inside it rather than wrapping
            var total = topRank + (long)extraRanks;

            return (int)Math.Min(total, ushort.MaxValue - 1);
        }

        private const double RatioEpsilon = 0.000001;

        private static uint ClampToUint(ulong value) => value > uint.MaxValue ? uint.MaxValue : (uint)value;

        /// <summary>
        /// Returns the maximum rank that can be purchased with an xp amount
        /// </summary>
        /// <param name="sac">Trained or specialized skill</param>
        /// <param name="xpAmount">The amount of xp used to make the purchase</param>
        public static int CalcSkillRank(SkillAdvancementClass sac, uint xpAmount)
        {
            var rankXpTable = GetSkillXPTable(sac);
            for (var i = rankXpTable.Count - 1; i >= 0; i--)
            {
                var rankAmount = rankXpTable[i];
                if (xpAmount >= rankAmount)
                    return i;
            }
            return -1;
        }

        private const uint magicSkillCheckMargin = 50;

        public bool CanReadScroll(Scroll scroll)
        {
            var power = scroll.Spell.Power;

            // level 1/7/8 scrolls can be learned by anyone?
            if (power < 50 || power >= 300) return true;

            var magicSkill = scroll.Spell.GetMagicSkill();
            var playerSkill = GetCreatureSkill(magicSkill);

            var minSkill = power - magicSkillCheckMargin;

            return playerSkill.AdvancementClass >= SkillAdvancementClass.Trained && playerSkill.Current >= minSkill;
        }

        public void AddSkillCredits(int amount)
        {
            TotalSkillCredits += amount;
            AvailableSkillCredits += amount;

            Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0));

            if (amount > 1)
                SendTransientError($"You have been awarded {amount:N0} additional skill credits.");
            else
                SendTransientError("You have been awarded an additional skill credit.");
        }

        /// <summary>
        /// Called on player login
        /// If a player has any skills trained that require updates from ACE-World-16-Patches,
        /// ensure these updates are installed, and if they aren't, send a helpful message to player with instructions for installation
        /// </summary>
        public void HandleDBUpdates()
        {
            // dirty fighting
            var dfSkill = GetCreatureSkill(Skill.DirtyFighting);
            if (dfSkill.AdvancementClass >= SkillAdvancementClass.Trained)
            {
                foreach (var spellID in SpellExtensions.DirtyFightingSpells)
                {
                    var spell = new Server.Entity.Spell(spellID);
                    if (spell.NotFound)
                    {
                        var actionChain = new ActionChain();
                        actionChain.AddDelaySeconds(3.0f);
                        actionChain.AddAction(this, () =>
                        {
                            Session.Network.EnqueueSend(new GameMessageSystemChat("To install Dirty Fighting, please apply the latest patches from https://github.com/ACEmulator/ACE-World-16PY-Patches", ChatMessageType.Broadcast));
                        });
                        actionChain.EnqueueChain();
                    }
                    break;  // performance improvement: only check first spell
                }
            }

            // void magic
            var voidSkill = GetCreatureSkill(Skill.VoidMagic);
            if (voidSkill.AdvancementClass >= SkillAdvancementClass.Trained)
            {
                foreach (var spellID in SpellExtensions.VoidMagicSpells)
                {
                    var spell = new Server.Entity.Spell(spellID);
                    if (spell.NotFound)
                    {
                        var actionChain = new ActionChain();
                        actionChain.AddDelaySeconds(3.0f);
                        actionChain.AddAction(this, () =>
                        {
                            Session.Network.EnqueueSend(new GameMessageSystemChat("To install Void Magic, please apply the latest patches from https://github.com/ACEmulator/ACE-World-16PY-Patches", ChatMessageType.Broadcast));
                        });
                        actionChain.EnqueueChain();
                    }
                    break;  // performance improvement: only check first spell (measured 102ms to check 75 uncached void spells)
                }
            }

            // summoning
            var summoning = GetCreatureSkill(Skill.Summoning);
            if (summoning.AdvancementClass >= SkillAdvancementClass.Trained)
            {
                uint essenceWCID = 48878;
                var weenie = DatabaseManager.World.GetCachedWeenie(essenceWCID);
                if (weenie == null)
                {
                    var actionChain = new ActionChain();
                    actionChain.AddDelaySeconds(3.0f);
                    actionChain.AddAction(this, () =>
                    {
                        Session.Network.EnqueueSend(new GameMessageSystemChat("To install Summoning, please apply the latest patches from https://github.com/ACEmulator/ACE-World-16PY-Patches", ChatMessageType.Broadcast));
                    });
                    actionChain.EnqueueChain();
                }
            }
        }

        public static HashSet<Skill> MeleeSkills = new HashSet<Skill>()
        {
            Skill.LightWeapons,
            Skill.HeavyWeapons,
            Skill.FinesseWeapons,
            Skill.DualWield,
            Skill.TwoHandedCombat,

            // legacy
            Skill.Axe,
            Skill.Dagger,
            Skill.Mace,
            Skill.Spear,
            Skill.Staff,
            Skill.Sword,
            Skill.UnarmedCombat
        };

        public static HashSet<Skill> MissileSkills = new HashSet<Skill>()
        {
            Skill.MissileWeapons,

            // legacy
            Skill.Bow,
            Skill.Crossbow,
            Skill.Sling,
            Skill.ThrownWeapon
        };

        public static HashSet<Skill> MagicSkills = new HashSet<Skill>()
        {
            Skill.CreatureEnchantment,
            Skill.ItemEnchantment,
            Skill.LifeMagic,
            Skill.VoidMagic,
            Skill.WarMagic
        };

        public static List<Skill> AlwaysTrained = new List<Skill>()
        {
            Skill.ArcaneLore,
            Skill.Jump,
            Skill.Loyalty,
            Skill.MagicDefense,
            Skill.Run,
            Skill.Salvaging
        };

        public static List<Skill> AugSpecSkills = new List<Skill>()
        {
            Skill.ArmorTinkering,
            Skill.ItemTinkering,
            Skill.MagicItemTinkering,
            Skill.WeaponTinkering,
            Skill.Salvaging
        };

        public static bool IsSkillUntrainable(Skill skill)
        {
            return !AlwaysTrained.Contains(skill);
        }

        public bool IsSkillSpecializedViaAugmentation(Skill skill, out bool playerHasAugmentation)
        {
            playerHasAugmentation = false;

            switch (skill)
            {
                case Skill.ArmorTinkering:
                    playerHasAugmentation = AugmentationSpecializeArmorTinkering > 0;
                    break;

                case Skill.ItemTinkering:
                    playerHasAugmentation = AugmentationSpecializeItemTinkering > 0;
                    break;

                case Skill.MagicItemTinkering:
                    playerHasAugmentation = AugmentationSpecializeMagicItemTinkering > 0;
                    break;

                case Skill.WeaponTinkering:
                    playerHasAugmentation = AugmentationSpecializeWeaponTinkering > 0;
                    break;

                case Skill.Salvaging:
                    playerHasAugmentation = AugmentationSpecializeSalvaging > 0;
                    break;
            }

            return AugSpecSkills.Contains(skill);
        }

        public override bool GetHeritageBonus(WorldObject weapon)
        {
            if (weapon == null || !weapon.IsMasterable)
                return false;

            if (PropertyManager.GetBool("universal_masteries").Item)
            {
                // https://asheron.fandom.com/wiki/Spring_2014_Update
                // end of retail - universal masteries
                return true;
            }
            else
                return GetHeritageBonus(GetWeaponType(weapon));
        }

        public bool GetHeritageBonus(WeaponType weaponType)
        {
            switch (HeritageGroup)
            {
                case HeritageGroup.Aluvian:
                    if (weaponType == WeaponType.Dagger || weaponType == WeaponType.Bow)
                        return true;
                    break;
                case HeritageGroup.Gharundim:
                    if (weaponType == WeaponType.Staff || weaponType == WeaponType.Magic)
                        return true;
                    break;
                case HeritageGroup.Sho:
                    if (weaponType == WeaponType.Unarmed || weaponType == WeaponType.Bow)
                        return true;
                    break;
                case HeritageGroup.Viamontian:
                    if (weaponType == WeaponType.Sword || weaponType == WeaponType.Crossbow)
                        return true;
                    break;
                case HeritageGroup.Shadowbound: // umbraen
                case HeritageGroup.Penumbraen:
                    if (weaponType == WeaponType.Unarmed || weaponType == WeaponType.Crossbow)
                        return true;
                    break;
                case HeritageGroup.Gearknight:
                    if (weaponType == WeaponType.Mace || weaponType == WeaponType.Crossbow)
                        return true;
                    break;
                case HeritageGroup.Undead:
                    if (weaponType == WeaponType.Axe || weaponType == WeaponType.Thrown)
                        return true;
                    break;
                case HeritageGroup.Empyrean:
                    if (weaponType == WeaponType.Sword || weaponType == WeaponType.Magic)
                        return true;
                    break;
                case HeritageGroup.Tumerok:
                    if (weaponType == WeaponType.Spear || weaponType == WeaponType.Thrown)
                        return true;
                    break;
                case HeritageGroup.Lugian:
                    if (weaponType == WeaponType.Axe || weaponType == WeaponType.Thrown)
                        return true;
                    break;
                case HeritageGroup.Olthoi:
                case HeritageGroup.OlthoiAcid:
                    break;
            }
            return false;
        }

        /// <summary>
        /// If the WeaponType is missing from a weapon, tries to convert from WeaponSkill (for old data)
        /// </summary>
        public WeaponType GetWeaponType(WorldObject weapon)
        {
            if (weapon == null)
                return WeaponType.Undef;    // unarmed?

            if (weapon is Caster)
                return WeaponType.Magic;

            var weaponType = weapon.GetProperty(PropertyInt.WeaponType);
            if (weaponType != null)
                return (WeaponType)weaponType;

            var weaponSkill = weapon.GetProperty(PropertyInt.WeaponSkill);
            if (weaponSkill != null && SkillToWeaponType.TryGetValue((Skill)weaponSkill, out WeaponType converted))
                return converted;
            else
                return WeaponType.Undef;
        }

        public static Dictionary<Skill, WeaponType> SkillToWeaponType = new Dictionary<Skill, WeaponType>()
        {
            { Skill.UnarmedCombat, WeaponType.Unarmed },
            { Skill.Sword, WeaponType.Sword },
            { Skill.Axe, WeaponType.Axe },
            { Skill.Mace, WeaponType.Mace },
            { Skill.Spear, WeaponType.Spear },
            { Skill.Dagger, WeaponType.Dagger },
            { Skill.Staff, WeaponType.Staff },
            { Skill.Bow, WeaponType.Bow },
            { Skill.Crossbow, WeaponType.Crossbow },
            { Skill.ThrownWeapon, WeaponType.Thrown },
            { Skill.TwoHandedCombat, WeaponType.TwoHanded },
            { Skill.CreatureEnchantment, WeaponType.Magic },    // only for war/void?
            { Skill.ItemEnchantment, WeaponType.Magic },
            { Skill.LifeMagic, WeaponType.Magic },
            { Skill.WarMagic, WeaponType.Magic },
            { Skill.VoidMagic, WeaponType.Magic },
        };

        public void HandleSkillCreditRefund()
        {
            if (!(GetProperty(PropertyBool.UntrainedSkills) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your trained skills have been reset due to an error with skill credits.\nYou have received a refund for these skill credits and experience.", ChatMessageType.Broadcast));

                RemoveProperty(PropertyBool.UntrainedSkills);
            });
            actionChain.EnqueueChain();
        }

        public void HandleSkillSpecCreditRefund()
        {
            if (!(GetProperty(PropertyBool.UnspecializedSkills) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your specialized skills have been unspecialized due to an error with skill credits.\nYou have received a refund for these skill credits and experience.", ChatMessageType.Broadcast));

                RemoveProperty(PropertyBool.UnspecializedSkills);
            });
            actionChain.EnqueueChain();
        }

        public void HandleFreeSkillResetRenewal()
        {
            if (!(GetProperty(PropertyBool.FreeSkillResetRenewed) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your opportunity to change your skills is renewed! Visit Fianhe to reset your skills.", ChatMessageType.Magic));

                RemoveProperty(PropertyBool.FreeSkillResetRenewed);

                QuestManager.Erase("UsedFreeSkillReset");
            });
            actionChain.EnqueueChain();
        }

        public void HandleFreeAttributeResetRenewal()
        {
            if (!(GetProperty(PropertyBool.FreeAttributeResetRenewed) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                // Your opportunity to change your attributes is renewed! Visit Chafulumisa to reset your skills [sic attributes].
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your opportunity to change your attributes is renewed! Visit Chafulumisa to reset your attributes.", ChatMessageType.Magic));

                RemoveProperty(PropertyBool.FreeAttributeResetRenewed);

                QuestManager.Erase("UsedFreeAttributeReset");
            });
            actionChain.EnqueueChain();
        }

        public void HandleSkillTemplesReset()
        {
            if (!(GetProperty(PropertyBool.SkillTemplesTimerReset) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The Temples of Forgetfulness and Enlightenment have had the timer for their use reset due to skill changes.", ChatMessageType.Magic));

                RemoveProperty(PropertyBool.SkillTemplesTimerReset);

                QuestManager.Erase("ForgetfulnessGems1");
                QuestManager.Erase("ForgetfulnessGems2");
                QuestManager.Erase("ForgetfulnessGems3");
                QuestManager.Erase("ForgetfulnessGems4");
                QuestManager.Erase("Forgetfulness6days");
                QuestManager.Erase("Forgetfulness13days");
                QuestManager.Erase("Forgetfulness20days");
            });
            actionChain.EnqueueChain();
        }

        public void HandleFreeMasteryResetRenewal()
        {
            if (!(GetProperty(PropertyBool.FreeMasteryResetRenewed) ?? false)) return;

            var actionChain = new ActionChain();
            actionChain.AddDelaySeconds(5.0f);
            actionChain.AddAction(this, () =>
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("Your opportunity to change your Masteries is renewed!", ChatMessageType.Magic));

                RemoveProperty(PropertyBool.FreeMasteryResetRenewed);

                QuestManager.Erase("UsedFreeMeleeMasteryReset");
                QuestManager.Erase("UsedFreeRangedMasteryReset");
                QuestManager.Erase("UsedFreeSummoningMasteryReset");
            });
            actionChain.EnqueueChain();
        }

        /// <summary>
        /// Resets a skill.
        ///
        /// **Shadowgain 095h - two callers, opposite intents, now made explicit.**
        ///
        /// - `EmoteType.UntrainSkill` (EmoteManager) - **Fianhe at Asheron's Castle**, a respec. Chris,
        ///   2026-08-11: *"This one needs to be the same as temples."* It now DELEGATES to the very
        ///   methods the Temple gems use, rather than carrying its own copy of the logic, so the two
        ///   doors cannot drift apart. Duplicated demote logic between the reconcile and the Temple
        ///   is exactly what silently destroyed a real skill's ranks earlier the same day.
        /// - `Enlightenment.RemoveSkills` - a full character reset back to level 1, where wiping every
        ///   skill IS the point. That path passes `fullWipe: true` and keeps the old destructive
        ///   behaviour.
        ///
        /// The old shared implementation gave Fianhe the wipe as well, so one door preserved ranks
        /// and the other ate them.
        /// </summary>
        public bool ResetSkill(Skill skill, bool refund = true, bool fullWipe = false)
        {
            var creatureSkill = GetCreatureSkill(skill, false);

            if (creatureSkill == null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
                return false;

            if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)creatureSkill.Skill, out var skillBase) || skillBase == null)
                return false;

            if (!fullWipe)
            {
                // RESPEC. Route through the Temple's own methods - UnspecializeSkill recomputes the
                // rank from XP on the trained curve and refunds the specialization credits;
                // UntrainSkill refuses while all_skills_trained is on, because the reconcile would
                // re-train the skill anyway and the only effect would be discarding ranks.
                bool changed;

                if (creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized)
                    changed = UnspecializeSkill(skill, skillBase.UpgradeCostFromTrainedToSpecialized);
                else
                    changed = UntrainSkill(skill, GetTrainingCost(skillBase));

                if (!changed)
                    return false;   // the callee has already told the player why

                if (Session != null)
                {
                    Session.Network.EnqueueSend(
                        new GameMessagePrivateUpdateSkill(this, creatureSkill),
                        new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0),
                        // Retail's own wording for both outcomes, matching what the Temple gems send -
                        // this path is a respec and should not announce itself differently.
                        new GameEventWeenieErrorWithString(Session,
                            creatureSkill.AdvancementClass == SkillAdvancementClass.Trained
                                ? WeenieErrorWithString.YouHaveSucceededUnspecializing_Skill
                                : WeenieErrorWithString.YouHaveSucceededUntraining_Skill,
                            skill.ToSentence()));
                }

                return true;
            }

            // ---- FULL WIPE (Enlightenment only) ----

            // salvage / tinkering skills specialized via augmentations
            // Salvaging cannot be untrained or unspecialized => skillIsSpecializedViaAugmentation && !untrainable
            IsSkillSpecializedViaAugmentation(creatureSkill.Skill, out var skillIsSpecializedViaAugmentation);

            var typeOfSkill = creatureSkill.AdvancementClass.ToString().ToLower() + " ";
            var untrainable = IsSkillUntrainable(skill);
            var creditRefund = (creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized && !(skillIsSpecializedViaAugmentation && !untrainable)) || untrainable;

            if (creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized && !(skillIsSpecializedViaAugmentation && !untrainable))
            {
                creatureSkill.AdvancementClass = SkillAdvancementClass.Trained;
                creatureSkill.InitLevel = 0;
                if (!skillIsSpecializedViaAugmentation) // Tinkering skills can be unspecialized, but do not refund upgrade cost.
                    AvailableSkillCredits += skillBase.UpgradeCostFromTrainedToSpecialized;
            }

            if (untrainable)
            {
                creatureSkill.AdvancementClass = SkillAdvancementClass.Untrained;
                creatureSkill.InitLevel = 0;

                // Shadowgain 090 item 5: refund exactly what was PAID, which is zero while training
                // is free. Enlightenment overwrites AvailableSkillCredits wholesale straight after
                // this anyway, but leaving the leak in would make this method wrong on its own terms.
                AvailableSkillCredits += GetTrainingCost(skillBase);
            }

            // Shadowgain 090 item 2: no XP refund, ever.

            creatureSkill.ExperienceSpent = 0;
            creatureSkill.Ranks = 0;

            var updateSkill = new GameMessagePrivateUpdateSkill(this, creatureSkill);
            var availableSkillCredits = new GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, AvailableSkillCredits ?? 0);

            var specCreditsReturned = creditRefund && creatureSkill.AdvancementClass == SkillAdvancementClass.Trained;

            var msg = $"Your {(untrainable ? $"{typeOfSkill}" : "")}{skill.ToSentence()} skill has been {(untrainable ? "removed" : "reset")}, and its ranks are gone. ";
            msg += specCreditsReturned
                ? "Your specialization credits have been refunded. Training itself costs nothing on this world, so there is nothing else to return."
                : "Training costs nothing on this world, so there is nothing to refund.";

            if (refund)
                Session.Network.EnqueueSend(updateSkill, availableSkillCredits, new GameMessageSystemChat(msg, ChatMessageType.Broadcast));
            else
                Session.Network.EnqueueSend(updateSkill, new GameMessageSystemChat(msg, ChatMessageType.Broadcast));

            return true;
        }

        /// <summary>
        /// All of the skills players have access to @ end of retail
        /// </summary>
        public static HashSet<Skill> PlayerSkills = new HashSet<Skill>()
        {
            Skill.MeleeDefense,
            Skill.MissileDefense,
            Skill.ArcaneLore,
            Skill.MagicDefense,
            Skill.ManaConversion,
            Skill.ItemTinkering,
            Skill.AssessPerson,
            Skill.Deception,
            Skill.Healing,
            Skill.Jump,
            Skill.Lockpick,
            Skill.Run,
            Skill.AssessCreature,
            Skill.WeaponTinkering,
            Skill.ArmorTinkering,
            Skill.MagicItemTinkering,
            Skill.CreatureEnchantment,
            Skill.ItemEnchantment,
            Skill.LifeMagic,
            Skill.WarMagic,
            Skill.Leadership,
            Skill.Loyalty,
            Skill.Fletching,
            Skill.Alchemy,
            Skill.Cooking,
            Skill.Salvaging,
            Skill.TwoHandedCombat,
            Skill.VoidMagic,
            Skill.HeavyWeapons,
            Skill.LightWeapons,
            Skill.FinesseWeapons,
            Skill.MissileWeapons,
            Skill.Shield,
            Skill.DualWield,
            Skill.Recklessness,
            Skill.SneakAttack,
            Skill.DirtyFighting,
            Skill.Summoning
        };
    }
}
