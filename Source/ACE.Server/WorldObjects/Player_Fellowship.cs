using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameEvent.Events;
using ACE.Server.Network.GameMessages.Messages;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        ///  The fellowship that this player belongs to
        /// </summary>
        public Fellowship Fellowship;

        public bool FellowVitalUpdate;

        /// <summary>
        /// Shadowgain 204: the fellowship skill-gain buff, CACHED.
        ///
        /// Unifying levels killed the reason to fellowship: the point of one was sharing KILL xp, and
        /// kill xp no longer drives level. This restores an incentive to group WITHOUT restoring a
        /// share - it multiplies the earner's OWN skill and attribute award and never pools or
        /// transfers anything, so nobody levels off someone else's practice. That is the failure 193
        /// Bug 6 and 201 both exist to prevent, and a share would have walked straight back into it.
        ///
        /// WHY THIS IS A CACHED FIELD AND NOT A FUNCTION CALL. The consumer is
        /// Proficiency.OnSuccessUse, which fires per swing, per evade and per cast - 171 measured
        /// 154,437 melee evades in NINE HOURS from one skill on one character. Fellowship.WithinRange
        /// walks every fellow and does a distance calculation, which is fine once per kill in SplitXp
        /// and is emphatically not fine at that frequency. So the scan runs on the heartbeat and the
        /// hot path reads a double.
        /// </summary>
        public double FellowshipGainMultiplier { get; private set; } = 1.0;

        /// <summary>
        /// Shadowgain 204: recompute the cached buff. Called from Heartbeat - see the note above on
        /// why it is not called from the award site.
        /// </summary>
        public void RefreshFellowshipGainMultiplier()
        {
            FellowshipGainMultiplier = ComputeFellowshipGainMultiplier();
        }

        private double ComputeFellowshipGainMultiplier()
        {
            if (!PropertyManager.GetBool("fellowship_gain_enabled").Item)
                return 1.0;

            var fellowship = Fellowship;

            if (fellowship == null || Session == null)
                return 1.0;

            var requireDistinctAccount = PropertyManager.GetBool("fellowship_gain_require_distinct_account").Item;

            // n = REAL co-located fellows besides me. All three guards must hold, and each kills a
            // specific way of faking a group: WithinRange kills nominal membership from across the
            // world, the account check kills a fellowship of your own mules, and the access-level
            // check keeps admin characters from inflating anyone.
            var n = 0;

            foreach (var fellow in fellowship.WithinRange(this))
            {
                if (fellow == null || fellow == this || fellow.Session == null)
                    continue;

                if (fellow.Session.AccessLevel != AccessLevel.Player)
                    continue;

                if (requireDistinctAccount && fellow.Session.AccountId == Session.AccountId)
                    continue;

                n++;
            }

            if (n <= 0)
                return 1.0;

            var cap = PropertyManager.GetDouble("fellowship_gain_max_bonus").Item;

            if (double.IsNaN(cap) || cap <= 0.0)
                return 1.0;

            // The curve is anchored on the game's own ceiling rather than a number of our choosing, so
            // raising MaxFellows re-balances it automatically instead of leaving the top end unpriced.
            var full = Fellowship.MaxFellows - 1;

            if (n > full)
                n = full;

            var decay = PropertyManager.GetDouble("fellowship_gain_decay").Item;

            double bonus;

            if (double.IsNaN(decay) || decay <= 0.0 || decay >= 1.0 || full <= 0)
            {
                // Degenerate settings get a straight line rather than a divide-by-zero. decay >= 1
                // would make the denominator zero; decay <= 0 would make every group size identical.
                bonus = full > 0 ? cap * n / full : cap;
            }
            else
            {
                bonus = cap * (1.0 - Math.Pow(decay, n)) / (1.0 - Math.Pow(decay, full));
            }

            if (double.IsNaN(bonus) || bonus <= 0.0)
                return 1.0;

            return 1.0 + bonus;
        }

        // todo: Figure out if this is the best place to do this, and whether there are concurrency issues associated with it.
        public void FellowshipCreate(string fellowshipName, bool shareXP)
        {
            // An Olthoi player cannot create a fellowship
            if (IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.OlthoiCannotJoinFellowship));
                return;
            }

            Fellowship = new Fellowship(this, fellowshipName, shareXP);
            Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(Session));
            Session.Network.EnqueueSend(new GameEventFellowshipFellowUpdateDone(Session));
        }

        public void HandleActionFellowshipChangeOpenness(bool openness)
        {
            if (Fellowship != null)
            {
                if (Guid.Full != Fellowship.FellowshipLeaderGuid)
                {
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
                    return;
                }

                if (!Fellowship.IsLocked)
                    Fellowship.UpdateOpenness(openness);
                else
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.FellowshipIsLocked));
            }
        }

        public void HandleActionFellowshipChangeLock(bool lockState, string lockName)
        {
            if (Fellowship != null)
                Fellowship.UpdateLock(lockState, lockName);
        }

        public void FellowshipQuit(bool disband)
        {
            if (Fellowship != null)
                Fellowship.QuitFellowship(this, disband);
        }

        public void FellowshipDismissPlayer(uint dismissGuid)
        {
            if (Fellowship == null) return;

            if (Guid.Full != Fellowship.FellowshipLeaderGuid)
            {
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
                return;
            }

            if (Guid.Full == dismissGuid)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("You can't dismiss yourself from the fellowship", ChatMessageType.Broadcast));
                return;
            }

            var fellowToDismiss = PlayerManager.GetOnlinePlayer(dismissGuid);

            if (fellowToDismiss == null)
                return;

            Fellowship.RemoveFellowshipMember(fellowToDismiss, this);
        }

        public void FellowshipRecruit(Player newPlayer)
        {
            if (newPlayer == null) return;

            // An Olthoi player cannot join a fellowship
            if (newPlayer.IsOlthoiPlayer)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat("The Olthoi's hunger for destruction is too great to understand a request for fellowship.", ChatMessageType.Broadcast));
                SendWeenieError(WeenieError.None);
                return;
            }

            if (newPlayer.GetCharacterOption(CharacterOption.IgnoreFellowshipRequests))
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{newPlayer.Name} is not accepting fellowship requests.", ChatMessageType.Fellowship));                
                Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.FellowshipIgnoringRequests));
            }
            else if (Fellowship != null)
            {
                if (Guid.Full == Fellowship.FellowshipLeaderGuid || Fellowship.Open)
                    Fellowship.AddFellowshipMember(this, newPlayer);
                else
                    Session.Network.EnqueueSend(new GameEventWeenieError(Session, WeenieError.YouMustBeLeaderOfFellowship));
            }
        }

        public void FellowshipNewLeader(uint newLeaderGuid)
        {
            if (Fellowship == null || Guid.Full == newLeaderGuid)
                return;

            if (Guid.Full != Fellowship.FellowshipLeaderGuid)
            {
                log.Warn($"{Name} tried to assign new fellowship leader from {Fellowship.FellowshipLeaderGuid:X8} to {newLeaderGuid:X8}");
                return;
            }

            var newLeader = PlayerManager.GetOnlinePlayer(newLeaderGuid);

            if (newLeader == null)
                return;

            if (newLeader.Fellowship != Fellowship)
            {
                Session.Network.EnqueueSend(new GameMessageSystemChat($"{newLeader.Name} is not a member of the fellowship!", ChatMessageType.Broadcast));
                return;
            }

            Fellowship.AssignNewLeader(this, newLeader);
        }

        public bool FellowshipPanelOpen { get; set; }

        /// <summary>
        /// Called when player opens / closes the fellowship panel
        /// </summary>
        public void HandleFellowshipUpdateRequest(bool panelOpen)
        {
            FellowshipPanelOpen = panelOpen;

            if (Fellowship != null && FellowshipPanelOpen)
                Session.Network.EnqueueSend(new GameEventFellowshipFullUpdate(Session));
        }
    }
}
