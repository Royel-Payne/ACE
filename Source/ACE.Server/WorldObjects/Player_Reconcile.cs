using log4net;

using ACE.DatLoader;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Shadowgain 013 - Greylock's pure vision: everything Trained, nothing Specialized, every
        /// attribute starting at 10 and reaching the same ceiling through use alone.
        ///
        /// Runs on enter-world, so it covers new AND existing characters with no reroll. Every part
        /// is idempotent and behind its own toggle, and **earned ranks are never taken away** - the
        /// reconcile only ever normalises the things a player did not earn (advancement class,
        /// creation attribute allocation, unspendable credits).
        ///
        /// This supersedes the earlier keep-the-credit-economy decision. It became safe once the 002
        /// client crash was exonerated as a local Decal fault, 007 gave every skill a usage path, and
        /// 004+ gave every attribute one.
        /// </summary>
        public void ShadowgainReconcile(bool atCreation = false)
        {
            var trained = EnsureAllSkillsTrained();
            var demoted = DemoteSpecializedSkills(atCreation);
            var attrs = NormalizeAttributeStartingValues();
            var credits = ZeroUnspendableSkillCredits(atCreation);

            // 090: LAST, deliberately. It reads the current specialization set to work out what has
            // been spent, so it has to run after DemoteSpecializedSkills has had its say - otherwise
            // at creation it would bill the character for a specialization that is about to be
            // stripped, and hand them a permanent deficit on their first login.
            var rebuilt = BackfillLevelSkillCredits();

            if (trained > 0 || demoted > 0 || attrs > 0 || credits > 0 || rebuilt > 0)
            {
                log.Info($"[SHADOWGAIN 013] {Name} reconciled{(atCreation ? " at creation" : " at login")}: " +
                         $"trained={trained} despecialized={demoted} attributesReset={attrs} creditsCleared={credits} creditsRebuilt={rebuilt}");
            }
        }

        /// <summary>
        /// Brings every valid skill up to at least Trained, free of skill-credit cost.
        ///
        /// Proficiency.OnSuccessUse requires AdvancementClass >= Trained, so any skill left Untrained
        /// can never rise through use - which is the whole experiment. Ranks and XP are left alone: a
        /// newly trained skill starts at its trained base rather than receiving the creation bonus,
        /// so this grants eligibility, not progress.
        ///
        /// Returns the number newly trained.
        /// </summary>
        public int EnsureAllSkillsTrained()
        {
            if (!PropertyManager.GetBool("all_skills_trained").Item)
                return 0;

            var newlyTrained = 0;

            foreach (var skill in SkillHelper.ValidSkills)
            {
                var creatureSkill = GetCreatureSkill(skill);

                if (creatureSkill == null || creatureSkill.AdvancementClass >= SkillAdvancementClass.Trained)
                    continue;

                // 0 credits spent, and no creation bonus XP - a grant, not a purchase
                if (TrainSkill(skill, 0))
                    newlyTrained++;
            }

            return newlyTrained;
        }

        /// <summary>
        /// Demotes any Specialized skill back to exactly Trained.
        ///
        /// Together with EnsureAllSkillsTrained this gives one flat rule - every skill sits at
        /// exactly Trained - normalising whatever the creation client allowed, since the client still
        /// offers specialization and the server is what decides.
        ///
        /// Two subtleties:
        ///
        /// 1. **InitLevel is not purely the spec bonus.** SpecializeSkill sets it to 10, but 005 also
        ///    uses InitLevel to carry rank OVERFLOW past the top of the dat table. So this subtracts
        ///    the spec bonus rather than zeroing the field, which would silently erase overflow ranks.
        ///
        /// 2. **Ranks are preserved, XP is topped up.** A specialized rank costs less than a trained
        ///    one, so recomputing rank from the same XP under the trained table would LOWER it. Ranks
        ///    are earned, so instead the rank stands and ExperienceSpent is raised to what that rank
        ///    costs when trained. Generous by design - the alternative is confiscating progress.
        /// </summary>
        /// <summary>
        /// 090: same shape as ZeroUnspendableSkillCredits - always at CREATION, at login only while
        /// specialization is disabled. The character editor still offers specialization and the
        /// server is what decides; nobody starts specialized regardless of the toggle.
        ///
        /// Once spec is enabled this must NOT run at login, or it would demote the specializations
        /// players legitimately bought at a Temple.
        /// </summary>
        public int DemoteSpecializedSkills(bool atCreation = false)
        {
            if (!atCreation && !PropertyManager.GetBool("disable_specialization").Item)
                return 0;

            var demoted = 0;

            foreach (var skill in SkillHelper.ValidSkills)
            {
                var creatureSkill = GetCreatureSkill(skill);

                if (creatureSkill == null || creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                    continue;

                // 090 item 2: shared with the Temple's Gem of Forgetfulness, so the two can never
                // disagree about what "specialized -> trained" means again. The rank-preserving,
                // overflow-preserving logic lives in DemoteSkillToTrained.
                DemoteSkillToTrained(creatureSkill);

                demoted++;
            }

            if (demoted > 0)
                ChangesDetected = true;

            return demoted;
        }

        /// <summary>
        /// Shadowgain 013 Part 2: every attribute starts at 10.
        ///
        /// Resets the CREATION allocation only - StartingValue - and never touches earned Ranks,
        /// since attribute value is StartingValue + Ranks and the ranks are the part that was played
        /// for.
        ///
        /// The point is a level playing field: in stock ACE the ceiling is StartingValue + 190 ranks,
        /// so a creation-maxed attribute tops out ~90 points above a dumped one and that gap can
        /// never be closed by playing. With everyone starting at 10 and the rank ceiling raised to
        /// suit (see AttributeMaxRanks), all six attributes reach the same number through use.
        /// </summary>
        public int NormalizeAttributeStartingValues()
        {
            if (!PropertyManager.GetBool("attributes_start_at_ten").Item)
                return 0;

            var reset = 0;

            foreach (var kvp in Attributes)
            {
                var attribute = kvp.Value;

                if (attribute == null || attribute.StartingValue == AttributeStartingValue)
                    continue;

                attribute.StartingValue = AttributeStartingValue;
                reset++;

                if (Session != null)
                    Session.Network.EnqueueSend(new Network.GameMessages.Messages.GameMessagePrivateUpdateAttribute(this, attribute));
            }

            if (reset > 0)
                ChangesDetected = true;

            return reset;
        }

        /// <summary>
        /// Shadowgain 013 Part 3: with every skill auto-trained and specialization gone, skill credits
        /// buy nothing. Zeroing them stops the UI inviting the player to spend on a dead economy.
        ///
        /// Unassigned EXPERIENCE is deliberately left alone - it still has a use, notably
        /// augmentations (AugmentationDevice spends it), which is why 003 stopped short of zeroing it.
        /// </summary>
        /// <summary>
        /// 090: at CREATION this always runs; at login it only runs while specialization is off.
        ///
        /// The old gate was `all_skills_trained &amp;&amp; disable_specialization`, which was correct
        /// while spec was disabled and becomes a trap the moment it is re-enabled: flipping
        /// `disable_specialization` false silently removed the wipe **including at creation**, which
        /// is the one place it is still wanted. Nobody keeps spec or leftover credits from the
        /// character editor - Chris's rule is that specialization is always post-creation, earned at
        /// a Temple.
        ///
        /// At login the wipe must NOT run once spec is enabled, or it would eat the level-up credits
        /// this design depends on.
        /// </summary>
        public int ZeroUnspendableSkillCredits(bool atCreation = false)
        {
            if (!PropertyManager.GetBool("all_skills_trained").Item)
                return 0;

            if (!atCreation && !PropertyManager.GetBool("disable_specialization").Item)
                return 0;

            var credits = AvailableSkillCredits ?? 0;

            if (credits <= 0)
                return 0;

            AvailableSkillCredits = 0;
            ChangesDetected = true;

            if (Session != null)
                Session.Network.EnqueueSend(new Network.GameMessages.Messages.GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, 0));

            return credits;
        }

        /// <summary>
        /// Shadowgain 090: rebuilds a character's skill credits from what they have actually earned.
        ///
        /// Existing characters levelled with credits suppressed at the source
        /// (Player_Xp only grants them when the credit economy is live), so re-enabling
        /// specialization would otherwise strand every current player at zero while new characters
        /// earned normally. This restores what levelling should have paid them.
        ///
        /// **It is a recompute, not a one-shot migration**, and that is the whole design:
        ///
        ///     Total     = level credits + quest credits          (what the character has earned)
        ///     Available = Total - credits spent on specialization  (what is left to spend)
        ///
        /// Both sides are derived from state that is already on the character, so running it a
        /// second time lands on the same numbers. It needs no marker property, it self-heals a
        /// character whose credits drifted for any reason, and it costs nothing to run at every
        /// login. A one-shot migration would have needed a marker, would have been unrepeatable,
        /// and would have left no way to correct a mistake short of hand-editing the database.
        ///
        /// **Heritage credits are excluded on purpose.** The 52 (68 Olthoi) handed out at creation
        /// is considered consumed by all_skills_trained - it is what pays for every skill being
        /// Trained for free. Only level-earned credits fund specialization. That single rule is what
        /// makes "training is free, specialization is the only thing credits buy" hold end to end.
        ///
        /// It BLUNTS the untrain-refund exploit without closing it. UntrainSkill still hands back
        /// the skill's book cost for a skill that cost nothing, and this recompute takes that back
        /// at the next login - but only at the next login. Within one session the credits are real
        /// and spendable, and once they are sunk into a specialization the recompute lands on a
        /// negative Available and clamps to 0, leaving the specializations bought with them intact.
        /// The refund still has to be removed at the source (090 item 5); do not treat this as the
        /// fix.
        ///
        /// Reads the DAT's CharacterLevelSkillCreditList directly rather than ACE's
        /// GetAdditionalCredits helper, which stops at 250 -> 45 and omits the 275 -> 46 row.
        /// Verified against client_portal.dat: sum(1..10) = 9, sum(1..275) = 46.
        ///
        /// Returns 1 if the credits changed, 0 if they were already correct.
        /// </summary>
        public int BackfillLevelSkillCredits()
        {
            if (!PropertyManager.GetBool("skill_credits_from_levels").Item)
                return 0;

            // The heritage exclusion above is only defensible while creation really does train
            // everything for free. Without all_skills_trained the stock economy applies and the
            // 52 is genuinely the player's to spend, so leave it entirely alone.
            if (!PropertyManager.GetBool("all_skills_trained").Item)
                return 0;

            // While specialization is off there is nothing to buy, ZeroUnspendableSkillCredits is
            // wiping the pool at every login by design, and granting credits here would only fight
            // it. The backfill exists for the spec-enabled world.
            if (PropertyManager.GetBool("disable_specialization").Item)
                return 0;

            var earned = GetLevelSkillCredits() + GetQuestSkillCredits();
            var spent = GetSpecializedSkillCredits();

            var total = earned;
            var available = earned - spent;

            if (available < 0)
            {
                // Specialized beyond what levelling funds - only reachable from a grant path
                // outside this accounting (an admin hand-out, a legacy character, an unrecognised
                // credit-awarding quest). The specializations stand and the debt is forgiven: this
                // reconcile never takes away something a player is already using.
                log.Warn($"[SHADOWGAIN 090] {Name} carries {spent} credits of specialization against {earned} earned - " +
                         $"clamping available to 0 rather than demoting anything");

                available = 0;
            }

            var wasTotal = TotalSkillCredits ?? 0;
            var wasAvailable = AvailableSkillCredits ?? 0;

            if (wasTotal == total && wasAvailable == available)
                return 0;

            TotalSkillCredits = total;
            AvailableSkillCredits = available;
            ChangesDetected = true;

            if (Session != null)
            {
                Session.Network.EnqueueSend(
                    new Network.GameMessages.Messages.GameMessagePrivateUpdatePropertyInt(this, PropertyInt.AvailableSkillCredits, available),
                    new Network.GameMessages.Messages.GameMessagePrivateUpdatePropertyInt(this, PropertyInt.TotalSkillCredits, total));
            }

            log.Info($"[SHADOWGAIN 090] {Name} skill credits rebuilt at level {Level ?? 1}: earned={earned} " +
                     $"spentOnSpec={spent} | available {wasAvailable}->{available}, total {wasTotal}->{total}");

            return 1;
        }

        /// <summary>
        /// Total skill credits this character's LEVEL has paid out, straight from the DAT table that
        /// Player_Xp reads at level-up - so a restored character and a levelled one can never
        /// disagree. Heritage and quest credits are not included.
        /// </summary>
        public int GetLevelSkillCredits()
        {
            var creditList = DatManager.PortalDat.XpTable.CharacterLevelSkillCreditList;

            var level = Level ?? 1;

            if (level >= creditList.Count)
                level = creditList.Count - 1;

            var credits = 0;

            // index i is the grant for REACHING level i, matching Player_Xp's post-increment read
            // and /delevel's sum over (delevel..currentLevel]. Level 1 pays nothing.
            for (var i = 1; i <= level; i++)
                credits += (int)creditList[i];

            return credits;
        }

        /// <summary>
        /// The retail quests that hand out a skill credit. Nobody on this server has completed any
        /// of them, so this returns 0 today - but the backfill is a recompute that runs at every
        /// login, and anything it does not count it takes away. Counting them here is what stops
        /// the first player to finish Ralirea's or Oswald's quest from losing the credit the moment
        /// they log back in.
        ///
        /// Same three quest stamps ACE itself reconstructs credits from (Enlightenment.RemoveSkills,
        /// verify-skill-credits).
        /// </summary>
        public int GetQuestSkillCredits()
        {
            if (QuestManager == null)
                return 0;

            return QuestManager.GetCurrentSolves("ArantahKill1")              // Ralirea, level 35
                 + QuestManager.GetCurrentSolves("OswaldManualCompleted")     // Oswald, level 90
                 + QuestManager.GetCurrentSolves("LumAugSkillQuest");         // Luminance, stamped up to twice
        }

        /// <summary>
        /// Credits currently tied up in specializations - the one thing credits buy here.
        ///
        /// Charges exactly what SpecializeSkill charges (UpgradeCostFromTrainedToSpecialized, the
        /// UPGRADE column rather than the full cost) so the recompute always agrees with what the
        /// Temple actually deducted. Using the heritage-adjusted costs verify-skill-credits reads
        /// would be wrong here for the same reason the heritage 52 is excluded: no live code path
        /// charges them.
        /// </summary>
        public int GetSpecializedSkillCredits()
        {
            var spent = 0;

            foreach (var skill in SkillHelper.ValidSkills)
            {
                var creatureSkill = GetCreatureSkill(skill);

                if (creatureSkill == null || creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                    continue;

                // the tinkering/salvaging five can ONLY be specialized by augmentation, which calls
                // SpecializeSkill(skill, 0) and costs no credits. They also carry >= 999 in the dat's
                // spec column, so billing for one would zero a player's pool outright.
                if (IsSkillSpecializedViaAugmentation(skill, out _))
                    continue;

                if (!DatManager.PortalDat.SkillTable.SkillBaseHash.TryGetValue((uint)skill, out var skillBase))
                    continue;

                spent += skillBase.UpgradeCostFromTrainedToSpecialized;
            }

            return spent;
        }

        /// <summary>
        /// The uniform attribute starting value under 013. Not a config dial: the vitals re-base and
        /// AttributeMaxRanks both key off it, and letting it drift would silently change the ceiling.
        /// </summary>
        public const uint AttributeStartingValue = 10;
    }
}
