# Update — your off-hand weapon now trains the right skill

**If you dual wield, your off-hand has been training the wrong thing.** Every off-hand swing was crediting its experience to Dual Wield instead of to the weapon you were actually holding — so a finesse dagger in your left hand trained Dual Wield, not Finesse Weapons. That is fixed. Both hands now credit the weapon they are swinging, whatever it is, including mixed setups like a light main-hand and a finesse off-hand.

**Zan/Apex and Adramelech worked this out from in-game and reported it accurately** before anyone looked at the code — *"the main hand was getting exp but the off hand was going to dw"* was exactly right. Measured afterwards, it was almost precisely half of every swing going to the wrong skill, which is why a two-handed alt could feel like it was catching up. Thank you both; that is the kind of report that gets something fixed rather than argued about.

**Dual Wield itself loses nothing.** It still trains on every dual-wield swing, the same as before. Nothing was taken away from it to pay for this.

**We also fixed the knock-on effect, so this is not a trade.** Sending off-hand credit to the weapon skill would have quietly cut into Coordination gain, because Dual Wield was the main thing feeding it. Coordination now trains from dual-wielding directly, tuned to land within about one percent of where it was. So this should read as a straight gain: your weapon skills and their attributes move faster, and nothing moves slower. If your Coordination looks like it has slowed down, tell me — that number is a first guess from testing and it moves if the live data disagrees.

**One thing worth knowing, which is not a bug and is not changing: leave "repeat attacks" turned on if you dual wield.** With that option off, your off-hand never actually swings — you are effectively fighting one-handed and losing the off-hand's damage entirely. This has always been true and it is not something we are going to change, since it lives deep in the combat animation code. The fix above means it no longer costs you any experience, but it still costs you damage. Turn it on.

**Nothing retroactive, and nothing you have earned has changed.** Experience already banked stays where it is — this changes where *future* swings go. No skills, ranks or attributes were altered.

As always — if something reads wrong or behaves oddly, say so in #bugs or just tell me.
