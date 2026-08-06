using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.Network.GameAction.Actions
{
    public static class GameActionRaiseVital
    {
        [GameAction(GameActionType.RaiseVital)]
        public static void Handle(ClientMessage message, Session session)
        {
            var vital = (PropertyAttribute2nd)message.Payload.ReadUInt32();
            var xpSpent = message.Payload.ReadUInt32();

            // Shadowgain 004: vitals were the last remaining way to buy power with pooled experience.
            //
            //     MaxValue = StartingValue + Ranks + attributeDerivedComponent
            //
            // Ranks are purchased with XP. With skills (003) and attributes (004) both closed, ALL
            // pooled experience would have funnelled here - a player could dump everything into Health
            // and buy tankiness outright while every other stat had to be earned through use. Closing
            // it leaves vitals as a pure consequence of their governing attribute: Endurance drives
            // Health and Stamina, Self drives Mana, and both of those now rise only by use.
            //
            // So Health still grows - you just raise it by taking hits rather than by shopping.
            if (PropertyManager.GetBool("vital_gain_usage_only").Item)
            {
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"{vital.ToSentence()} cannot be raised by spending experience on this world. It grows with the attribute behind it - Endurance for health and stamina, Self for mana - and those rise through use.",
                    ChatMessageType.Advancement));

                return;
            }

            session.Player.HandleActionRaiseVital(vital, xpSpent);
        }
    }
}
