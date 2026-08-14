# Announcement bodies

One file per announcement, posted with `announce.py --channel info --file <path>`.

**These are in git on purpose.** `announce.py` reads the wording from a FILE rather than argv
precisely so it can be reviewed as a normal diff before anything reaches players — which only
works if the files are versioned. They were not, until 121.

`announce-posted.json` — the already-posted ledger — is deliberately **NOT** here. It is runtime
state written on the droplet, and shipping a repo copy over it would erase the record of anything
posted since the last deploy. That record is what stops the same announcement going out twice
(see 120, where it went out twice five minutes apart).
