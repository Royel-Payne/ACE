# Update — short, concrete title

**One bold lead-in sentence per change, then the explanation in the same paragraph.** The bold part
is the heading — do not add `#` headings for sections, because only the first `# ` line becomes the
embed title and every later one renders oversized inside the body. `announce.py` refuses that now,
but the shape below is the one to copy.

**Say what changed for the player, not what changed in the code.** No dial names, no entry numbers,
no internal vocabulary. "Evading attacks now trains Quickness" rather than
"`defense_attribute_weight` is 0.3".

**Lead with the thing they will notice.** If a number moved, say roughly how much. If something was
broken, say it was broken and for how long — people are far more forgiving of a named bug than a
silent one.

**Say what did NOT change, when it is the obvious fear.** "No lock anywhere got harder to pick" and
"nobody lost a rank" do more to prevent a panicked #bugs post than any amount of detail about what
did change.

**Credit players by name when they found it.** It is true, it is cheap, and it is why people report
things at all.

**Close with the invitation.** These numbers are guesses until they are measured; say so and ask.

---

Delete this line and everything below it before sending.

- Write prose as ONE LINE PER PARAGRAPH or let `unwrap()` handle the rewrapping — Discord renders
  every newline literally, so a hard-wrapped paragraph arrives broken mid-sentence.
- Rehearse it: `announce.py --channel info --file <this> --dry-run`
- Fix a post that is already out: `--edit <message_id>` (keeps its position, sends no second ping)
- The full reasoning behind all of this is in `README.md` in this directory.
