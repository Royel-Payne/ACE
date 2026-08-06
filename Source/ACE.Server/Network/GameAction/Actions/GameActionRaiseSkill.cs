using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionRaiseSkill
    {
        [GameAction(GameActionType.RaiseSkill)]
        public static void Handle(ClientMessage message, Session session)
        {
            var skill = (Skill)message.Payload.ReadUInt32();
            var xpSpent = message.Payload.ReadUInt32();

            // Shadowgain 003: usage is the sole source of skill RANK. Buying ranks with pooled
            // experience is the exact shortcut that makes "skills rise by use" meaningless, so the
            // player-initiated raise is refused here.
            //
            // Gated at the network action rather than inside HandleActionRaiseSkill on purpose:
            // that method is also used by AwardSkillXP for NPC/quest emote rewards, which should
            // keep working. Usage gain does not come through here at all - it writes directly via
            // Player.AwardSkillUsageXP.
            //
            // Note this deliberately does NOT touch GameActionRaiseAttribute. Attributes have no
            // usage-based gain until entry 004, so disabling their spend path now would leave them
            // with no way to rise at all.
            if (PropertyManager.GetBool("skill_gain_usage_only").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Skills rise only through use on this world. Your {skill.ToSentence()} skill cannot be raised by spending experience - go and use it.",
                    ChatMessageType.Advancement));

                return;
            }

            session.Player.HandleActionRaiseSkill(skill, xpSpent);
        }
    }
}
