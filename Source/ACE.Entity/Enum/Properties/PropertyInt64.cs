using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyInt64 : ushort
    {
        Undef                 = 0,
        [SendOnLogin]
        TotalExperience       = 1,
        [SendOnLogin]
        AvailableExperience   = 2,
        [AssessmentProperty]
        AugmentationCost      = 3,
        [AssessmentProperty]
        ItemTotalXp           = 4,
        [AssessmentProperty]
        ItemBaseXp            = 5,
        [SendOnLogin]
        AvailableLuminance    = 6,
        [SendOnLogin]
        MaximumLuminance      = 7,
        InteractionReqs       = 8,

        /* Custom Properties */
        AllegianceXPCached    = 9000,
        AllegianceXPGenerated = 9001,
        AllegianceXPReceived  = 9002,
        VerifyXp              = 9003,

        // Shadowgain 109: per-skill OVERFLOW experience, one property per skill at
        // ShadowgainSkillXpBase + (int)Skill. 9100-9199 is reserved for it, matching the 9100+
        // convention PropertyBool and PropertyString already use for this fork.
        //
        // Not declared per skill, because the key is computed from the Skill enum - a member per
        // skill would be 40-odd entries that could silently disagree with that enum. Casting an
        // undeclared value is safe here: biota_properties_int64.type is a smallint unsigned, and
        // NOTHING in this range reaches the client - GameEventPlayerDescription filters int64s
        // through the SendOnLogin allowlist, and none of these carry that attribute.
        //
        // The Skill enum is 55 members (max id 54, Summoning), so the 100 reserved slots leave room
        // for upstream to add another 45 before this would collide with anything.
        //
        // Only written when a skill's true experience EXCEEDS uint.MaxValue; see
        // CreatureSkill.TrueExperienceSpent for why absence is the meaningful default.
        ShadowgainSkillXpBase = 9100,
    }
}
