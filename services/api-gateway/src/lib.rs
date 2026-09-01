pub mod config;

use std::{future::Future, sync::Arc};

use axum::{
    Json, Router,
    body::Body,
    extract::{Request, State},
    http::{HeaderMap, HeaderName, StatusCode},
    response::{IntoResponse, Response},
    routing::get,
};
use config::{Config, UpstreamName};
use reqwest::redirect::Policy;
use serde::Serialize;
use tokio::net::TcpListener;
use tracing::{info, warn};
use url::Url;

#[derive(Clone)]
struct AppState {
    config: Config,
    client: reqwest::Client,
}

#[derive(Serialize)]
struct HealthStatus {
    status: &'static str,
}

#[derive(Serialize)]
struct Problem {
    #[serde(rename = "type")]
    problem_type: &'static str,
    title: &'static str,
    status: u16,
    detail: &'static str,
}

pub fn build_app(config: Config) -> Result<Router, reqwest::Error> {
    let client = reqwest::Client::builder()
        .redirect(Policy::none())
        .build()?;
    Ok(Router::new()
        .route("/health/live", get(health))
        .route("/health/ready", get(health))
        .fallback(proxy)
        .with_state(Arc::new(AppState { config, client })))
}

pub async fn serve(
    listener: TcpListener,
    app: Router,
    shutdown: impl Future<Output = ()> + Send + 'static,
) -> std::io::Result<()> {
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown)
        .await
}

pub async fn healthcheck(url: Url, timeout: std::time::Duration) -> Result<(), String> {
    let response = reqwest::Client::builder()
        .timeout(timeout)
        .redirect(Policy::none())
        .build()
        .map_err(|error| format!("could not create healthcheck client: {error}"))?
        .get(url)
        .send()
        .await
        .map_err(|error| format!("gateway healthcheck failed: {error}"))?;

    if response.status().is_success() {
        Ok(())
    } else {
        Err(format!(
            "gateway healthcheck returned HTTP {}",
            response.status()
        ))
    }
}

async fn health() -> Json<HealthStatus> {
    Json(HealthStatus { status: "healthy" })
}

async fn proxy(State(state): State<Arc<AppState>>, request: Request) -> Response {
    let path = request.uri().path();
    let Some((upstream_name, base_url)) = state.config.upstreams.resolve(path) else {
        return problem(
            StatusCode::NOT_FOUND,
            "Route not found",
            "The gateway has no upstream for this route.",
        );
    };

    match forward(&state.client, upstream_name, base_url, request).await {
        Ok(response) => response,
        Err(error) => {
            warn!(upstream = ?upstream_name, error = %error, "upstream request failed");
            problem(
                StatusCode::BAD_GATEWAY,
                "Upstream unavailable",
                "The upstream service could not complete the request.",
            )
        }
    }
}

async fn forward(
    client: &reqwest::Client,
    upstream_name: UpstreamName,
    base_url: &Url,
    request: Request,
) -> Result<Response, reqwest::Error> {
    let (parts, body) = request.into_parts();
    let mut target = base_url
        .join(parts.uri.path().trim_start_matches('/'))
        .expect("validated base URL accepts relative paths");
    target.set_query(parts.uri.query());

    let connection_headers = connection_header_names(&parts.headers);
    let mut upstream_request = client
        .request(parts.method, target)
        .body(reqwest::Body::wrap_stream(body.into_data_stream()));
    for (name, value) in &parts.headers {
        if !is_hop_by_hop(name, &connection_headers) {
            upstream_request = upstream_request.header(name, value);
        }
    }

    info!(upstream = ?upstream_name, path = %parts.uri, "proxying request");
    let upstream_response = upstream_request.send().await?;
    let status = upstream_response.status();
    let response_headers = upstream_response.headers().clone();
    let connection_headers = connection_header_names(&response_headers);
    let mut response = Response::builder().status(status);
    for (name, value) in &response_headers {
        if !is_hop_by_hop(name, &connection_headers) {
            response = response.header(name, value);
        }
    }

    Ok(response
        .body(Body::from_stream(upstream_response.bytes_stream()))
        .expect("upstream response status and headers are valid"))
}

fn connection_header_names(headers: &HeaderMap) -> Vec<HeaderName> {
    headers
        .get_all(http::header::CONNECTION)
        .iter()
        .filter_map(|value| value.to_str().ok())
        .flat_map(|value| value.split(','))
        .filter_map(|name| HeaderName::from_bytes(name.trim().as_bytes()).ok())
        .collect()
}

fn is_hop_by_hop(name: &HeaderName, connection_headers: &[HeaderName]) -> bool {
    matches!(
        name.as_str(),
        "connection"
            | "keep-alive"
            | "proxy-authenticate"
            | "proxy-authorization"
            | "te"
            | "trailer"
            | "transfer-encoding"
            | "upgrade"
            | "host"
    ) || connection_headers.contains(name)
}

fn problem(status: StatusCode, title: &'static str, detail: &'static str) -> Response {
    let mut response = (
        status,
        Json(Problem {
            problem_type: "about:blank",
            title,
            status: status.as_u16(),
            detail,
        }),
    )
        .into_response();
    response.headers_mut().insert(
        http::header::CONTENT_TYPE,
        http::HeaderValue::from_static("application/problem+json"),
    );
    response
}

#[cfg(test)]
mod tests {
    use std::{net::SocketAddr, sync::Arc, time::Duration};

    use axum::{
        body::to_bytes, extract::State as AxumState, http::Request as HttpRequest, routing::get,
    };
    use config::Upstreams;
    use tokio::sync::{Notify, oneshot};
    use tower::ServiceExt;

    use super::*;

    fn test_config(upstream: Url) -> Config {
        Config {
            bind: SocketAddr::from(([127, 0, 0, 1], 0)),
            health_url: Url::parse("http://127.0.0.1/health/ready").unwrap(),
            healthcheck_timeout: Duration::from_secs(1),
            upstreams: Upstreams {
                auth_gate: upstream.clone(),
                rider_manager: upstream.clone(),
                moto_hub: upstream.clone(),
                rental_operations: upstream,
            },
        }
    }

    async fn spawn_echo_upstream() -> Url {
        async fn echo(request: Request) -> Response {
            let uri = request.uri().to_string();
            let marker = request
                .headers()
                .get("x-test-marker")
                .and_then(|value| value.to_str().ok())
                .unwrap_or_default()
                .to_owned();
            let body = to_bytes(request.into_body(), 1024).await.unwrap();
            (
                StatusCode::CREATED,
                [("x-upstream", "auth-gate")],
                format!("{uri}|{marker}|{}", String::from_utf8_lossy(&body)),
            )
                .into_response()
        }

        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        tokio::spawn(async move {
            axum::serve(listener, Router::new().fallback(echo))
                .await
                .unwrap();
        });
        Url::parse(&format!("http://{address}/")).unwrap()
    }

    #[tokio::test]
    async fn proxies_method_path_query_headers_and_body() {
        let upstream = spawn_echo_upstream().await;
        let app = build_app(test_config(upstream)).unwrap();
        let response = app
            .oneshot(
                HttpRequest::post("/api/auth/login?return=console")
                    .header("x-test-marker", "forwarded")
                    .header("connection", "x-remove-me")
                    .header("x-remove-me", "secret")
                    .body(Body::from("credentials"))
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::CREATED);
        assert_eq!(response.headers()["x-upstream"], "auth-gate");
        let body = to_bytes(response.into_body(), 1024).await.unwrap();
        assert_eq!(body, "/api/auth/login?return=console|forwarded|credentials");
    }

    #[tokio::test]
    async fn serves_local_health_without_an_upstream() {
        let upstream = Url::parse("http://127.0.0.1:1/").unwrap();
        let app = build_app(test_config(upstream)).unwrap();
        let response = app
            .oneshot(
                HttpRequest::get("/health/ready")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
    }

    #[tokio::test]
    async fn rejects_routes_that_are_not_explicitly_owned() {
        let upstream = Url::parse("http://127.0.0.1:1/").unwrap();
        let app = build_app(test_config(upstream)).unwrap();
        let response = app
            .oneshot(
                HttpRequest::get("/api/unknown")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::NOT_FOUND);
        assert_eq!(
            response.headers()[http::header::CONTENT_TYPE],
            "application/problem+json"
        );
    }

    #[tokio::test]
    async fn graceful_shutdown_drains_an_in_flight_request() {
        struct DrainState {
            started: Notify,
            release: Notify,
        }

        async fn slow(AxumState(state): AxumState<Arc<DrainState>>) -> &'static str {
            state.started.notify_one();
            state.release.notified().await;
            "drained"
        }

        let drain = Arc::new(DrainState {
            started: Notify::new(),
            release: Notify::new(),
        });
        let upstream_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let upstream_address = upstream_listener.local_addr().unwrap();
        let upstream_app = Router::new()
            .route("/api/auth/slow", get(slow))
            .with_state(drain.clone());
        tokio::spawn(async move {
            axum::serve(upstream_listener, upstream_app).await.unwrap();
        });

        let gateway_listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let gateway_address = gateway_listener.local_addr().unwrap();
        let upstream = Url::parse(&format!("http://{upstream_address}/")).unwrap();
        let app = build_app(test_config(upstream)).unwrap();
        let (shutdown_tx, shutdown_rx) = oneshot::channel();
        let server = tokio::spawn(serve(gateway_listener, app, async move {
            let _ = shutdown_rx.await;
        }));

        let request = tokio::spawn(async move {
            reqwest::get(format!("http://{gateway_address}/api/auth/slow"))
                .await
                .unwrap()
                .text()
                .await
                .unwrap()
        });
        drain.started.notified().await;
        shutdown_tx.send(()).unwrap();
        drain.release.notify_one();

        assert_eq!(request.await.unwrap(), "drained");
        tokio::time::timeout(Duration::from_secs(1), server)
            .await
            .expect("server did not finish draining")
            .unwrap()
            .unwrap();
    }
}
