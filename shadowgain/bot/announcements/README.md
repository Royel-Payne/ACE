# Announcement bodies

One file per announcement, posted with `announce.py --channel info --file <path>`.

**These are in git on purpose.** `announce.py` reads the wording from a FILE rather than argv
precisely so it can be reviewed as a normal diff before anything reaches players — which only
works if the files are versioned. They were not, until 121.

`announce-posted.json` — the already-posted ledger — is deliberately **NOT** here. It is runtime
state written on the droplet, and shipping a repo copy over it would erase the record of anything
posted since the last deploy. That record is what stops the same announcement going out twice
(see 120, where it went out twice five minutes apart).

## Formatting: ONE `# ` LINE, then `**bold**` lead-ins. Nothing else.

`announce.py` consumes the **first** `# ` line as the embed title. Every *later* `#` stays in the
body and Discord renders it as a full-size H1 **inside** the embed, which is far larger than
surrounding text and reads as shouting.

That is what happened to the 178 post on 2026-08-19 - four `#` section headings shipped live before
Chris spotted it: *"a few really big/bold sections that feel a bit odd - was this intentional?"*

Every announcement here from 090 onward uses the same shape, and it is the one to copy:

```
# Update — short title
(blank)
**Bold lead-in sentence.** Then the explanation in the same paragraph.
(blank)
**Next bold lead-in.** And so on.
```

Sections do not need headings; the bold lead-in *is* the heading. If a post is long enough to feel
like it needs `##`, it is long enough to cut.

**To fix a post that is already out:** `--edit <message_id>` rewrites it in place, keeping its
position and timestamp and sending no second notification. Use that for wording and formatting;
repost only when the CONTENT changed enough that people who read it need to read it again.
