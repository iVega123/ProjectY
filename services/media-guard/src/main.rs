use axum::{
    Json, Router,
    body::Bytes,
    extract::{DefaultBodyLimit, State},
    http::{HeaderMap, StatusCode},
    routing::{get, post},
};
use base64::{Engine, engine::general_purpose::STANDARD};
use opentelemetry::global;
use opentelemetry_http::HeaderExtractor;
use serde::Serialize;
use std::{sync::Arc, time::Duration};
use tokio::sync::Semaphore;
use tracing::Instrument;
use tracing_opentelemetry::OpenTelemetrySpanExt;
mod telemetry;

#[derive(Serialize)]
struct Output {
    image: String,
    thumbnail: String,
    content_type: &'static str,
}

async fn convert(
    State(slots): State<Arc<Semaphore>>,
    headers: HeaderMap,
    bytes: Bytes,
) -> Result<Json<Output>, (StatusCode, &'static str)> {
    let permit = slots
        .try_acquire_owned()
        .map_err(|_| (StatusCode::SERVICE_UNAVAILABLE, "image workers busy"))?;
    let parent = global::get_text_map_propagator(|p| p.extract(&HeaderExtractor(&headers)));
    let span = tracing::info_span!(
        "POST /sanitize",
        otel.kind = "server",
        http.request.method = "POST",
        http.route = "/sanitize"
    );
    let _ = span.set_parent(parent);
    async move {
        let result = tokio::task::spawn_blocking(move || {
            let _permit = permit;
            media_guard::sanitize(&bytes)
        })
        .await
        .map_err(|_| (StatusCode::INTERNAL_SERVER_ERROR, "image worker failed"))?
        .map_err(|message| (StatusCode::UNPROCESSABLE_ENTITY, message))?;
        Ok(Json(Output {
            image: STANDARD.encode(result.image),
            thumbnail: STANDARD.encode(result.thumbnail),
            content_type: "image/png",
        }))
    }
    .instrument(span)
    .await
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    if std::env::args().any(|arg| arg == "--healthcheck") {
        reqwest::Client::builder()
            .timeout(Duration::from_secs(2))
            .build()?
            .get("http://127.0.0.1:8092/health/ready")
            .send()
            .await?
            .error_for_status()?;
        return Ok(());
    }
    let telemetry = telemetry::TelemetryGuard::initialize()?;
    let app = Router::new()
        .route("/health/live", get(|| async { "ok" }))
        .route("/health/startup", get(|| async { "ok" }))
        .route("/health/ready", get(|| async { "ok" }))
        .route("/sanitize", post(convert))
        .layer(DefaultBodyLimit::max(media_guard::MAX_BYTES))
        .with_state(Arc::new(Semaphore::new(2)));
    let listener = tokio::net::TcpListener::bind("0.0.0.0:8092").await?;
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown())
        .await?;
    telemetry.shutdown();
    Ok(())
}

async fn shutdown() {
    #[cfg(unix)]
    {
        let mut term =
            tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()).unwrap();
        tokio::select! { _ = term.recv() => {}, _ = tokio::signal::ctrl_c() => {} }
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
}
