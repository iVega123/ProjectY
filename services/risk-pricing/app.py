import asyncio
import logging
import threading
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, Response
from opentelemetry import metrics, trace
from opentelemetry.exporter.otlp.proto.http._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

from worker import run

health = {"last_poll":0, "last_event_ms":0}

@asynccontextmanager
async def lifespan(app):
    resource = Resource.create({"service.name":"risk-pricing"})
    provider = TracerProvider(resource=resource)
    provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
    trace.set_tracer_provider(provider)
    meter_provider = MeterProvider(resource=resource, metric_readers=[PeriodicExportingMetricReader(OTLPMetricExporter())])
    metrics.set_meter_provider(meter_provider)
    metrics.get_meter("risk-pricing").create_observable_gauge("projecty.risk.last_event_age_seconds",
      callbacks=[lambda _: [metrics.Observation((int(time.time()*1000)-health["last_event_ms"])/1000 if health["last_event_ms"] else 86400)]])
    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(message)s')
    logs = LoggerProvider(resource=resource)
    logs.add_log_record_processor(BatchLogRecordProcessor(OTLPLogExporter()))
    logging.getLogger().addHandler(LoggingHandler(level=logging.INFO, logger_provider=logs))
    metrics.get_meter("risk-pricing").create_observable_gauge("projecty.risk.oldest_score_age_seconds",
      callbacks=[lambda _: [metrics.Observation(max(0, time.time()-health.get("oldest_score_ms", 0)/1000)
        if health.get("oldest_score_ms") else 0)]])
    stop = threading.Event()
    thread = threading.Thread(target=run, args=(stop,health), daemon=True)
    thread.start()
    app.state.worker = thread
    yield
    stop.set()
    await asyncio.to_thread(thread.join, 25)
    provider.shutdown()
    meter_provider.shutdown()
    logs.shutdown()

app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
FastAPIInstrumentor.instrument_app(app)

@app.get("/health/live")
def live(): return {"status":"alive"}

@app.get("/health/startup")
def startup(response: Response):
    if not getattr(app.state, "worker", None) or not app.state.worker.is_alive():
        response.status_code=503
    return {"status":"started"}

@app.get("/health/ready")
def ready(response: Response):
    available = time.monotonic()-health["last_poll"]<15
    if not available:
        response.status_code=503
    return {"status":"ready" if available else "delayed"}
