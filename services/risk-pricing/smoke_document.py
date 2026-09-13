"""Exercise media-guard -> private S3 -> Kafka -> actual worker OCR in the test stack."""
import base64
import io
import json
import os
import time
import urllib.request
import uuid

import boto3
from confluent_kafka import Consumer, Producer
from PIL import Image, ImageDraw, ImageFont

from rider_pb2 import RiderEvent

identity = 'document-smoke-' + str(uuid.uuid4())
image = Image.new('RGB', (900,160), 'white')
ImageDraw.Draw(image).text((20,40),'CNH 12345678901',fill='black',font=ImageFont.load_default(size=48))
source=io.BytesIO()
image.save(source,format='PNG')
request=urllib.request.Request('http://media-guard:8092/sanitize',data=source.getvalue(),method='POST')
with urllib.request.urlopen(request,timeout=15) as response:
    sanitized=json.load(response)
s3=boto3.client('s3',endpoint_url=os.environ['S3_ENDPOINT'])
bucket=os.environ['S3_BUCKET']
if bucket not in [b['Name'] for b in s3.list_buckets()['Buckets']]:
    s3.create_bucket(Bucket=bucket)
key='riders/'+identity+'/document.png'
s3.put_object(Bucket=bucket,Key=key,Body=base64.b64decode(sanitized['image']),ContentType='image/png')
s3.put_object(Bucket=bucket,Key=key+'.thumb.png',Body=base64.b64decode(sanitized['thumbnail']),ContentType='image/png')
consumer=Consumer({'bootstrap.servers':'kafka:9092','group.id':identity,'auto.offset.reset':'earliest'})
consumer.subscribe(['document.verified'])
producer=Producer({'bootstrap.servers':'kafka:9092'})
event=RiderEvent(event_id=identity,rider_id=identity,occurred_at_ms=int(time.time()*1000),object_key=key,cnh_number='12345678901')
producer.produce('document.stored',key=identity,value=event.SerializeToString())
assert producer.flush(10)==0
verified=False
try:
    deadline=time.monotonic()+60
    while time.monotonic()<deadline:
        message=consumer.poll(1)
        if message is None or message.error():
            continue
        fact=RiderEvent.FromString(message.value())
        if fact.rider_id==identity:
            assert fact.HasField('verified') and fact.verified
            verified=True
            break
    assert verified,'Worker did not verify the stored sanitized document'
    print('PASS: media sanitization, private S3 storage, document.stored and actual worker OCR -> document.verified')
finally:
    consumer.close()
    # Delete only this probe's explicitly named objects.
    s3.delete_object(Bucket=bucket,Key=key)
    s3.delete_object(Bucket=bucket,Key=key+'.thumb.png')
