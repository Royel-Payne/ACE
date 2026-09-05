-- Shadowgain 224 (feature C): The Fence, the black-market vendor selling the mansion portal
-- devices for MMD, hiding in an Arwic basement.
--
-- WHY A VENDOR AT ALL: a tiny server cannot field the King's 1-6 / group content these devices
-- come from, so they are sold instead. All 10 are hardcoded MANSION-ONLY (Hooker.cs refuses use
-- unless RootHouse.HouseType == Mansion) - Chris's decision 2026-09-05 is to sell them AS-IS with
-- no relaxation of that gate; if mansions are ever opened to non-monarchs, the market widens by
-- itself.
--
-- THE NPC is a straight clone of tailor Ianto (42428), the proven MMD-vendor template: type 12
-- Vendor, AlternateCurrency (DID 57) = 20630 Trade Note (250,000), MerchandiseItemTypes 0 (buys
-- nothing back), title under the name via PropertyString 5 (Template). Cloned by INSERT..SELECT
-- so appearance/attribute data always matches whatever the world DB actually contains. Ianto's
-- vendor-chatter emotes are deliberately NOT cloned (they talk about tailoring).
--
-- PRICING: with AlternateCurrency set, the vendor charges `Value` (int 19) in UNITS of the
-- currency item - the 004 tool at Value 50 sells for 50 MMD. Chris priced the devices at
-- 250 MMD each (2026-09-05: "some effort, not a wall"; all 10 ~ 2,500 MMD). None of the 10 is
-- stackable (no MaxStackSize/StackUnitValue rows), so Value alone is the price - the 004
-- StackUnitValue trap does not apply. Original values, for the record: 29608-29612 were 10000,
-- 30261 was 100000, 26588/27932 were 120000, 29103 was 0, 30745 was 5000.
--
-- SPAWN: landblock_instance guid 0x7C6A907B (first free tail slot in Arwic's 0x7C6A9xxx static
-- range), cell 0xC6A9014D - the basement Chris picked, coords his.
--
-- WHY THIS FILE EXISTS: ace_world is REPLACED wholesale by an ACE world DB release; re-apply
-- every file in this directory after any world DB update. Idempotent: deletes its own rows
-- before inserting. Applies at next server restart - ACE caches weenies at startup, and
-- landblock instances load with the landblock. RECYCLE THE CONTAINER AFTER APPLYING.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 006-the-fence-mansion-devices.sql

-- ---------------------------------------------------------------- The Fence (wcid 900220)

DELETE FROM ace_world.weenie_properties_int           WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_bool          WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_float         WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_string        WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_d_i_d         WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_attribute     WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_attribute_2nd WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_position      WHERE object_Id = 900220;
DELETE FROM ace_world.weenie_properties_create_list   WHERE object_Id = 900220;
DELETE FROM ace_world.weenie                          WHERE class_Id  = 900220;

INSERT INTO ace_world.weenie (class_Id, class_Name, type)
VALUES (900220, 'ace900220-thefence', 12);            -- 12 = WeenieType.Vendor, like Ianto

INSERT INTO ace_world.weenie_properties_int (object_Id, type, value)
  SELECT 900220, type, value FROM ace_world.weenie_properties_int WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value)
  SELECT 900220, type, value FROM ace_world.weenie_properties_bool WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_float (object_Id, type, value)
  SELECT 900220, type, value FROM ace_world.weenie_properties_float WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value)
  SELECT 900220, type, value FROM ace_world.weenie_properties_d_i_d WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_attribute (object_Id, type, init_Level, level_From_C_P, c_p_Spent)
  SELECT 900220, type, init_Level, level_From_C_P, c_p_Spent
    FROM ace_world.weenie_properties_attribute WHERE object_Id = 42428;
INSERT INTO ace_world.weenie_properties_attribute_2nd (object_Id, type, init_Level, level_From_C_P, c_p_Spent, current_Level)
  SELECT 900220, type, init_Level, level_From_C_P, c_p_Spent, current_Level
    FROM ace_world.weenie_properties_attribute_2nd WHERE object_Id = 42428;

INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (900220, 1, 'The Fence'),
  (900220, 5, 'Black Marketeer');                     -- 5 = Template, the title under the name

-- outfit: clone Ianto's worn clothes (destination_Type 2 = Wield), then the shop -
-- destination_Type 4 = shop inventory, stack_Size -1 = infinite stock
INSERT INTO ace_world.weenie_properties_create_list
  (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
  SELECT 900220, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond
    FROM ace_world.weenie_properties_create_list
   WHERE object_Id = 42428 AND destination_Type = 2;

INSERT INTO ace_world.weenie_properties_create_list
  (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
VALUES
  (900220, 4, 29608, -1, 0, 0, 0),   -- Black Spawn Den Portal Device
  (900220, 4, 29609, -1, 0, 0, 0),   -- Citadels Portal Device
  (900220, 4, 29610, -1, 0, 0, 0),   -- Lesser Direlands Device
  (900220, 4, 29611, -1, 0, 0, 0),   -- Outland Portal Device
  (900220, 4, 29612, -1, 0, 0, 0),   -- Olthoi Lands Portal Device
  (900220, 4, 30261, -1, 0, 0, 0),   -- Dangerous Portal Device
  (900220, 4, 26588, -1, 0, 0, 0),   -- Portal to Kivik Lir's Temple
  (900220, 4, 27932, -1, 0, 0, 0),   -- Portal to Izji Qo's Temple
  (900220, 4, 29103, -1, 0, 0, 0),   -- K'nath Lair Portal
  (900220, 4, 30745, -1, 0, 0, 0);   -- Replica of a Tursh Totem

-- ---------------------------------------------------------------- 250 MMD each

UPDATE ace_world.weenie_properties_int
   SET value = 250
 WHERE type = 19  -- Value
   AND object_Id IN (29608, 29609, 29610, 29611, 29612, 30261, 26588, 27932, 29103, 30745);

-- ---------------------------------------------------------------- the spawn

DELETE FROM ace_world.landblock_instance WHERE guid = 0x7C6A907B;

INSERT INTO ace_world.landblock_instance
  (guid, weenie_Class_Id, obj_Cell_Id, origin_X, origin_Y, origin_Z, angles_W, angles_X, angles_Y, angles_Z, is_Link_Child)
VALUES
  (0x7C6A907B, 900220, 0xC6A9014D, 25.369148, 28.666265, 38.955002, -0.982935, 0, 0, 0.183953, 0);
