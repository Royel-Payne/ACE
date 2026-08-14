using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Shadowgain 021: two opt-in progression lanes.
        ///
        /// The default (hard) lane is a deliberate multi-year climb; the fast lane trades that
        /// calendar time for the name mark and the honour roll, permanently.
        /// (The mark is progression_marker_prefix - a dagger since 023, and live-configurable, so it
        /// is named by role rather than by glyph here.)
        ///
        /// WHY BOTH SKILL GAIN *AND* KILL XP ARE SCALED TOGETHER
        ///
        /// Cowork's refinement, verified in code before building: character level grants NO power
        /// in Shadowgain. Every read of Level is content-gating, death cost, vitae severity,
        /// housing or allegiance - never damage, defence, vitals or skill. Since 013 disabled
        /// XP-spend, 004 tied vitals to attributes, and skills come only from use, scaling
        /// xp_modifier alone would produce a level-200 character with beginner skills.
        ///
        /// So a lane's speed multiplies BOTH, in proportion:
        ///   - skill and attribute gain -> the character's actual power
        ///   - kill XP                  -> the level number, so it stays an honest proxy and
        ///                                 content-gates unlock in step
        ///
        /// Modelled on the real tables (tools/pacing): time to level 275 scales LINEARLY with the
        /// combined multiplier, and final weapon skill / Strength land on identical values at every
        /// multiplier - 351 / 263. Both lanes arrive at the same power; only the calendar differs.
        /// That is what makes the marker honest: it marks the journey, not the destination.
        ///
        ///     x1   18,510 h   17.8 years      (today's pace)
        ///     x9    2,057 h    2.0 years      hard lane
        ///     x100    185 h    2.1 months     fast lane
        /// </summary>
        public bool ShadowgainFastPath
        {
            get => GetProperty(PropertyBool.ShadowgainFastPath) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ShadowgainFastPath); else SetProperty(PropertyBool.ShadowgainFastPath, value); }
        }

        /// <summary>
        /// The one-way ratchet. Set the first time the fast lane is chosen and never cleared -
        /// returning to the slow lane restores nothing. Without this, a player races ahead on fast
        /// and toggles back to reclaim the marker, and the marker means nothing.
        /// </summary>
        public bool ShadowgainForfeitedMarker
        {
            get => GetProperty(PropertyBool.ShadowgainForfeitedMarker) ?? false;
            set { if (!value) RemoveProperty(PropertyBool.ShadowgainForfeitedMarker); else SetProperty(PropertyBool.ShadowgainForfeitedMarker, value); }
        }

        /// <summary>
        /// TRUE only for a character that has NEVER touched the fast lane. Drives the name-mark
        /// prefix and honour-roll eligibility. Note this is deliberately not "is currently on the
        /// hard lane" - see the ratchet above.
        /// </summary>
        public bool IsMasochist => !ShadowgainForfeitedMarker;

        /// <summary>
        /// The speed multiplier for this character's current lane. Applied to skill gain, attribute
        /// gain and kill XP alike. Both dials are live-tunable - Chris specifically wanted the hard
        /// lane adjustable once real players reach the deep end.
        /// </summary>
        public double ProgressionSpeed
        {
            get
            {
                var key = ShadowgainFastPath ? "progression_speed_fast" : "progression_speed_hard";

                var speed = PropertyManager.GetDouble(key).Item;

                return speed > 0.0 ? speed : 1.0;
            }
        }

        /// <summary>
        /// Switches lane. Returns false if this was a no-op.
        ///
        /// Choosing fast trips the ratchet immediately and permanently. Choosing hard afterwards is
        /// allowed as a gameplay choice, and restores nothing.
        /// </summary>
        public bool SetProgressionLane(bool fast)
        {
            if (ShadowgainFastPath == fast)
                return false;

            ShadowgainFastPath = fast;

            if (fast)
                ShadowgainForfeitedMarker = true;    // never cleared

            ChangesDetected = true;

            // the mark is part of the name, so everyone nearby needs to be told it changed
            EnqueueBroadcast(new Network.GameMessages.Messages.GameMessagePublicUpdatePropertyString(this, PropertyString.Name, Name));

            return true;
        }
    }
}
