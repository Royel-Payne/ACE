using ACE.Server.Managers;

namespace ACE.Server.Physics.Common
{
    public class EncumbranceSystem
    {
        public static int EncumbranceCapacity(int strength, int numAugs)
        {
            if (strength <= 0) return 0;

            var bonusBurden = 30 * numAugs;

            int capacity;

            if (bonusBurden >= 0)
            {
                if (bonusBurden > 150)
                    bonusBurden = 150;

                capacity = 150 * strength + strength * bonusBurden;
            }
            else
                capacity = 150 * strength;

            // Shadowgain 009: Strength-independent capacity floor.
            //
            // Capacity is purely 150 x Strength upstream, which was fine when anyone could buy
            // Strength with pooled XP. Under usage-based gain a caster who never melees would be
            // stuck at their starting Strength - and therefore permanently unable to carry their own
            // loot. The floor guarantees a workable minimum while Strength still governs everything
            // above it, so a warrior still out-carries a mage by a wide margin.
            //
            // Applied here rather than at the call sites so the burden penalty, the client's
            // "overburdened by N" readout, and 009's Strength gain all agree on one number.
            // ADDITIVE, not max(). A max() floor would create a dead zone - with a floor of 5000,
            // Strength 10 and Strength 33 both yield exactly 5000, so raising Strength buys a weak
            // character nothing at all until they pass the floor. Adding it instead means Strength
            // always matters, there is no flat region, and the strong stay far ahead:
            //   Str 10  ->  1,500 + floor
            //   Str 100 -> 15,000 + floor
            if (PropertyManager.GetBool("burden_capacity_floor_enabled").Item)
                capacity += (int)PropertyManager.GetLong("burden_capacity_floor").Item;

            return capacity;
        }

        public static float GetBurden(int capacity, int encumbrance)
        {
            if (capacity <= 0) return 3.0f;

            if (encumbrance >= 0)
                return (float)encumbrance / capacity;
            else
                return 0.0f;
        }

        public static float GetBurdenMod(float burden)
        {
            if (burden < 1.0f) return 1.0f;

            if (burden < 2.0f)
                return 2.0f - burden;
            else
                return 0.0f;
        }
    }
}
