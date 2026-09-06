import io
import logging
import os
import time
from concurrent.futures import Future
import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from confluent_kafka import Consumer, Producer, TopicPartition
from PIL import Image
import pytesseract
from opentelemetry import trace, propagate
from rental_pb2 import RentalEvent
from rider_pb2 import RiderEvent
from state import State
from policy import verify_number

log = logging.getLogger("risk-pricing")
tracer = trace.get_tracer("risk-pricing")

def ocr(event):
    if not event.object_key.startswith("riders/") or not event.object_key.endswith(".png"):
        raise ValueError("document must reference a sanitized object")
    s3 = boto3.client("s3", endpoint_url=os.environ["S3_ENDPOINT"],
                      config=Config(connect_timeout=2, read_timeout=5, retries={"max_attempts":1}))
    try:
        result = s3.get_object(Bucket=os.environ["S3_BUCKET"], Key=event.object_key)
    except ClientError as error:
        if error.response.get("Error", {}).get("Code") != "NoSuchKey":
            raise
        # Replacements/deletions remove private objects before delayed events
        # arrive. Consume these as unverified; newer facts still win by timestamp.
        log.info("document no longer available; verification failed closed")
        return False
    with result["Body"] as stream:
        content = stream.read(64*1024*1024+1)
    if len(content)>64*1024*1024:
        raise ValueError("sanitized document is too large")
    with Image.open(io.BytesIO(content)) as image:
        if image.format != "PNG" or max(image.size)>4096:
            raise ValueError("unexpected document format or dimensions")
        text = pytesseract.image_to_string(image, lang="eng", config="--psm 6", timeout=15)
    return verify_number(text, event.cnh_number)

def run(stop, health):
    state = State(os.environ.get("STATE_PATH", "/data/risk.db"))
    bootstrap = os.environ.get("KAFKA_BOOTSTRAP_SERVERS", "kafka:9092")
    consumer = Consumer({"bootstrap.servers":bootstrap, "group.id":"risk-pricing-v1",
                         "enable.auto.commit":False, "auto.offset.reset":"earliest"})
    producer = Producer({"bootstrap.servers":bootstrap, "enable.idempotence":True, "message.timeout.ms":5000})
    consumer.subscribe(["document.stored", "rider.verified", "rental.started", "rental.closed"])
    last_periodic = 0
    try:
        while not stop.is_set():
            message = None
            try:
                now = int(time.time()*1000)
                if now-last_periodic>=300_000:
                    state.periodic(now, int(os.environ.get("FLEET_CAPACITY", "100")))
                    last_periodic = now
                for event_id, topic, key, payload, parent in state.db.execute("SELECT * FROM outbox LIMIT 100").fetchall():
                    delivered = Future()
                    with tracer.start_as_current_span("publish "+topic,
                            context=propagate.extract({"traceparent": parent} if parent else {}),
                            kind=trace.SpanKind.PRODUCER,
                            attributes={"messaging.system":"kafka", "messaging.destination.name":topic}):
                        carrier = {}
                        propagate.inject(carrier)
                        producer.produce(topic, key=key, value=payload, headers=list(carrier.items()),
                                         on_delivery=lambda error, _msg, result=delivered: result.set_result(error))
                        if producer.flush(6) or not delivered.done() or delivered.result():
                            raise RuntimeError("Kafka delivery not acknowledged")
                    with state.db:
                        state.db.execute("DELETE FROM outbox WHERE id=?", (event_id,))
                message = consumer.poll(1)
                health["last_poll"] = time.monotonic()
                health["oldest_score_ms"] = state.db.execute("SELECT MIN(scored_at) FROM riders").fetchone()[0] or 0
                if message is None:
                    continue
                if message.error():
                    raise RuntimeError(str(message.error()))
                event = (RentalEvent if message.topic().startswith("rental.") else RiderEvent)()
                event.ParseFromString(message.value())
                headers = {k:v.decode() for k,v in (message.headers() or []) if v is not None}
                with tracer.start_as_current_span("process "+message.topic(), context=propagate.extract(headers),
                                                 kind=trace.SpanKind.CONSUMER,
                                                 attributes={"messaging.system":"kafka", "messaging.destination.name":message.topic()}):
                    verified = ocr(event) if message.topic()=="document.stored" else None
                    carrier = {}
                    propagate.inject(carrier)
                    state.apply(message.topic(), event, now, verified, carrier.get("traceparent"))
                    consumer.commit(message=message, asynchronous=False)
                    health["last_event_ms"] = now
                    log.info("event processed", extra={"event_type":message.topic()})
            except Exception:
                health["last_poll"] = 0
                log.exception("processing delayed; event and outbox retained")
                if message is not None and not message.error():
                    consumer.seek(TopicPartition(message.topic(), message.partition(), message.offset()))
                stop.wait(2)
    finally:
        consumer.close()
        producer.flush(5)
        state.db.close()
