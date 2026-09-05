using System.ComponentModel;

namespace ACE.Entity.Enum.Properties
{
    // No properties are sent to the client unless they featured an attribute.
    // SendOnLogin gets sent to players in the PlayerDescription event
    // AssessmentProperty gets sent in successful appraisal
    public enum PropertyBool : ushort
    {
        Undef                            = 0,
        [Ephemeral]
        Stuck                            = 1,
        [AssessmentProperty][Ephemeral]
        Open                             = 2,
        [AssessmentProperty]
        Locked                           = 3,
        RotProof                         = 4,
        AllegianceUpdateRequest          = 5,
        AiUsesMana                       = 6,
        AiUseHumanMagicAnimations        = 7,
        AllowGive                        = 8,
        CurrentlyAttacking               = 9,
        AttackerAi                       = 10,
        IgnoreCollisions                 = 11,
        ReportCollisions                 = 12,
        Ethereal                         = 13,
        GravityStatus                    = 14,
        LightsStatus                     = 15,
        ScriptedCollision                = 16,
        Inelastic                        = 17,
        [Ephemeral]
        Visibility                       = 18,
        Attackable                       = 19,
        SafeSpellComponents              = 20,
        [SendOnLogin]
        AdvocateState                    = 21,
        Inscribable                      = 22,
        DestroyOnSell                    = 23,
        UiHidden                         = 24,
        IgnoreHouseBarriers              = 25,
        HiddenAdmin                      = 26,
        PkWounder                        = 27,
        PkKiller                         = 28,
        NoCorpse                         = 29,
        UnderLifestoneProtection         = 30,
        ItemManaUpdatePending            = 31,
        [Ephemeral]
        GeneratorStatus                  = 32,
        [Ephemeral]
        ResetMessagePending              = 33,
        DefaultOpen                      = 34,
        DefaultLocked                    = 35,
        DefaultOn                        = 36,
        OpenForBusiness                  = 37,
        IsFrozen                         = 38,
        DealMagicalItems                 = 39,
        LogoffImDead                     = 40,
        ReportCollisionsAsEnvironment    = 41,
        AllowEdgeSlide                   = 42,
        AdvocateQuest                    = 43,
        [SendOnLogin][Ephemeral]
        IsAdmin                          = 44,
        [SendOnLogin][Ephemeral]
        IsArch                           = 45,
        [SendOnLogin][Ephemeral]
        IsSentinel                       = 46,
        [SendOnLogin]
        IsAdvocate                       = 47,
        CurrentlyPoweringUp              = 48,
        [Ephemeral]
        GeneratorEnteredWorld            = 49,
        NeverFailCasting                 = 50,
        VendorService                    = 51,
        AiImmobile                       = 52,
        DamagedByCollisions              = 53,
        IsDynamic                        = 54,
        IsHot                            = 55,
        IsAffecting                      = 56,
        AffectsAis                       = 57,
        SpellQueueActive                 = 58,
        [Ephemeral]
        GeneratorDisabled                = 59,
        IsAcceptingTells                 = 60,
        LoggingChannel                   = 61,
        OpensAnyLock                     = 62,
        [AssessmentProperty]
        UnlimitedUse                     = 63,
        GeneratedTreasureItem            = 64,
        IgnoreMagicResist                = 65,
        IgnoreMagicArmor                 = 66,
        AiAllowTrade                     = 67,
        [SendOnLogin]
        SpellComponentsRequired          = 68,
        [AssessmentProperty]
        IsSellable                       = 69,
        IgnoreShieldsBySkill             = 70,
        NoDraw                           = 71,
        ActivationUntargeted             = 72,
        HouseHasGottenPriorityBootPos    = 73,
        [Ephemeral]
        GeneratorAutomaticDestruction    = 74,
        HouseHooksVisible                = 75,
        HouseRequiresMonarch             = 76,
        HouseHooksEnabled                = 77,
        HouseNotifiedHudOfHookCount      = 78,
        AiAcceptEverything               = 79,
        IgnorePortalRestrictions         = 80,
        RequiresBackpackSlot             = 81,
        DontTurnOrMoveWhenGiving         = 82,
        NpcLooksLikeObject               = 83,
        IgnoreCloIcons                   = 84,
        [AssessmentProperty]
        AppraisalHasAllowedWielder       = 85,
        ChestRegenOnClose                = 86,
        LogoffInMinigame                 = 87,
        PortalShowDestination            = 88,
        PortalIgnoresPkAttackTimer       = 89,
        NpcInteractsSilently             = 90,
        [AssessmentProperty]
        Retained                         = 91,
        IgnoreAuthor                     = 92,
        Limbo                            = 93,
        [AssessmentProperty]
        AppraisalHasAllowedActivator     = 94,
        ExistedBeforeAllegianceXpChanges = 95,
        IsDeaf                           = 96,
        [SendOnLogin][Ephemeral]
        IsPsr                            = 97,
        Invincible                       = 98,
        [AssessmentProperty]
        Ivoryable                        = 99,
        [AssessmentProperty]
        Dyable                           = 100,
        CanGenerateRare                  = 101,
        CorpseGeneratedRare              = 102,
        NonProjectileMagicImmune         = 103,
        [SendOnLogin]
        ActdReceivedItems                = 104,
        Unknown105                       = 105,
        [Ephemeral]
        FirstEnterWorldDone              = 106,
        RecallsDisabled                  = 107,
        [AssessmentProperty]
        RareUsesTimer                    = 108,
        ActdPreorderReceivedItems        = 109,
        [Ephemeral]
        Afk                              = 110,
        IsGagged                         = 111,
        ProcSpellSelfTargeted            = 112,
        IsAllegianceGagged               = 113,
        EquipmentSetTriggerPiece         = 114,
        Uninscribe                       = 115,
        WieldOnUse                       = 116,
        ChestClearedWhenClosed           = 117,
        NeverAttack                      = 118,
        SuppressGenerateEffect           = 119,
        TreasureCorpse                   = 120,
        EquipmentSetAddLevel             = 121,
        BarberActive                     = 122,
        TopLayerPriority                 = 123,
        [SendOnLogin]
        NoHeldItemShown                  = 124,
        [SendOnLogin]
        LoginAtLifestone                 = 125,
        OlthoiPk                         = 126,
        [SendOnLogin]
        Account15Days                    = 127,
        HadNoVitae                       = 128,
        NoOlthoiTalk                     = 129,
        [AssessmentProperty]
        AutowieldLeft                    = 130,

        /* Custom Properties */
        LinkedPortalOneSummon            = 9001,
        LinkedPortalTwoSummon            = 9002,
        HouseEvicted                     = 9003,
        UntrainedSkills                  = 9004,
        [Ephemeral]
        IsEnvoy                          = 9005,
        UnspecializedSkills              = 9006,
        FreeSkillResetRenewed            = 9007,
        FreeAttributeResetRenewed        = 9008,
        SkillTemplesTimerReset           = 9009,
        FreeMasteryResetRenewed          = 9010,

        // Shadowgain 021: two-speed progression. 9100+ is reserved for this fork so it
        // cannot collide with future upstream additions in the 9000 range.
        //
        // ShadowgainFastPath        - the character's CURRENT lane (true = fast).
        // ShadowgainForfeitedMarker - the one-way ratchet. Set the instant the fast lane
        //                             is first chosen and NEVER cleared, even if the
        //                             player returns to the slow lane. This is what makes
        //                             the marker mean "never took the shortcut" rather
        //                             than "is not taking it right now".
        ShadowgainFastPath               = 9101,
        ShadowgainForfeitedMarker        = 9102,

        // Shadowgain 213: marks an armour-set applicator produced by the TIER 2 extractor, which may be
        // applied to ANY coverage rather than only the donor's own.
        //
        // It has to be a STORED flag rather than something derived. 209 encodes an applicator's coverage
        // in its WEENIE - GetArmorWCID picks the applicator wcid FROM the donor's ValidLocations, and
        // Apply re-derives it by asking the same question of the target. A tier-2 applicator is built the
        // same way and is therefore indistinguishable from a tier-1 one by inspection. Without this flag
        // the only way to let tier 2 cross coverage would be to drop the check for EVERY applicator,
        // which would silently loosen 209 - the one thing 213 must not do.
        ShadowgainAnyCoverageApplicator  = 9103,

        // Shadowgain 224: mule mode - the one-way combat brick a character accepts at The
        // Muleskinner. While set, attacking refuses (CanDamage) and the spellbook no longer
        // channels War, Void or Life (CreatePlayerSpell); item casts, Creature/Item magic and
        // defenses stay live. Set once, never cleared - the free max-mule package (Str 290,
        // pack/burden augs) is the trade and the trade does not come back off.
        ShadowgainMuleMode               = 9104,

        // Shadowgain 224: weenie marker for the NPC offering the mule conversion. Lives on the
        // NPC's weenie in the world DB, never on a player - Creature.ActOnUse routes a marked
        // NPC to the hard-confirm dialog instead of emote chatter.
        ShadowgainMuleTrainer            = 9105,
    }
}
