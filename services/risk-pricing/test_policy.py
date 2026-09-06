from policy import verify_number, pricing, score
from state import State
from rider_pb2 import RiderEvent
from rental_pb2 import RentalEvent
import pytest


def test_deleted_document_does_not_block_replacement_or_other_riders(monkeypatch, tmp_path):
    import worker
    from botocore.exceptions import ClientError

    class Storage:
        def get_object(self, **kwargs):
            raise ClientError({"Error": {"Code": "NoSuchKey"}}, "GetObject")

    monkeypatch.setenv("S3_ENDPOINT", "http://storage")
    monkeypatch.setenv("S3_BUCKET", "test")
    monkeypatch.setattr(worker.boto3, "client", lambda *args, **kwargs: Storage())
    state = State(str(tmp_path / "deleted.db"))
    old = RiderEvent(event_id="deleted", rider_id="r1", occurred_at_ms=100,
                     object_key="riders/r1/old.png")
    assert state.apply("document.stored", old, 101, worker.ocr(old))
    replacement = RiderEvent(event_id="replacement", rider_id="r1", occurred_at_ms=200)
    assert state.apply("document.stored", replacement, 201, True)
    other = RiderEvent(event_id="other", rider_id="r2", occurred_at_ms=200)
    assert state.apply("document.stored", other, 202, True)
    assert not state.apply("document.stored", old, 203, worker.ocr(old))
    assert state.db.execute("SELECT verified FROM riders ORDER BY id").fetchall() == [(1,), (1,)]
    assert state.db.execute("SELECT COUNT(*) FROM inbox").fetchone()[0] == 3
    state.db.close()


@pytest.mark.parametrize("code", ["AccessDenied", "NoSuchBucket", "InternalError", "SlowDown"])
def test_storage_faults_remain_retryable(monkeypatch, code):
    import worker
    from botocore.exceptions import ClientError

    class Storage:
        def get_object(self, **kwargs):
            raise ClientError({"Error": {"Code": code}}, "GetObject")

    monkeypatch.setenv("S3_ENDPOINT", "http://storage")
    monkeypatch.setenv("S3_BUCKET", "test")
    monkeypatch.setattr(worker.boto3, "client", lambda *args, **kwargs: Storage())
    with pytest.raises(ClientError):
        worker.ocr(RiderEvent(object_key="riders/r1/document.png"))

def test_real_tesseract_on_sanitized_document(monkeypatch):
    import io
    from PIL import Image, ImageDraw, ImageFont
    import worker
    image = Image.new("RGB", (900, 160), "white")
    ImageDraw.Draw(image).text((20, 40), "CNH 12345678901", fill="black", font=ImageFont.load_default(size=48))
    data = io.BytesIO()
    image.save(data, format="PNG")
    class Storage:
        def get_object(self, **kwargs):
            return {"Body": io.BytesIO(data.getvalue())}
    monkeypatch.setenv("S3_ENDPOINT", "http://storage")
    monkeypatch.setenv("S3_BUCKET", "test")
    monkeypatch.setattr(worker.boto3, "client", lambda *args, **kwargs: Storage())
    assert worker.ocr(RiderEvent(object_key="riders/test/document.png", cnh_number="12345678901"))
    assert not worker.ocr(RiderEvent(object_key="riders/test/document.png", cnh_number="10987654321"))

def test_ocr_does_not_join_unrelated_digits():
    assert verify_number("CNH 12345678901", "12345678901")
    assert not verify_number("12345 number 678901", "12345678901")
    assert not verify_number("9123456789012", "12345678901")

def test_default_is_conservative_and_demand_is_bounded():
    assert score(False,0,0)>score(True,0,0)
    assert score(True,5,5)>score(True,0,5)
    assert pricing(0,100,1)["tiers"][0]["daily_minor"]==3000
    assert pricing(10000,100,1)["tiers"][0]["daily_minor"]==3900

def test_inbox_and_outbox_survive_restart_and_late_event(tmp_path):
    path = str(tmp_path/"state.db")
    state = State(path)
    event = RiderEvent(event_id="d1",rider_id="r1",occurred_at_ms=100,cnh_number="12345678901")
    assert state.apply("document.stored",event,101,True)
    state.db.close()
    state = State(path)
    assert not state.apply("document.stored",event,102,False)
    assert state.db.execute("SELECT COUNT(*) FROM outbox").fetchone()[0]==2
    old = RiderEvent(event_id="d0",rider_id="r1",occurred_at_ms=90)
    state.apply("document.stored",old,103,False)
    assert state.db.execute("SELECT verified FROM riders").fetchone()[0]==1
    close = RentalEvent(event_id="closed",rental_id="rental",rider_id="r1",occurred_at_ms=200,ended_at_ms=2,predicted_end_at_ms=1)
    start = RentalEvent(event_id="started",rental_id="rental",rider_id="r1",occurred_at_ms=100)
    state.apply("rental.closed",close,201)
    state.apply("rental.started",start,202)
    assert state.db.execute("SELECT closed,late FROM rentals").fetchone()==(1,1)
