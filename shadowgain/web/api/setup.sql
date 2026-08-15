-- Shadowgain 124: the web character sheet's READ-ONLY MySQL user.
--
-- Usage (on the droplet, password supplied at run time - never stored in this file):
--   docker exec -i ace-db mysql -uroot -p"$RP" < setup.sql
--
-- THIS USER CAN ONLY SELECT. That is the enforcement behind "the web service never writes to a
-- game database" - the code has no write in it, and if a future edit added one it would fail at
-- the database rather than succeed quietly.
--
-- Host is '%' rather than 'localhost' for the same reason the bot's is (bot/setup.sql): the
-- service connects from the host to the container's published port, so MySQL sees the Docker
-- gateway address. Not an exposure - docker-compose.fast.yml binds 3306 to 127.0.0.1 only.

CREATE USER IF NOT EXISTS 'sgweb'@'%' IDENTIFIED BY 'REPLACE_ME';

-- ---------------------------------------------------------------------------------------------
-- ace_auth: the login path, and NOTHING else
-- ---------------------------------------------------------------------------------------------
--
-- This grant is column-scoped and the choice of columns is the security boundary.
--
-- passwordHash and passwordSalt ARE included, and they have to be: bcrypt verification happens
-- in this process, so it must read the hash. That is the same trust the game server has. What
-- makes it acceptable is what is NOT granted - email_Address, create_I_P, last_Login_I_P and
-- their _ntoa twins, and ban_Reason are all absent, so a compromised web process leaks no
-- personal data and no free-text staff notes. 123: "Never expose account email / password hash
-- / IP." The hash never leaves this process; the others cannot be read at all.
--
-- passwordSalt is needed because it is not a salt: ACE writes the literal string 'use bcrypt'
-- into it to mark the hash format, and a legacy SHA512 account must be REFUSED rather than
-- silently failed (AccountExtensions.PasswordMatches).
--
-- banned_Time / ban_Expire_Time so a suspended account cannot read its own sheet.
GRANT SELECT (accountId, accountName, passwordHash, passwordSalt, accessLevel,
              banned_Time, ban_Expire_Time)
  ON ace_auth.account TO 'sgweb'@'%';

-- ---------------------------------------------------------------------------------------------
-- ace_shard: the character data
-- ---------------------------------------------------------------------------------------------
GRANT SELECT ON ace_shard.`character`                        TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota                              TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_int               TO 'sgweb'@'%';
-- int64 is a SEPARATE table and a separate grant. Forgetting it does not fail at deploy time -
-- it fails on the first character whose skill XP passed uint.MaxValue, as "SELECT command
-- denied", which is exactly how the bot found the same omission.
GRANT SELECT ON ace_shard.biota_properties_int64             TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_bool              TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_float             TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_string            TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_attribute         TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_attribute_2nd     TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_skill             TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_position          TO 'sgweb'@'%';
-- Inventory: d_i_d carries the item Icon (type 8), i_i_d the Container/Wielder links (2 / 3).
GRANT SELECT ON ace_shard.biota_properties_d_i_d             TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.biota_properties_i_i_d             TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.character_properties_title_book    TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.character_properties_quest_registry TO 'sgweb'@'%';
-- 127: an item's spellbook, for the examine text the tooltip renders.
GRANT SELECT ON ace_shard.biota_properties_spell_book  TO 'sgweb'@'%';
-- The live dials the rank maths reads. PropertyManager loads these rows OVER the compiled
-- defaults, so computing ranks without them means computing on a curve the server abandoned.
GRANT SELECT ON ace_shard.config_properties_boolean          TO 'sgweb'@'%';
GRANT SELECT ON ace_shard.config_properties_long             TO 'sgweb'@'%';

-- ---------------------------------------------------------------------------------------------
-- ace_world: deliberately NOT granted
-- ---------------------------------------------------------------------------------------------
--
-- The quest names and landblock names the sheet needs come from ace_world, but they are static
-- content that changes only when a world DB is imported. So they are extracted once by
-- web/tools/build-name-tables.sh, committed as JSON, and read from disk. No grant, no join
-- across databases on a page load, and no 4,237-row query in front of every request.

FLUSH PRIVILEGES;
