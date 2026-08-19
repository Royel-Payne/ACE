using System;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Shadowgain 170: bring back a skill you pruned, without going through the client.
    ///
    /// WHY THIS HAS TO EXIST. 093 promises that pruning a skill is "always free, fully reversible,
    /// nothing lost" - the XP is frozen rather than discarded, and re-training restores the skill
    /// exactly as it was. The server honours that: `GetTrainingCost` returns 0 while
    /// `all_skills_trained` is on, and `TrainSkill(skill, 0)` takes the prune branch, restores the
    /// rank from the frozen XP and subtracts zero credits.
    ///
    /// **The reverse was only reachable through the client's Train button, and the client refuses.**
    /// It reads the skill's TrainedCost out of the dat - 6 for Alchemy - and greys the button out
    /// when your available credits are lower, without ever asking the server. Chris, 2026-08-19:
    /// *"click to train it at 0 cost but a skill credit available check blocks the button from being
    /// clickable"*. Apex hit it with 5 credits against Alchemy's 6, and the server logged nothing at
    /// all, because nothing was ever sent.
    ///
    /// So the promise held server-side and was unreachable in practice, for anyone whose credits
    /// happen to sit below the dat price of the thing they pruned. That is not an edge case - it
    /// tightens every time a player specializes something.
    ///
    /// SAFE AT PLAYER TIER, because it can only ever undo something the caller chose:
    ///
    ///   - it restores ONLY skills on the caller's own pruned list, which they put there
    ///   - it charges 0, exactly as the client path would have
    ///   - it grants no rank and no XP: `RestoreSkillToTrained` recomputes the rank from experience
    ///     that was already theirs and frozen at the prune
    ///   - it cannot touch another character
    ///
    /// The Admin equivalent is `@trainskill`, kept as a fail-safe for anything this refuses.
    /// </summary>
    public static class ShadowgainSkillCommands
    {
        [CommandHandler("sg-train", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
            "Re-train a skill you previously untrained. Always free.",
            "[skill name]\n"
            + "  With no argument, lists the skills you have untrained.\n"
            + "  Example: /sg-train Alchemy\n"
            + "  Your ranks and experience were frozen when you untrained it, not lost - this puts\n"
            + "  them back exactly as they were, and costs nothing.")]
        public static void HandleShadowgainTrain(Session session, params string[] parameters)
        {
            var player = session?.Player;

            if (player == null)
                return;

            var pruned = player.GetPrunedSkills();

            if (pruned.Count == 0)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    "You have no untrained skills to restore.", ChatMessageType.Broadcast));
                return;
            }

            // No argument: say what there is to restore, rather than a usage line. A player who has
            // forgotten the exact spelling is the common case, not a mistake to be corrected.
            if (parameters == null || parameters.Length == 0)
            {
                var names = string.Join(", ", pruned.OrderBy(s => s.ToString()).Select(s => s.ToSentence()));

                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Untrained skills you can restore for free: {names}. Use /sg-train <skill name>.",
                    ChatMessageType.Broadcast));
                return;
            }

            // "two handed combat", "TwoHandedCombat" and "Two Handed Combat" all resolve. The enum
            // has no spaces, and asking a player to know that is a support question waiting to happen.
            var wanted = string.Concat(string.Join("", parameters).Where(c => !char.IsWhiteSpace(c)));

            if (!Enum.TryParse(wanted, true, out Skill skill) || !Enum.IsDefined(typeof(Skill), skill))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"'{string.Join(" ", parameters)}' is not a skill name. Use /sg-train on its own to see what you can restore.",
                    ChatMessageType.Broadcast));
                return;
            }

            if (!pruned.Contains(skill))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"You have not untrained {skill.ToSentence()}, so there is nothing to restore.",
                    ChatMessageType.Broadcast));
                return;
            }

            // 0 credits, deliberately: this is the same call the client's Train button would have
            // made, at the same price the server would have charged it.
            if (!player.TrainSkill(skill, 0))
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{skill.ToSentence()} could not be restored. Please report this with /bug.",
                    ChatMessageType.Broadcast));
                return;
            }

            var creatureSkill = player.GetCreatureSkill(skill);

            session.Network.EnqueueSend(
                new GameMessagePrivateUpdateSkill(player, creatureSkill),
                new GameMessagePrivateUpdatePropertyInt(player, PropertyInt.AvailableSkillCredits,
                    player.AvailableSkillCredits ?? 0),
                new GameMessageSystemChat(
                    $"{skill.ToSentence()} restored at rank {creatureSkill.Ranks}. No credits were spent.",
                    ChatMessageType.Advancement));
        }
    }
}
