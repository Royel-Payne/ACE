using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionRaiseAttribute
    {
        [GameAction(GameActionType.RaiseAttribute)]
        public static void Handle(ClientMessage message, Session session)
        {
            var attribute = (PropertyAttribute)message.Payload.ReadUInt32();
            var xpSpent = message.Payload.ReadUInt32();

            // Shadowgain 004: attributes now rise through use, so buying ranks with pooled experience is
            // the same shortcut 003 closed for skills. Entry 003 deliberately left this open, because
            // attributes had no usage gain yet and gating it then would have left them unable to rise at
            // all; 004 adds the gain, so it closes now.
            //
            // Gated at the network action, matching GameActionRaiseSkill - usage gain writes directly via
            // Player.AwardAttributeUsageXP and never passes through here.
            if (PropertyManager.GetBool("attribute_gain_usage_only").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"Attributes rise only through use on this world. Your {attribute} cannot be raised by spending experience - go and earn it.",
                    ChatMessageType.Advancement));

                return;
            }

            session.Player.HandleActionRaiseAttribute(attribute, xpSpent);
        }
    }
}
