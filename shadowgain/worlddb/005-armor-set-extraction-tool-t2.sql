-- Shadowgain 213: the TIER 2 Armor Set Extraction Tool - any coverage onto any coverage.
--
-- Ships in the FIRST commit of the feature, deliberately. 209's tool weenie was typed in by hand on TEST
-- and had no migration until the morning it deployed, which is how it came within one step of going to
-- LIVE as a dead feature: the code and the dial would have shipped and the tool simply would not have
-- existed for anyone to buy. That is not repeated here.
--
-- WHY A SECOND TOOL RATHER THAN A MODE ON THE FIRST. By the time the handler runs, a tier-1 and a tier-2
-- attempt are indistinguishable - same donor, same player, same skill. The TOOL is the only thing that can
-- carry the tier, so the tier has to be a separate weenie. It also keeps 209 exactly as it shipped: its
-- tool, its dial and its coverage guard are untouched, which was the explicit constraint on this work.
--
-- THE ICON IS THE YELLOW VARIANT, and that is a deliberate pairing rather than a free choice. Chris:
-- "the games original tailoring items have a red/yellow so I figured we could use the yellow since we used
-- red last time". 209's tool cloned the Armor Tailoring Kit (41956, icon 100690891 - the red one); this
-- clones its sibling the Weapon Tailoring Kit (51445, icon 100693217 - the yellow one). Same art family,
-- so the two tiers read as a matched pair on the vendor panel instead of as unrelated items.
--
-- PRICED AT 50 MMD, matching the tier-1 kit (Chris, 2026-08-24: "1 mmd just feels too low, let's keep
-- them both at 50 mmd" - it shipped at 1 first). Both halves are still required and the reason is not
-- obvious - it cost two wrong diagnoses on TEST when 209 shipped. `Value` (int 19) is what a single unit
-- is worth, but a vendor prices a stack from `StackUnitValue` (int 15), and the clone source is stackable
-- at 100 - so without forcing MaxStackSize (11) to 1 AND setting StackUnitValue, the shop offers 100
-- kits for 1 trade note.
--
-- The kit cost is deliberately trivial next to the risk: it is CONSUMED on every attempt, a failed
-- extraction destroys the DONOR, and the binding roll (a 33% ceiling, 38% with Charmed Smith) destroys
-- the TARGET on failure. Destructive by design - the gamble is the price.
--
-- TargetType 2054 (Armor|Clothing|Gem) is mandatory, not cosmetic. The retail client runs
-- ItemHolder::TargetCompatibleWithObject and refuses to SEND a use request whose tool TargetType does not
-- intersect the target's ItemType - so a wrong value here means the server never sees the attempt at all
-- and no amount of server-side code can rescue it. That failure cost a full build cycle on 209.
--
-- WHY THIS FILE EXISTS: this is a world-database edit, and ace_world is REPLACED wholesale by an
-- ACE world DB release. Anything changed by hand is silently gone on the next import with no
-- error and no warning. Re-apply every file in this directory after any world DB update.
--
-- Idempotent: deletes its own rows before inserting, and keys on the weenie rather than on a row id
-- (`weenie_properties_create_list.id` is auto_increment and differs per shard). Applies at next server
-- restart - ACE caches weenies at startup, so the restart is required, not optional. See the Phase 5
-- warning in DEPLOY.md.
--
--   docker exec -i ace-db mysql -uroot -p"$PW" < 005-armor-set-extraction-tool-t2.sql

DELETE FROM ace_world.weenie_properties_int    WHERE object_Id = 900213;
DELETE FROM ace_world.weenie_properties_bool   WHERE object_Id = 900213;
DELETE FROM ace_world.weenie_properties_string WHERE object_Id = 900213;
DELETE FROM ace_world.weenie_properties_d_i_d  WHERE object_Id = 900213;
DELETE FROM ace_world.weenie                   WHERE class_Id  = 900213;

-- type 38 = WeenieType.Gem, the useable-on-target behaviour the handler rides on.
INSERT INTO ace_world.weenie (class_Id, class_Name, type)
VALUES (900213, 'ace900213-armorsetextractiontool2', 38);

INSERT INTO ace_world.weenie_properties_int (object_Id, type, value) VALUES
  (900213,  1,   2048),   -- ItemType: Gem
  (900213,  5,     10),   -- EncumbranceVal
  (900213, 11,      1),   -- MaxStackSize: 1. See the pricing note above.
  (900213, 12,      1),   -- StackSize
  (900213, 13,     10),   -- StackUnitEncumbrance
  (900213, 15,     50),   -- StackUnitValue: what the vendor charges. 50 MMD, same as the tier-1 kit.
  (900213, 16, 524296),   -- ItemUseable: useable on another object
  (900213, 19,     50),   -- Value
  (900213, 93,   1044),   -- PhysicsState
  (900213, 94,   2054);   -- TargetType: Armor|Clothing|Gem - mandatory, see above.

INSERT INTO ace_world.weenie_properties_bool (object_Id, type, value) VALUES
  (900213,  1, 0),        -- Stuck
  (900213, 11, 1),        -- IgnoreCollisions
  (900213, 13, 1),        -- Ethereal
  (900213, 14, 1),        -- GravityStatus
  (900213, 19, 1),        -- Attackable
  (900213, 69, 0);

INSERT INTO ace_world.weenie_properties_string (object_Id, type, value) VALUES
  (900213,  1, 'Superior Armor Set Extraction Kit'),
  (900213, 16, 'Draws the attribute set out of a piece of armor so it can be bound onto ANY other piece, whatever it covers. Extraction demands a formidable Armor Tinkering skill, and failure destroys the armor. Binding the extracted set succeeds at best one time in three - failure destroys the armor it is being bound to. The kit is consumed in the attempt.');

INSERT INTO ace_world.weenie_properties_d_i_d (object_Id, type, value) VALUES
  (900213,  1,  33555677),   -- Setup
  (900213,  3, 536870932),   -- SoundTable
  (900213,  8, 100693217),   -- Icon: the YELLOW tailoring kit art (51445). 209 uses the red one.
  (900213, 22, 872415275);   -- PhysicsEffectTable

-- The same four tailors that sell the 209 tool: Ianto, Ciriaco, Qing and Iqbal. destination_Type 4 = shop
-- inventory, stack_Size -1 = infinite stock. `id` is auto_increment and deliberately not specified.
DELETE FROM ace_world.weenie_properties_create_list
 WHERE weenie_Class_Id = 900213 AND object_Id IN (42428, 42429, 42430, 42431);

INSERT INTO ace_world.weenie_properties_create_list
  (object_Id, destination_Type, weenie_Class_Id, stack_Size, palette, shade, try_To_Bond)
VALUES
  (42428, 4, 900213, -1, 0, 0, 0),
  (42429, 4, 900213, -1, 0, 0, 0),
  (42430, 4, 900213, -1, 0, 0, 0),
  (42431, 4, 900213, -1, 0, 0, 0);
