use std::{env, error::Error};

use opentelemetry::{global, trace::TracerProvider as _};
use opentelemetry_appender_tracing::layer::OpenTelemetryTracingBridge;
use opentelemetry_sdk::{
    Resource, logs::SdkLoggerProvider, propagation::TraceContextPropagator,
    trace::SdkTracerProvider,
};
use tracing_appender::non_blocking::WorkerGuard;
use tracing_subscriber::{EnvFilter, layer::SubscriberExt, util::SubscriberInitExt};

pub struct TelemetryGuard {
    tracer_provider: SdkTracerProvider,
    logger_provider: SdkLoggerProvider,
    _stdout_guard: WorkerGuard,
}

impl TelemetryGuard {
    pub fn initialize() -> Result<Self, Box<dyn Error + Send + Sync>> {
        let service_name =
            env::var("OTEL_SERVICE_NAME").unwrap_or_else(|_| "media-guard".to_owned());
        let resource = Resource::builder().with_service_name(service_name).build();

        let span_exporter = opentelemetry_otlp::SpanExporter::builder()
            .with_tonic()
            .build()?;
        let tracer_provider = SdkTracerProvider::builder()
            .with_resource(resource.clone())
            .with_batch_exporter(span_exporter)
            .build();
        global::set_tracer_provider(tracer_provider.clone());
        global::set_text_map_propagator(TraceContextPropagator::new());

        let log_exporter = opentelemetry_otlp::LogExporter::builder()
            .with_tonic()
            .build()?;
        let logger_provider = SdkLoggerProvider::builder()
            .with_resource(resource)
            .with_batch_exporter(log_exporter)
            .build();

        let tracer = tracer_provider.tracer("media-guard");
        let trace_layer = tracing_opentelemetry::layer().with_tracer(tracer);
        let log_layer = OpenTelemetryTracingBridge::new(&logger_provider);
        let (writer, stdout_guard) = tracing_appender::non_blocking(std::io::stdout());

        tracing_subscriber::registry()
            .with(EnvFilter::try_from_default_env().unwrap_or_else(|_| "info".into()))
            .with(tracing_subscriber::fmt::layer().json().with_writer(writer))
            .with(trace_layer)
            .with(log_layer)
            .try_init()?;

        Ok(Self {
            tracer_provider,
            logger_provider,
            _stdout_guard: stdout_guard,
        })
    }

    pub fn shutdown(self) {
        if let Err(error) = self.tracer_provider.shutdown() {
            eprintln!("failed to flush OpenTelemetry traces: {error}");
        }
        if let Err(error) = self.logger_provider.shutdown() {
            eprintln!("failed to flush OpenTelemetry logs: {error}");
        }
    }
}
