-- Shadowgain 210: the Aun collectors stop promising a week for a one-hour lockout.
--
-- THE BUG IS THE TEXT, NOT THE TIMER. `craft_quest_lockout_seconds` is 3600 on both worlds and is
-- enforced correctly: QuestManager replaces the quest's own stored MinDelta outright -
--
--     if (craftLockout > 0 && IsCraftTaskQuest(questName))
--         nextSolveTime = playerQuest.LastTimeCompleted + craftLockout;
--
-- - so the 72,000s (20 hour) MinDelta stamped on the Siraluun quests never applies. Chris caught
-- this from the other end: Aun Nireeura refused a Timber hairpin with "return to me in a week",
-- seven seconds after accepting one. The refusal was correct. Only the sentence was wrong.
--
-- AC emote messages are static strings with no templating, so this text cannot follow the dial by
-- itself. It said a week when the lockout was 20 hours too - the dial only widened a gap that was
-- already there. A player who reads it literally waits a week for a one-hour cooldown, which makes
-- the crafting loop look vastly more punishing than it is.
--
-- WHY VAGUE RATHER THAN "AN HOUR". Chris: "gives us flex for rate change". These rates are still
-- being tuned; naming a duration in static text guarantees this file gets rewritten every time the
-- dial moves, and guarantees it is wrong in between. "Later" / "in time" is correct at any setting.
-- The lockout is communicated accurately by the refusal itself, which fires only while it is real.
--
-- SCOPE - four collectors, 32 rows, all category 12 (TestSuccess: the "come back later" line shown
-- after a SUCCESSFUL turn-in). Eight rows each, one per trophy tier. Deliberately NOT included:
--
--   Tiffany Comfore (22074), 10 rows reading "Arwic was not built in a day". That is an idiom
--   about taking care over your craft, not a lockout promise - it names no return time at all.
--   It matched a keyword search for "day" and nothing more. Changing it would have damaged
--   flavour text that was never wrong.
--
-- Aun Ihmenaura's wording named no unit a search could match ("Several passings of the sun") and
-- was found only by reading all four collectors' text. Keyword sweeps miss in-fiction durations.
--
-- WHY THIS FILE EXISTS: this is a world-database edit, and ace_world is REPLACED wholesale by an
-- ACE world DB release. Anything changed by hand is silently gone on the next import with no
-- error and no warning. Re-apply every file in this directory after any world DB update.
--
-- Idempotent: matches both the original and the replacement, and keys on the weenie rather than a
-- row id, so it survives re-import renumbering. Applies at next server restart - ACE caches
-- weenies at startup, so an already-running world keeps the old text until it recycles.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 003-aun-collector-lockout-wording.sql

-- Aun Nireeura (hairpins): "return to me in a week" -> "return to me later"
UPDATE ace_world.weenie_properties_emote_action ea
  JOIN ace_world.weenie_properties_emote em ON em.id = ea.emote_Id
   SET ea.message = 'Your pins are a boon to our xuta. We use the pins we make, as well as the ones you and your xuta have supplied to us, for many tasks. However, we only need so many. Extras would sadly be put to waste. Please return to me later and I may be able to take more pins off of your hands.'
 WHERE em.object_Id = 29859            -- ace29859-aunnireeura
   AND ea.message IN (
         'Your pins are a boon to our xuta. We use the pins we make, as well as the ones you and your xuta have supplied to us, for many tasks. However, we only need so many. Extras would sadly be put to waste. Please return to me in a week and I may be able to take more pins off of your hands.',
         'Your pins are a boon to our xuta. We use the pins we make, as well as the ones you and your xuta have supplied to us, for many tasks. However, we only need so many. Extras would sadly be put to waste. Please return to me later and I may be able to take more pins off of your hands.'
       );

-- Aun Ihmenaura (tokens): "Several passings of the sun must be made" -> "Time must pass"
UPDATE ace_world.weenie_properties_emote_action ea
  JOIN ace_world.weenie_properties_emote em ON em.id = ea.emote_Id
   SET ea.message = 'We must forever be cautious of the land we live in. Hunt the Siraluun too frequently and their numbers dwindle. The spirits demand that respect be paid to these fowl before further can be accomplished. Time must pass before the spirits will allow me to accept another token from you.'
 WHERE em.object_Id = 29860            -- ace29860-aunihmenaura
   AND ea.message IN (
         'We must forever be cautious of the land we live in. Hunt the Siraluun too frequently and their numbers dwindle. The spirits demand that respect be paid to these fowl before further can be accomplished. Several passings of the sun must be made before the spirits will allow me to accept another token from you.',
         'We must forever be cautious of the land we live in. Hunt the Siraluun too frequently and their numbers dwindle. The spirits demand that respect be paid to these fowl before further can be accomplished. Time must pass before the spirits will allow me to accept another token from you.'
       );

-- Aun Kahuiura (crafted goods): "Perhaps in a week" -> "Perhaps in time"
UPDATE ace_world.weenie_properties_emote_action ea
  JOIN ace_world.weenie_properties_emote em ON em.id = ea.emote_Id
   SET ea.message = 'As with many of the other crafted goods my siblings accept, I too suffer from a surplus. Please, give us time to use what we have. Perhaps in time we will have run our stock down low enough and I will be able to accept goods from you.'
 WHERE em.object_Id = 29861            -- ace29861-aunkahuiura
   AND ea.message IN (
         'As with many of the other crafted goods my siblings accept, I too suffer from a surplus. Please, give us time to use what we have. Perhaps in a week we will have run our stock down low enough and I will be able to accept goods from you.',
         'As with many of the other crafted goods my siblings accept, I too suffer from a surplus. Please, give us time to use what we have. Perhaps in time we will have run our stock down low enough and I will be able to accept goods from you.'
       );

-- Aun Pitamaura (scissors): "in a week's time" -> "in time"
UPDATE ace_world.weenie_properties_emote_action ea
  JOIN ace_world.weenie_properties_emote em ON em.id = ea.emote_Id
   SET ea.message = 'I encourage your zeal in the fine art of shear crafting. Unfortunately, my siblings cannot dull your blades fast enough. If, in time, you still have scissors you wish to donate to our practice of artistry, please bring them to me and I will happily reward you for them.'
 WHERE em.object_Id = 29862            -- ace29862-aunpitamaura
   AND ea.message IN (
         'I encourage your zeal in the fine art of shear crafting. Unfortunately, my siblings cannot dull your blades fast enough. If, in a week''s time, you still have scissors you wish to donate to our practice of artistry, please bring them to me and I will happily reward you for them.',
         'I encourage your zeal in the fine art of shear crafting. Unfortunately, my siblings cannot dull your blades fast enough. If, in time, you still have scissors you wish to donate to our practice of artistry, please bring them to me and I will happily reward you for them.'
       );
