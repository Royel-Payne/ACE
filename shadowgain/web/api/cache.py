"""Shadowgain 124 — the per-character snapshot cache.

WHY 5 MINUTES, AND WHY NO REFRESH BUTTON

A character's shard rows only change when the server SAVES them, and `player_save_interval` is
300 seconds. Polling faster than that re-reads bytes that are identical — it is pure load with no
new information, and on a page with an inventory query behind it that is not free. So the TTL
matches the save interval, and the design (Task.md 123) deliberately has no refresh button: a
button would promise freshness the data cannot deliver, and would invite exactly the hammering
this cache exists to absorb.

The consequence worth being honest about: a player who logs out and reloads the page can see
their old numbers for up to five minutes. That is the correct trade for a snapshot view, and the
`asOf` timestamp on every payload says plainly how old the data is rather than implying it is
live.

Two things do NOT come from here: online/offline and last-seen, which ride the existing 30-second
status feed, so the parts of the page that should feel live still do.
"""

from __future__ import annotations

import threading
import time
from typing import Any, Callable

# Matches player_save_interval (300s). If that dial ever moves, this should move with it.
DEFAULT_TTL = 300

# A ceiling on distinct cached characters. 54 live characters today, so this will not be reached
# in normal use — it exists so a scripted sweep of the public endpoint cannot grow the process's
# memory without bound.
MAX_ENTRIES = 500


class TTLCache:
    def __init__(self, ttl: int = DEFAULT_TTL, max_entries: int = MAX_ENTRIES) -> None:
        self.ttl = ttl
        self.max_entries = max_entries
        self._entries: dict[Any, tuple[float, Any]] = {}
        self._lock = threading.Lock()

    def get_or_build(self, key: Any, build: Callable[[], Any]) -> tuple[Any, float]:
        """Return `(value, age_seconds)`, building on a miss.

        `build` runs OUTSIDE the lock. Holding it across a database round-trip would serialise
        every request behind the slowest one — which for a first inventory load is precisely the
        request you least want everybody queued behind. The cost is that two simultaneous misses
        for the same character can both build; that duplicates one read and then converges, which
        is far cheaper than the contention it avoids.
        """
        now = time.time()

        with self._lock:
            entry = self._entries.get(key)

            if entry is not None and now - entry[0] < self.ttl:
                return entry[1], now - entry[0]

        value = build()

        with self._lock:
            if len(self._entries) >= self.max_entries:
                self._evict_locked(now)

            self._entries[key] = (time.time(), value)

        return value, 0.0

    def _evict_locked(self, now: float) -> None:
        """Drop everything expired; if that frees nothing, drop the oldest quarter."""
        expired = [k for k, (stamp, _) in self._entries.items() if now - stamp >= self.ttl]

        for key in expired:
            self._entries.pop(key, None)

        if len(self._entries) < self.max_entries:
            return

        oldest = sorted(self._entries.items(), key=lambda kv: kv[1][0])

        for key, _ in oldest[: max(1, len(oldest) // 4)]:
            self._entries.pop(key, None)

    def invalidate(self, key: Any) -> None:
        with self._lock:
            self._entries.pop(key, None)

    def stats(self) -> dict:
        with self._lock:
            return {"entries": len(self._entries), "ttl": self.ttl}


# One cache for private character payloads, one for public. Separate because they hold different
# shapes for the same character id, and because a public scrape must not be able to evict the
# private entries a logged-in player is actually using.
private_characters = TTLCache()
public_characters = TTLCache()

# The picker list changes when a character is created, deleted or levels — none of which needs
# five-minute freshness, but a shorter TTL keeps a new character from feeling lost.
character_lists = TTLCache(ttl=60)
