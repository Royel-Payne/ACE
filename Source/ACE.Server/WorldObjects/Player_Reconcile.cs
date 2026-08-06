using log4net;

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
            var demoted = DemoteSpecializedSkills();
            var attrs = NormalizeAttributeStartingValues();
            var credits = ZeroUnspendableSkillCredits();

            if (trained > 0 || demoted > 0 || attrs > 0 || credits > 0)
            {
                log.Info($"[SHADOWGAIN 013] {Name} reconciled{(atCreation ? " at creation" : " at login")}: " +
                         $"trained={trained} despecialized={demoted} attributesReset={attrs} creditsCleared={credits}");
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
        public int DemoteSpecializedSkills()
        {
            if (!PropertyManager.GetBool("disable_specialization").Item)
                return 0;

            var demoted = 0;

            foreach (var skill in SkillHelper.ValidSkills)
            {
                var creatureSkill = GetCreatureSkill(skill);

                if (creatureSkill == null || creatureSkill.AdvancementClass != SkillAdvancementClass.Specialized)
                    continue;

                creatureSkill.AdvancementClass = SkillAdvancementClass.Trained;

                // strip only the specialization bonus, preserving any 005 overflow carried here
                creatureSkill.InitLevel = creatureSkill.InitLevel >= 10 ? creatureSkill.InitLevel - 10 : 0;

                // keep the rank; make the XP consistent with it under the trained table
                var trainedTable = GetSkillXPTable(SkillAdvancementClass.Trained);

                if (trainedTable != null && creatureSkill.Ranks < trainedTable.Count)
                {
                    var costOfRank = trainedTable[creatureSkill.Ranks];

                    if (creatureSkill.ExperienceSpent < costOfRank)
                        creatureSkill.ExperienceSpent = costOfRank;
                }

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
        public int ZeroUnspendableSkillCredits()
        {
            if (!PropertyManager.GetBool("all_skills_trained").Item || !PropertyManager.GetBool("disable_specialization").Item)
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
        /// The uniform attribute starting value under 013. Not a config dial: the vitals re-base and
        /// AttributeMaxRanks both key off it, and letting it drift would silently change the ceiling.
        /// </summary>
        public const uint AttributeStartingValue = 10;
    }
}
