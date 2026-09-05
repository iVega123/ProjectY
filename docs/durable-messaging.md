# Durable retry and quarantine

The active RabbitMQ producers write an outbox row in the business transaction.
The relay declares durable source queues, publishes persistent messages and waits
for publisher confirms. It disposes each connection and channel, including on
failure. The request-scoped publishers do not own AMQP channels.

Both consumers use the shared retry router. Failed registrations, image parts
and licence updates enter source-specific quorum retry queues with 2, 4 and
8 second TTLs. Their dead-letter exchange routes expired messages back to the
original source. Quorum retry queues use at-least-once dead-lettering and
reject-publish overflow so the handoff is confirmed by the broker. After three
retries, the message goes through a dedicated terminal exchange into a durable
poison queue. Invalid authentication and malformed licence JSON are quarantined
immediately. No consumer reads the poison queues automatically.

The original delivery is acknowledged only after the destination confirms it.
A mandatory return is treated as a failed publication. Routing failure stops
the consumer host with the original delivery unacknowledged; the broker can
redeliver after the application is restarted. Duplicate delivery across that
boundary remains possible, and the existing inbox prevents a duplicate effect.
Consumer registration failures fail startup visibly through host error logging.

Retry count lives in the persistent message header, not a process dictionary.
It is bounded by three attempts and has the message's lifetime; adding a second
Redis counter would introduce another nontransactional state boundary.

Prometheus scrapes RabbitMQ's detailed queue depth and consumer counts. The
`RabbitMqDeadLetters` warning remains active while a poison queue contains
messages, including after an application restart. An OTel counter,
`messaging.dead_letters`, identifies each source that sends a message to quarantine.
Use the preserved message ID and trace headers to investigate before replay.

Existing source queue arguments remain compatible with previously initialized
brokers. New queues/exchanges are declared on startup. Existing installations
must apply the consumer permission patterns from `scripts/New-LocalSecrets.ps1`
to their current users; do not rotate credentials or delete data to add permissions.

Reproduce the restart and poison-message proof:

```sh
dotnet test RiderManager/RiderManagerTests/RiderManagerTests.csproj --filter FullyQualifiedName~DurableRetryTests
```

The test restarts a real RabbitMQ container during a delayed delivery, verifies
the persistent message and retry count survive, observes the good message pass,
and follows the poison message into its terminal queue. This proves broker
restart durability, not loss of a host disk or a multi-node quorum.
