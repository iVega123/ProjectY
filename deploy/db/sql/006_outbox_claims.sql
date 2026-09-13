-- Claims on the shared outbox, so a second relay replica cannot publish the same row.
--
-- The rental-core Kafka relay selected pending rows and marked them published
-- after sending. With one replica that is correct; with two, both select the
-- same rows and both send. A claim is taken before sending, in a short
-- transaction, and the send happens outside it: holding a transaction open
-- across a Kafka timeout would turn a broker outage into database contention.
--
-- The lease is what keeps a relay that dies mid-send from parking its rows:
-- once claimed_until passes, the next relay takes them. Republishing after a
-- crash is a duplicate delivery, and the consumers' inbox already absorbs it.
--
-- identity and billing share this table and ignore both columns; their relays
-- adopt the same claim in #192.
ALTER TABLE outbox ADD COLUMN IF NOT EXISTS claim_token UUID;
ALTER TABLE outbox ADD COLUMN IF NOT EXISTS claimed_until TIMESTAMPTZ;

-- Per-aggregate order: a row is eligible only when no earlier row of the same
-- aggregate is still pending. This index keeps that check a range read.
CREATE INDEX IF NOT EXISTS outbox_pending_by_aggregate
    ON outbox (aggregate_type, aggregate_id, occurred_at)
    WHERE published_at IS NULL;
