# Shared consumer state

Task #70 completes the existing PostgreSQL inbox introduced by ADR 0009. Upload
parts and deduplication records were already in PostgreSQL; moving them again to
Redis would split the transaction that protects the consumer's database effect.
Retry state remains in the durable RabbitMQ message introduced by #69.

Each image is bounded to 8 MiB, 2,048 sequence positions and 64 KiB per part.
A PostgreSQL transaction-scoped advisory lock serializes handlers for one rider,
so two consumer instances cannot both pass a stale size check. Invalid or
oversized parts are quarantined without retry. An EOF can arrive first:
the part that completes the contiguous sequence performs the upload. A completion
receipt prevents late duplicate envelopes from recreating a completed buffer.

Incomplete uploads expire one hour after their oldest part. A sweep every
15 minutes removes the entire expired upload, including newer parts of that
same upload. Inbox and completion receipts retain the existing seven-day
retention. This bounds retained state without expiring a partial upload one
chunk at a time.

Validation:

- Independent handler instances and database contexts assemble out-of-order
  parts once, including EOF first and a late duplicate.
- Concurrent instances competing for the last byte of an upload admit exactly
  one part and roll back the rejected part and its inbox record.
- Expiry removes an incomplete upload while retaining a recent one.
- The real outbox publisher performs 3,000 publications against RabbitMQ; after
  each batch the broker reports zero leaked channels and all messages remain
  queued. Request-scoped publishers only enqueue outbox records.

Reproduce:

```sh
dotnet test RiderManager/RiderManagerTests/RiderManagerTests.csproj --filter FullyQualifiedName~InboxProcessorTests
dotnet test MotoHub/MotoHubTests/MotoHubTests.csproj --filter FullyQualifiedName~PublisherChannelSoakTests
```

These are integration proofs of shared state and channel lifetime. They do not
measure long-running production capacity or provide a multi-node database SLA.
