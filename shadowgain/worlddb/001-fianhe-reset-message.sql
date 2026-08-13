-- Shadowgain 107: Fianhe tells the truth about what a skill reset does here.
--
-- Stock retail text is "All of your skills have been reset." On this world that sentence is
-- false, and false in the most alarming direction: it says DESTROYED when the truth is FROZEN
-- AND FREE TO RESTORE.
--
-- Fianhe resets every skill, and because all_skills_trained is on, each one routes into 093's
-- deliberate prune: AdvancementClass drops to Untrained but ranks and ExperienceSpent are left
-- exactly alone. Re-training any skill (client Train button, cost 0) removes it from the pruned
-- list and recomputes the rank from that preserved XP. Nothing is lost at any point.
--
-- Kept as a FEATURE rather than fixed: it is the bulk counterpart to the Gem of Forgetfulness,
-- letting a player clear everything and re-train only what they actually use - shorter buff
-- cycles, and a skill list that means something. It is already well gated by stock data
-- (InqYesNo confirmation, UsedFreeSkillReset, SkillReset30Day cooldown, MMD/Luminance cost).
--
-- The mechanic was never the problem. A player believing in a loss that did not happen was.
-- Chris, on reading the stock message after his own reset: "Not real sure what to do."
--
-- WHY THIS FILE EXISTS: this is a world-database edit, and ace_world is REPLACED wholesale by an
-- ACE world DB release. Anything changed by hand is silently gone on the next import with no
-- error and no warning. Re-apply every file in this directory after any world DB update.
--
-- Idempotent: matches both the original and the replacement, and keys on the weenie rather than
-- a row id, so it survives re-import renumbering. Applies at next server restart - ACE caches
-- weenies at startup, so an already-running world keeps the old text until it recycles.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 001-fianhe-reset-message.sql

UPDATE ace_world.weenie_properties_emote_action ea
  JOIN ace_world.weenie_properties_emote em ON em.id = ea.emote_Id
   SET ea.message = 'All of your skills have been set aside, and your specializations removed. Nothing is lost - your ranks are held exactly as they were. Re-train any skill, free of cost, to bring it back in full.'
 WHERE em.object_Id = 43941            -- ace43941-fianhe
   AND ea.type = 18                    -- Tell
   AND ea.message IN (
         'All of your skills have been reset.',
         'All of your skills have been set aside, and your specializations removed. Nothing is lost - your ranks are held exactly as they were. Re-train any skill, free of cost, to bring it back in full.'
       );
