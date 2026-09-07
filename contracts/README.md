# Governed Kafka event contracts

The topic manifest fixes the schema and immutable partition key for each event.
Rental events use `motorcycle_id`, rider/document/risk events use `rider_id`, and
invoices use `rental_id`. Money uses signed 64-bit minor units plus `currency`;
singular scalar fields have explicit presence. Removed numbers remain reserved.
The carried rider name and agreed price describe the rental at creation time.
Historical events without those fields remain distinguishable from zero/empty.

The pre-existing `pricing.updated` catalog is keyed by its fixed ISO currency
identifier (`BRL`), not by a rider. Its retained `pricing_json` contains integer
minor units and the currency, and is preserved for the existing price-table
consumer. New settlement/invoice money is typed directly as Protobuf `int64`.

The first governed versions are installed by `schema-init`. Producers look up
the exact schema through `/apis/ccompat/v7/subjects/{topic}-value`, cache its ID
in memory, and send raw Protobuf directly to Kafka with a `schema-id` header.
This deliberately preserves the retained raw payloads and existing decoders;
it is **not** Confluent's binary wire envelope. Consumers decode locally and do
not call the registry. A warm producer continues during registry failure. A cold
producer retains its outbox until lookup succeeds. Schema registration is a
deployment task, never a per-message operation.

## Verified registry behavior

Apicurio **3.3.2** is pinned. Its [Apache 2.0 license](https://github.com/Apicurio/apicurio-registry/blob/3.3.2/LICENSE)
and [Confluent-compatible API](https://www.apicur.io/registry/docs/apicurio-registry/3.3.x/getting-started/assembly-confluent-schema-registry-compatibility.html)
were checked during #132. Registry data uses the existing Kafka broker's
KafkaSQL journal, with no registry request in the message delivery path.

The real checker accepts unchanged schemas and rejects the string-to-int64
canary. It also rejects optional field additions under FULL: its forward pass
reverses the schemas and treats the added fields as unreserved removals. This
is a conservative false rejection, not absent enforcement. See the pinned
[checker implementation](https://github.com/Apicurio/apicurio-registry/blob/3.3.2/schema-util/protobuf/src/main/java/io/apicurio/registry/protobuf/rules/compatibility/ProtobufCompatibilityChecker.java).
Do not downgrade FULL or silently ignore its rejection. Further evolution of
governed messages requires resolving that upstream limitation or introducing an
explicitly versioned contract with a consumer rollout.

Before #132, these topics had raw Protobuf but no registry version. Initial
adoption validates all old field names, numbers, types and presence against Git
and establishes the expanded schema as registry version 1. Tests prove that old
payloads decode with absent money and new payloads preserve explicit zero.
Once `topics.json` exists in the base revision, CI registers that revision first
and checks the proposed schema against it under FULL. The negative control is
sent to the compatibility endpoint only, never registered.

## Validation

`node --test contracts/check.test.mjs` checks conventions.
`CONTRACT_BASE_REF=<base-sha> node contracts/check.mjs` checks a disposable
registry at `http://localhost:18081/apis/ccompat/v7` (override with
`SCHEMA_REGISTRY_URL`). Never run the CI checker against a deployed registry;
use `node contracts/register.mjs` for deployment. The root Required job depends
on the registry test, including its incompatible canary.
