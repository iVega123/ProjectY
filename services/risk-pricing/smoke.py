"""Live Kafka acceptance probe; run inside the Compose network, never against production."""
import os
import time
import uuid
from confluent_kafka import Consumer, Producer
from rider_pb2 import RiderEvent

identity = "smoke-" + str(uuid.uuid4())
broker = os.environ.get("KAFKA_BOOTSTRAP_SERVERS", "kafka:9092")
consumer = Consumer({"bootstrap.servers": broker, "group.id": identity, "auto.offset.reset": "earliest"})
consumer.subscribe(["risk.scored", "pricing.updated"])
producer = Producer({"bootstrap.servers": broker})
event = RiderEvent(event_id=identity, rider_id=identity, occurred_at_ms=int(time.time()*1000), verified=True)
producer.produce("rider.verified", key=identity, value=event.SerializeToString())
assert producer.flush(10) == 0
deadline = time.monotonic()+45
seen = set()
try:
    while time.monotonic() < deadline and len(seen) < 2:
        message = consumer.poll(1)
        if message is None or message.error():
            continue
        result = RiderEvent.FromString(message.value())
        if message.topic() == "risk.scored" and result.rider_id == identity:
            assert result.HasField("risk_score") and 0 <= result.risk_score <= 100
            seen.add("risk.scored")
        if message.topic() == "pricing.updated":
            assert result.pricing_json
            seen.add("pricing.updated")
    assert len(seen) == 2, f"Missing live output: {seen}"
    print("PASS: real Kafka verification -> risk.scored; periodic pricing.updated")
finally:
    consumer.close()
