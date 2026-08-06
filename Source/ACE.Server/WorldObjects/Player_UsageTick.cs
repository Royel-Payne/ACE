using System;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        /// <summary>
        /// Shadowgain 008/009: where the player was when a movement-driven award last fired.
        /// Net displacement is measured against THIS, not against the previous heartbeat, which is
        /// what makes running in a tight circle worthless - you have to actually get somewhere.
        /// </summary>
        private ACE.Entity.Position LastUsageTickPosition;

        /// <summary>
        /// Shadowgain 008 + 009: the shared movement tick, called from Heartbeat (~5s).
        ///
        /// Entries 008 (Quickness/Run from travel) and 009 (Strength from hauling while
        /// overburdened) are implemented together because they need identical plumbing: a
        /// net-displacement check that cannot be farmed by standing still or circling.
        /// </summary>
        public void UsageMovementTick()
        {
            var debug = PropertyManager.GetBool("attribute_debug_logging").Item;

            if (Location == null || IsDead)
                return;

            // NOTE: deliberately not gating on Teleporting. PlayerEnterWorld sets it true and only
            // OnTeleportComplete clears it, so a logged-in player can sit with it stuck true - which
            // silently disabled every movement award. Oversized jumps are filtered by distance below,
            // which handles actual teleports anyway.
            if (LastUsageTickPosition == null)
            {
                LastUsageTickPosition = new ACE.Entity.Position(Location);

                if (debug)
                    log.Info($"[MOVETICK] {Name} | anchor set");

                return;
            }

            // Cross-landblock distance is only trustworthy OUTDOORS. Position.DistanceTo falls back to
            // (landblockX - landblockX) * 192 + offset, which assumes the outdoor grid - ACE's own
            // source says "verify this is working correctly if one of these is indoors" and evidently
            // never did. Dungeon landblocks are not on that grid, so a measurement spanning an
            // indoor/outdoor boundary is meaningless, and was producing a large bogus distance that
            // fired an award for merely walking out of a dungeon.
            //
            // Inside a dungeon the landblock does not change, so plain 3D distance applies and travel
            // is credited normally. Outdoor travel across landblocks is fine too. Only the boundary
            // is untrustworthy - there we re-anchor and skip.
            if (Location.LandblockId != LastUsageTickPosition.LandblockId
                && (Location.Indoors || LastUsageTickPosition.Indoors))
            {
                if (debug)
                    log.Info($"[MOVETICK] {Name} | SKIP=indoorBoundary | re-anchoring, cross-landblock distance is unreliable indoors");

                LastUsageTickPosition = new ACE.Entity.Position(Location);
                return;
            }

            var distance = Location.DistanceTo(LastUsageTickPosition);

            // A teleport/recall is not travel. Reset the anchor and award nothing, so portalling
            // around the world cannot be used as a movement grind.
            var maxDistance = PropertyManager.GetDouble("movement_gain_max_distance").Item;

            if (distance > maxDistance)
            {
                if (debug)
                    log.Info($"[MOVETICK] {Name} | SKIP=teleport | distance={distance:N1} > {maxDistance:N1}");

                LastUsageTickPosition = new ACE.Entity.Position(Location);
                return;
            }

            // THE anti-AFK rule. Standing still gives 0 distance; running in a circle returns you to
            // roughly where you started, so displacement stays under the threshold and pays nothing.
            var minDisplacement = PropertyManager.GetDouble("movement_gain_min_displacement").Item;

            if (distance < minDisplacement)
            {
                if (debug)
                    log.Info($"[MOVETICK] {Name} | SKIP=tooClose | distance={distance:N1} < {minDisplacement:N1}");

                return;
            }

            if (debug)
                log.Info($"[MOVETICK] {Name} | AWARD | distance={distance:N1} (min {minDisplacement:N1})");

            LastUsageTickPosition = new ACE.Entity.Position(Location);

            AwardMovementGains();
            AwardOverburdenStrength();
        }

        /// <summary>
        /// Shadowgain 008: travel raises Quickness and the Run skill.
        /// Difficulty is a flat configured value, NOT derived from Quickness or Run - deriving it
        /// from the thing being raised is the 003 Shield runaway. The ratio still self-limits,
        /// because the divisor is the attribute/skill's own Base.
        /// </summary>
        private void AwardMovementGains()
        {
            var difficulty = (uint)Math.Max(1, PropertyManager.GetLong("movement_gain_difficulty").Item);

            AwardAttributeUsageXP(PropertyAttribute.Quickness, difficulty);

            var runSkill = GetCreatureSkill(Skill.Run);

            if (runSkill != null)
                Proficiency.OnSuccessUse(this, runSkill, difficulty);
        }

        /// <summary>
        /// Shadowgain 009: Strength rises ONLY while overburdened - carrying a normal load pays
        /// nothing. You have to actually be over capacity and suffering for it.
        ///
        /// Difficulty is the overburden AMOUNT (units over capacity), not burden percentage.
        /// Percentage would be self-referential: capacity derives from Strength, so a percentage
        /// would shift as Strength changed. The absolute amount is safe, and self-limiting in the
        /// right direction - capacity grows with Strength, so a fixed load overburdens you less
        /// over time and pays less. To keep gaining you must carry more.
        ///
        /// Being overburdened already costs 30% of Run, Jump, Melee Defense and Missile Defense,
        /// so this is a real trade rather than a free passive.
        /// </summary>
        private void AwardOverburdenStrength()
        {
            if (!PropertyManager.GetBool("burden_strength_gain").Item)
                return;

            // Uses the same capacity the game itself uses for the burden penalty, so gain lines up
            // exactly with the state the client reports ("You are currently overburdened by N").
            var capacity = EncumbranceSystem.EncumbranceCapacity((int)Strength.Current, AugmentationIncreasedCarryingCapacity);

            var carried = EncumbranceVal ?? 0;

            if (carried <= capacity)
                return;

            var overburdenUnits = carried - capacity;

            // SCALE CORRECTION. Burden is measured in units of a few thousand, while every other
            // difficulty in the system is a skill/defence value in the tens - melee reads ~72, the
            // movement tick 30. Feeding raw burden in made difficulty ~30x out of scale: a first
            // test paid 4132 xp and took Strength from rank 0 to 10 in ONE tick. The ratio cap
            // cannot save that, because it bounds the multiplier, not the difficulty itself.
            var divisor = Math.Max(1.0, PropertyManager.GetDouble("burden_strength_divisor").Item);

            var overburden = (uint)Math.Max(1, Math.Round(overburdenUnits / divisor));

            AwardAttributeUsageXP(PropertyAttribute.Strength, overburden);
        }
    }
}
