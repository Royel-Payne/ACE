-- Shadowgain 209: the Armor Set Extraction Tool weenie, and its place on the four tailors.
--
-- WITHOUT THIS FILE, 209 DEPLOYS DEAD. The code, the recipe hook and the dial all ship in the
-- binary, but wcid 900209 exists only where somebody typed it in by hand - which was TEST. On LIVE
-- the tool would simply not exist: no vendor sells it, `/ci` is not open to players, and the recipe
-- CANNOT be driven by any other item. That last part is the whole reason this weenie exists.
--
-- WHY A DEDICATED WEENIE AT ALL. The client checks `ItemHolder::TargetCompatibleWithObject` before
-- it will even send the use request, ANDing the tool's TargetType against the target's ItemType. A
-- stock tailoring kit has the wrong TargetType for armour, so the client refuses locally and the
-- server sees nothing at all - verified on TEST, where the server log recorded ZERO use events while
-- Chris was clicking. The fix is a tool whose TargetType (2054 = Creature|Armor|Clothing) permits the
-- pairing, so the request reaches the server, where the real guards live.
--
-- PRICED AT 50 MMD, per Chris: "there's precedent for this price". Both halves are needed and the
-- reason is not obvious - it cost two wrong diagnoses on TEST. `Value` (int 19) sets what a single
-- unit is worth, but a vendor prices a stack from `StackUnitValue` (int 15). The weenie was cloned
-- from 41956, which is stackable at 100 x 1, so the shop offered 100 tools for 1 trade note until
-- BOTH were corrected: MaxStackSize (11) forced to 1, and StackUnitValue (15) to 50.
--
-- The tailors sell for MMD rather than pyreals because they carry AlternateCurrency; that is stock
-- data on those vendors and is not touched here.
--
-- NOT INCLUDED, deliberately: the TargetType of wcid 9295 (a stock tailoring kit) was changed on TEST
-- during the failed first approach and then reverted. LIVE was never touched and still reads the
-- stock 128 - verified before writing this file. There is nothing to undo, so this file does not
-- pretend to undo it.
--
-- WHY THIS FILE EXISTS: this is a world-database edit, and ace_world is REPLACED wholesale by an
-- ACE world DB release. Anything changed by hand is silently gone on the next import with no
-- error and no warning. Re-apply every file in this directory after any world DB update.
--
-- Idempotent: deletes its own rows before inserting them, and keys on the weenie rather than on a
-- row id (`weenie_properties_create_list.id` is auto_increment and will differ per shard). Applies
-- at next server restart - ACE caches weenies at startup.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 004-armor-set-extraction-tool.sql

DELETE FROM ace_world.weenie_properties_int    WHERE object_Id = 900209;
DELETE FROM ace_world.weenie_properties_bool   WHERE object_Id = 900209;
DELETE FROM ace_world.weenie_properties_string WHERE object_Id = 900209;
DELETE FROM ace_world.weenie_properties_d_i_d  WHERE object_Id = 900209;
DELETE FROM ace_world.weenie                   WHERE class_Id  = 900209;

-- type 38 = WeenieType.Gem, the useable-on-target behaviour the recipe hook rides on.
INSERT INTO ace_world.weenie (class_Id, class_Name, type)
VALUES (900209, 'ace900209-armorsetextractiontool', 38);

INSERT INTO ace_world.weenie_properties_int (object_Id, type, value) VALUES
  (900209,  1,   2048),   -- ItemType: Gem
  (900209,  5,     10),   -- EncumbranceVal
  (900209, 11,      1),   -- MaxStackSize: 1. Cloned from 41956 as 100; a stack of 100 priced as one.
  (900209, 12,      1),   -- StackSize
  (900209, 13,     10),   -- StackUnitEncumbrance
  (900209, 15,     50),   -- StackUnitValue: what the vendor actually charges. 50 MMD.
  (900209, 16, 524296),   -- ItemUseable: useable on another object
  (900209, 19,     50),   -- Value
  (900209, 93,   1044),   -- PhysicsState
  (900209, 94,   2054);   -- TargetType: Creature|Armor|Clothing - the whole point, see above.

INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value) VALUES
  (900209,  1, 0),        -- Stuck
  (900209, 11, 1),        -- IgnoreCollisions
  (900209, 13, 1),        -- Ethereal
  (900209, 14, 1),        -- GravityStatus
  (900209, 19, 1),        -- Attackable
  (900209, 69, 0);        -- IsSellable handled by the vendor rows below

INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (900209,  1, 'Armor Set Extraction Kit'),
  (900209, 16, 'Used on a piece of armor to draw out its attribute set, producing an applicator that can move that set onto another piece covering the same area. Requires Armor Tinkering skill - on failure the armor it is used on is destroyed. The kit is consumed in the attempt.');

INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value) VALUES
  (900209,  1,  33555677),   -- Setup
  (900209,  3, 536870932),   -- SoundTable
  (900209,  8, 100690891),   -- Icon
  (900209, 22, 872415275);   -- PhysicsEffectTable

-- The four tailors: Ianto, Ciriaco, Qing and Iqbal. destination_Type 4 = shop inventory,
-- stack_Size -1 = infinite stock. `id` is auto_increment and is deliberately not specified.
DELETE FROM ace_world.weenie_properties_create_list
 WHERE weenie_Class_Id = 900209 AND object_Id IN (42428, 42429, 42430, 42431);

INSERT INTO ace_world.weenie_properties_create_list
  (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
VALUES
  (42428, 4, 900209, -1, 0, 0, 0),
  (42429, 4, 900209, -1, 0, 0, 0),
  (42430, 4, 900209, -1, 0, 0, 0),
  (42431, 4, 900209, -1, 0, 0, 0);
