using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameMessages.Messages
{
    public class GameMessageCreateObject : GameMessage
    {
        /// <summary>
        /// Shadowgain 041: <paramref name="useBaseName"/> is passed true ONLY when sending a player
        /// their own object, so their client (and therefore Decal/VTank) sees an unmarked name.
        /// Everything else keeps the default and produces the same bytes it always has.
        /// </summary>
        public GameMessageCreateObject(WorldObject worldObject, bool adminvision = false, bool adminnodraw = false, bool useBaseName = false)
            : base(GameMessageOpcode.ObjectCreate, GameMessageGroup.SmartboxQueue)
        {
            worldObject.SerializeCreateObject(Writer, adminvision, adminnodraw, useBaseName);
        }
    }
}
