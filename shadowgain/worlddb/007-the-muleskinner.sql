-- Shadowgain 224 (feature A): The Muleskinner, the dialog NPC offering the permanent mule
-- conversion, standing in Arwic.
--
-- NOT A SHOP. Chris's decision 2026-09-05: the conversion is FREE and item-less - "no item in
-- inventory to mis-use; the confirm is the safety". So this is a type 10 Creature, not a type 12
-- Vendor: using him routes through Creature.ActOnUse, where the ShadowgainMuleTrainer marker
-- (PropertyBool 9105, set below) sends the hard-confirm dialog; accepting runs
-- Player.ApplyMuleConversion (Str 290, pack/burden augs, one-way combat brick 9104). All of that
-- ships in the BINARY - without the code this NPC is inert, and without this file the code has
-- nothing to click. Deploy them together.
--
-- Appearance/attributes are cloned from tailor Ianto (42428) by INSERT..SELECT, then the
-- vendor-only pieces are stripped: AlternateCurrency (DID 57), Merchandise ints (74/75/76) and
-- DealMagicalItems (bool 39). What remains is a plain usable NPC: ItemUseable 32 (Remote),
-- UseRadius 3.0, unattackable, RubberGlue. His worn outfit is cloned too (destination_Type 2);
-- Ianto's tailoring-chatter emotes are NOT.
--
-- SPAWN: landblock_instance guid 0x7C6A907A (free tail slot in Arwic's 0x7C6A9xxx static range),
-- cell 0xC6A90014, coords Chris's pick.
--
-- WHY THIS FILE EXISTS: ace_world is REPLACED wholesale by an ACE world DB release; re-apply
-- every file in this directory after any world DB update. Idempotent: deletes its own rows before
-- inserting. Applies at next server restart - ACE caches weenies at startup. RECYCLE THE
-- CONTAINER AFTER APPLYING.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 007-the-muleskinner.sql

DELETE FROM ace_world.weenie_properties_int           WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_bool          WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_float         WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_string        WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_d_i_d         WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_attribute     WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_attribute_2nd WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_position      WHERE object_Id = 900221;
DELETE FROM ace_world.weenie_properties_create_list   WHERE object_Id = 900221;
DELETE FROM ace_world.weenie                          WHERE class_Id  = 900221;

INSERT INTO ace_world.weenie (class_Id, class_Name, type)
VALUES (900221, 'ace900221-themuleskinner', 10);      -- 10 = WeenieType.Creature: dialog NPC, no shop

INSERT INTO ace_world.weenie_properties_int (object_Id, type, value)
  SELECT 900221, type, value FROM ace_world.weenie_properties_int WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value)
  SELECT 900221, type, value FROM ace_world.weenie_properties_bool WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value)
  SELECT 900221, type, value FROM ace_world.weenie_properties_float WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value)
  SELECT 900221, type, value FROM ace_world.weenie_properties_d_i_d WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_p_Spent)
  SELECT 900221, type, init_Level, level_From_C_P, c_p_Spent
    FROM ace_world.weenie_properties_attribute WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_p_Spent, current_Level)
  SELECT 900221, type, init_Level, level_From_C_P, c_p_Spent, current_Level
    FROM ace_world.weenie_properties_attribute_2nd WHERE object_Id = 42428;

-- strip the vendor-only pieces the clone brought along
DELETE FROM ace_world.weenie_properties_d_i_d WHERE object_Id = 900221 AND type = 57;             -- AlternateCurrency
DELETE FROM ace_world.weenie_properties_int   WHERE object_Id = 900221 AND type IN (74, 75, 76);  -- Merchandise*
DELETE FROM ace_world.weenie_properties_bool  WHERE object_Id = 900221 AND type = 39;             -- DealMagicalItems

-- the marker the Creature.ActOnUse hook looks for (PropertyBool.ShadowgainMuleTrainer)
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value) VALUES
  (900221, 9105, 1);

INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (900221, 1, 'The Muleskinner'),
  (900221, 5, 'Master of Burden');                    -- 5 = Template, the title under the name

-- worn outfit only (destination_Type 2 = Wield); no shop rows on purpose
INSERT INTO ace_world.weenie_properties_create_list
  (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
  SELECT 900221, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond
    FROM ace_world.weenie_properties_create_list
   WHERE object_Id = 42428 AND destination_Type = 2;

-- the spawn
DELETE FROM ace_world.landblock_instance WHERE guid = 0x7C6A907A;

INSERT INTO ace_world.landblock_instance
  (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child)
VALUES
  (0x7C6A907A, 900221, 0xC6A90014, 70.735474, 86.909065, 42.005001, -0.841234, 0, 0, 0.540671, 0);
