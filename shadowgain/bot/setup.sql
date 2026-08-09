-- Shadowgain bot: a READ-ONLY MySQL user.
--
-- The bot needs to answer two questions - "what level is this character" and "when did
-- this account last log in" - and nothing else. It has no reason to write game state, so
-- it is not able to. The exporter currently uses root; the bot deliberately does not.
--
-- Host is '%' rather than 'localhost' because the bot connects from the host to the
-- container's published port, so MySQL sees the Docker gateway address, not localhost.
-- That is not an exposure: docker-compose.fast.yml binds 3306 to 127.0.0.1 only, so
-- nothing off-box can reach the port in the first place.
--
-- Usage (on the droplet, password supplied at run time - never stored in this file):
--   docker exec -i ace-db mysql -uroot -p"$RP" < setup.sql

CREATE USER IF NOT EXISTS 'sgbot'@'%' IDENTIFIED BY 'REPLACE_ME';

-- Character name, last_Login_Timestamp, and the two play counters the activity gate
-- reads: level + Age from biota_properties_int (types 25 and 125), TotalExperience from
-- biota_properties_int64 (type 1).
--
-- int64 is a SEPARATE grant because it is a separate table, and forgetting it does not
-- fail at deploy time - it fails inside the daily sweep, hours later, as
-- "SELECT command denied". Which is exactly how it was found.
GRANT SELECT ON ace_shard.`character`             TO 'sgbot'@'%';
GRANT SELECT ON ace_shard.biota_properties_int    TO 'sgbot'@'%';
GRANT SELECT ON ace_shard.biota_properties_int64  TO 'sgbot'@'%';
-- The hard-lane marker (ShadowgainForfeitedMarker, PropertyBool 9102), read by the
-- Ascendant check so a fast-lane character can never claim the gold role.
GRANT SELECT ON ace_shard.biota_properties_bool   TO 'sgbot'@'%';

-- accountId -> accountName only. The account table also holds passwordHash and
-- passwordSalt, so this grant is column-scoped rather than table-wide: the bot can map
-- an id to a name and can not read a credential even by accident.
-- accessLevel joins the column scope so staff accounts can be excluded from the honour-style
-- checks. passwordHash and passwordSalt remain unreadable.
GRANT SELECT (accountId, accountName, accessLevel) ON ace_auth.account TO 'sgbot'@'%';

FLUSH PRIVILEGES;
