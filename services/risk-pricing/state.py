import json
import sqlite3

from policy import pricing, score
from rider_pb2 import RiderEvent


class State:
    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.executescript("""
          PRAGMA journal_mode=WAL;
          CREATE TABLE IF NOT EXISTS inbox(id TEXT PRIMARY KEY, occurred INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS riders(id TEXT PRIMARY KEY, verified INTEGER NOT NULL DEFAULT 0,
            verified_at INTEGER NOT NULL DEFAULT 0, scored_at INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE IF NOT EXISTS rentals(id TEXT PRIMARY KEY, rider TEXT NOT NULL,
            closed INTEGER NOT NULL, late INTEGER NOT NULL, occurred INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY, topic TEXT NOT NULL, key TEXT NOT NULL,
            payload BLOB NOT NULL, traceparent TEXT);
        """)

    def emit(self, event, topic, key, traceparent=None):
        self.db.execute("INSERT OR IGNORE INTO outbox VALUES(?,?,?,?,?)",
                        (event.event_id, topic, key, event.SerializeToString(), traceparent))

    def rescore(self, rider, now, traceparent=None, source_id=None):
        verified = self.db.execute("SELECT verified FROM riders WHERE id=?", (rider,)).fetchone()[0]
        completed, late = self.db.execute("SELECT COUNT(*), COALESCE(SUM(late),0) FROM rentals WHERE rider=? AND closed=1", (rider,)).fetchone()
        self.emit(RiderEvent(event_id=f"risk:{source_id or str(now)+':'+rider}", rider_id=rider, occurred_at_ms=now,
                            risk_score=score(bool(verified), late, completed)), "risk.scored", rider, traceparent)
        self.db.execute("UPDATE riders SET scored_at=? WHERE id=?", (now, rider))

    def apply(self, topic, event, now, verified=None, traceparent=None):
        if not event.event_id or not event.rider_id or not event.HasField("occurred_at_ms"):
            raise ValueError("event identity and time are required")
        with self.db:
            inserted = self.db.execute("INSERT OR IGNORE INTO inbox VALUES(?,?)", (event.event_id, event.occurred_at_ms))
            if inserted.rowcount == 0:
                return False
            self.db.execute("INSERT OR IGNORE INTO riders(id) VALUES(?)", (event.rider_id,))
            if topic in ("document.stored", "rider.verified"):
                match = verified if topic == "document.stored" else event.verified
                changed = self.db.execute("UPDATE riders SET verified=?, verified_at=? WHERE id=? AND verified_at<=?",
                                (bool(match), event.occurred_at_ms, event.rider_id, event.occurred_at_ms))
                if topic == "document.stored" and changed.rowcount:
                    self.emit(RiderEvent(event_id="verified:"+event.event_id, rider_id=event.rider_id,
                                        occurred_at_ms=event.occurred_at_ms, verified=bool(match)), "document.verified", event.rider_id, traceparent)
            if topic in ("rental.started", "rental.closed"):
                closed = topic == "rental.closed"
                late = closed and event.ended_at_ms > event.predicted_end_at_ms
                self.db.execute("""INSERT INTO rentals VALUES(?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET
                  closed=excluded.closed, late=excluded.late, occurred=excluded.occurred
                  WHERE excluded.occurred>rentals.occurred OR (excluded.occurred=rentals.occurred AND excluded.closed=1)""",
                  (event.rental_id, event.rider_id, closed, late, event.occurred_at_ms))
            self.rescore(event.rider_id, now, traceparent, event.event_id)
            return True

    def periodic(self, now, capacity):
        with self.db:
            for (rider,) in self.db.execute("SELECT id FROM riders WHERE scored_at<?", (now-86_400_000,)).fetchall():
                self.rescore(rider, now)
            active = self.db.execute("SELECT COUNT(*) FROM rentals WHERE closed=0").fetchone()[0]
            table = pricing(active, capacity, now)
            self.emit(RiderEvent(event_id=f"pricing:{now}", occurred_at_ms=now, pricing_json=json.dumps(table)),
                      "pricing.updated", "BRL")
            # Keep dedup beyond the configured 7-day Kafka retention; facts remain
            # reconstructible. The operational runbook forbids older replay in-place.
            self.db.execute("DELETE FROM inbox WHERE occurred<?", (now-90*86_400_000,))
