pub mod auth;
pub mod config;
pub mod metrics;
pub mod policy;
pub mod rate_limit;
pub mod revocation;

use std::{fmt, future::Future, net::SocketAddr, sync::Arc};

use auth::{AuthError, Authenticator, IdentitySigner, has_reserved_identity_header, is_admin};
use axum::{
    Json, Router,
    body::Body,
    extract::{ConnectInfo, Request, State},
    http::{HeaderMap, HeaderName, HeaderValue, StatusCode, header::WWW_AUTHENTICATE},
    response::{IntoResponse, Response},
    routing::get,
};
use config::{Config, TokenBucketConfig, UpstreamName};
use metrics::GatewayMetrics;
use policy::{Access, access_for, is_canonical_path, requires_revocation_check};
use rate_limit::{RateLimitDecision, RateLimitError, RateLimiter, RedisRateLimiter};
use reqwest::redirect::Policy;
use revocation::{RedisRevocationStore, RevocationError, RevocationStore};
use serde::Serialize;
use sha2::{Digest, Sha256};
use tokio::net::TcpListener;
use tracing::{info, warn};
use url::Url;

struct AppState {
    config: Config,
    client: reqwest::Client,
    authenticator: Authenticator,
    identity_signer: IdentitySigner,
    revocation: Arc<dyn RevocationStore>,
    rate_limiter: Arc<dyn RateLimiter>,
    metrics: Arc<GatewayMetrics>,
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
    instance: String,
}

#[derive(Debug)]
pub enum BuildError {
    Http(reqwest::Error),
    Redis,
}

impl fmt::Display for BuildError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Http(error) => write!(formatter, "could not build HTTP client: {error}"),
            Self::Redis => formatter.write_str("GATEWAY_REDIS_URL is invalid"),
        }
    }
}

impl std::error::Error for BuildError {}

pub fn build_app(config: Config) -> Result<Router, BuildError> {
    let revocation = Arc::new(
        RedisRevocationStore::new(config.auth.redis_url.expose(), config.auth.redis_timeout)
            .map_err(|_| BuildError::Redis)?,
    );
    let rate_limiter = Arc::new(
        RedisRateLimiter::new(
            config.rate_limit.redis_url.expose(),
            config.rate_limit.operation_timeout,
        )
        .map_err(|_| BuildError::Redis)?,
    );
    build_app_with_dependencies(config, revocation, rate_limiter).map_err(BuildError::Http)
}

#[cfg(test)]
fn build_app_with_revocation(
    config: Config,
    revocation: Arc<dyn RevocationStore>,
) -> Result<Router, reqwest::Error> {
    let rate_limiter = Arc::new(
        RedisRateLimiter::new(
            config.rate_limit.redis_url.expose(),
            config.rate_limit.operation_timeout,
        )
        .expect("test rate-limit Redis URL is valid"),
    );
    build_app_with_dependencies(config, revocation, rate_limiter)
}

fn build_app_with_dependencies(
    config: Config,
    revocation: Arc<dyn RevocationStore>,
    rate_limiter: Arc<dyn RateLimiter>,
) -> Result<Router, reqwest::Error> {
    let client = reqwest::Client::builder()
        .redirect(Policy::none())
        .build()?;
    let authenticator = Authenticator::new(config.auth.clone(), client.clone());
    let identity_signer = IdentitySigner::new(&config.auth);
    let gateway_metrics = Arc::new(GatewayMetrics::default());
    Ok(Router::new()
        .route("/health/live", get(health))
        .route("/health/ready", get(health))
        .route("/metrics", get(metrics))
        .fallback(proxy)
        .with_state(Arc::new(AppState {
            config,
            client,
            authenticator,
            identity_signer,
            revocation,
            rate_limiter,
            metrics: gateway_metrics,
        })))
}

pub async fn serve(
    listener: TcpListener,
    app: Router,
    shutdown: impl Future<Output = ()> + Send + 'static,
) -> std::io::Result<()> {
    axum::serve(
        listener,
        app.into_make_service_with_connect_info::<SocketAddr>(),
    )
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

async fn metrics(State(state): State<Arc<AppState>>) -> Response {
    (
        [(
            http::header::CONTENT_TYPE,
            "text/plain; version=0.0.4; charset=utf-8",
        )],
        state.metrics.render(),
    )
        .into_response()
}

async fn proxy(State(state): State<Arc<AppState>>, request: Request) -> Response {
    let path = request.uri().path().to_owned();
    if !is_canonical_path(&path) {
        return problem(
            StatusCode::BAD_REQUEST,
            "urn:projecty:problem:non-canonical-path",
            "Non-canonical request path",
            "Percent-encoded, ambiguous, or trailing path separators are not accepted.",
            request.uri().to_string(),
        );
    }
    if has_reserved_identity_header(request.headers()) {
        return problem(
            StatusCode::BAD_REQUEST,
            "urn:projecty:problem:reserved-identity-header",
            "Reserved identity header",
            "Clients must not send x-identity-* headers.",
            request.uri().to_string(),
        );
    }
    let Some((upstream_name, base_url)) = state.config.upstreams.resolve(&path) else {
        return problem(
            StatusCode::NOT_FOUND,
            "urn:projecty:problem:route-not-found",
            "Route not found",
            "The gateway has no upstream for this route.",
            request.uri().to_string(),
        );
    };

    let audience = state.config.auth.audiences.for_upstream(upstream_name);
    let access = access_for(request.method(), &path, upstream_name);
    let (identity_headers, identity_subject) = match access {
        Access::Public => (None, None),
        Access::Authenticated | Access::Admin => {
            let identity = match state
                .authenticator
                .authenticate(request.headers(), audience)
                .await
            {
                Ok(identity) => identity,
                Err(error) => return authentication_problem(error, request.uri()),
            };
            if access == Access::Admin && !is_admin(&identity) {
                return problem(
                    StatusCode::FORBIDDEN,
                    "urn:projecty:problem:insufficient-role",
                    "Forbidden",
                    "This route requires the Admin role.",
                    request.uri().to_string(),
                );
            }
            if requires_revocation_check(request.method(), &path, upstream_name) {
                match state.revocation.is_revoked(&identity.token_id).await {
                    Ok(false) => {}
                    Ok(true) => {
                        return authentication_problem(AuthError::InvalidToken, request.uri());
                    }
                    Err(RevocationError::Unavailable) => {
                        return revocation_unavailable_problem(request.uri());
                    }
                }
            }
            let identity_headers = match state.identity_signer.headers(
                &identity,
                request.method(),
                request.uri(),
                audience,
            ) {
                Ok(headers) => headers,
                Err(error) => return authentication_problem(error, request.uri()),
            };
            (Some(identity_headers), Some(identity.subject))
        }
    };

    let (bucket_name, bucket) = rate_limit_bucket(&state.config, upstream_name);
    let principal = rate_limit_principal(&request, identity_subject);
    let rate_limit_key = rate_limit_key(bucket_name, &principal);
    let remaining = match state.rate_limiter.check(&rate_limit_key, bucket).await {
        Ok(RateLimitDecision {
            allowed: true,
            remaining,
            ..
        }) => Some(remaining),
        Ok(decision) => return rate_limit_problem(request.uri(), decision),
        Err(RateLimitError::Unavailable) => {
            state.metrics.record_rate_limit_degraded();
            warn!(
                bucket = bucket_name,
                "rate limiter unavailable; request allowed"
            );
            None
        }
    };

    let mut response = match forward(
        &state.client,
        upstream_name,
        base_url,
        request,
        identity_headers,
    )
    .await
    {
        Ok(response) => response,
        Err(error) => {
            warn!(upstream = ?upstream_name, error = %error, "upstream request failed");
            problem(
                StatusCode::BAD_GATEWAY,
                "urn:projecty:problem:upstream-unavailable",
                "Upstream unavailable",
                "The upstream service could not complete the request.",
                path,
            )
        }
    };
    if let Some(remaining) = remaining {
        response.headers_mut().insert(
            "x-ratelimit-remaining",
            HeaderValue::from_str(&remaining.to_string())
                .expect("remaining token count is a valid header"),
        );
    }
    response
}

fn rate_limit_key(bucket: &str, principal: &str) -> String {
    let digest = Sha256::digest(principal.as_bytes());
    format!("projecty:ratelimit:{bucket}:{digest:x}")
}

fn rate_limit_bucket(config: &Config, upstream: UpstreamName) -> (&'static str, TokenBucketConfig) {
    if upstream == UpstreamName::AuthGate {
        ("auth", config.rate_limit.auth)
    } else {
        ("general", config.rate_limit.general)
    }
}

fn rate_limit_principal(request: &Request, identity_subject: Option<String>) -> String {
    match identity_subject {
        Some(subject) => format!("identity:{subject}"),
        None => {
            let origin = request
                .extensions()
                .get::<ConnectInfo<SocketAddr>>()
                .map(|address| address.0.ip().to_string())
                .unwrap_or_else(|| "unknown".to_owned());
            format!("origin:{origin}")
        }
    }
}

async fn forward(
    client: &reqwest::Client,
    upstream_name: UpstreamName,
    base_url: &Url,
    request: Request,
    identity_headers: Option<HeaderMap>,
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
        if !is_hop_by_hop(name, &connection_headers) && !is_sensitive_client_header(name) {
            upstream_request = upstream_request.header(name, value);
        }
    }
    if let Some(identity_headers) = identity_headers {
        for (name, value) in identity_headers {
            if let Some(name) = name {
                upstream_request = upstream_request.header(name, value);
            }
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

fn is_sensitive_client_header(name: &HeaderName) -> bool {
    matches!(name.as_str(), "authorization" | "cookie") || name.as_str().starts_with("x-identity-")
}

fn authentication_problem(error: AuthError, instance: &axum::http::Uri) -> Response {
    let mut response = match error {
        AuthError::JwksUnavailable => problem(
            StatusCode::SERVICE_UNAVAILABLE,
            "urn:projecty:problem:identity-keys-unavailable",
            "Identity keys unavailable",
            "The gateway could not resolve trusted identity keys.",
            instance.to_string(),
        ),
        AuthError::MissingToken | AuthError::InvalidToken => problem(
            StatusCode::UNAUTHORIZED,
            "urn:projecty:problem:invalid-token",
            "Unauthorized",
            "A valid bearer token is required.",
            instance.to_string(),
        ),
    };
    if response.status() == StatusCode::UNAUTHORIZED {
        response.headers_mut().insert(
            WWW_AUTHENTICATE,
            http::HeaderValue::from_static("Bearer error=\"invalid_token\""),
        );
    }
    response
}

fn revocation_unavailable_problem(instance: &axum::http::Uri) -> Response {
    let mut response = problem(
        StatusCode::SERVICE_UNAVAILABLE,
        "urn:projecty:problem:revocation-unavailable",
        "Revocation check unavailable",
        "This high-value operation cannot proceed without a revocation check.",
        instance.to_string(),
    );
    response.headers_mut().insert(
        http::header::RETRY_AFTER,
        http::HeaderValue::from_static("1"),
    );
    response
}

fn rate_limit_problem(instance: &axum::http::Uri, decision: RateLimitDecision) -> Response {
    let mut response = problem(
        StatusCode::TOO_MANY_REQUESTS,
        "urn:projecty:problem:rate-limit-exceeded",
        "Too many requests",
        "The request rate for this caller exceeded the configured token bucket.",
        instance.to_string(),
    );
    response.headers_mut().insert(
        http::header::RETRY_AFTER,
        HeaderValue::from_str(&decision.retry_after_seconds.to_string())
            .expect("retry delay is a valid header"),
    );
    response.headers_mut().insert(
        "x-ratelimit-remaining",
        HeaderValue::from_str(&decision.remaining.to_string())
            .expect("remaining token count is a valid header"),
    );
    response
}

fn problem(
    status: StatusCode,
    problem_type: &'static str,
    title: &'static str,
    detail: &'static str,
    instance: String,
) -> Response {
    let mut response = (
        status,
        Json(Problem {
            problem_type,
            title,
            status: status.as_u16(),
            detail,
            instance,
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
    use std::{
        net::SocketAddr,
        sync::{
            Arc,
            atomic::{AtomicUsize, Ordering},
        },
        time::Duration,
    };

    use aws_lc_rs::{
        rand::SystemRandom,
        signature::{Ed25519KeyPair, KeyPair},
    };
    use axum::{
        body::to_bytes,
        extract::State as AxumState,
        http::{Request as HttpRequest, header::AUTHORIZATION},
        routing::get,
    };
    use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
    use config::{
        Audiences, AuthConfig, RateLimitConfig, Secret, SensitiveString, TokenBucketConfig,
        Upstreams,
    };
    use hmac::{Hmac, Mac};
    use jsonwebtoken::{Algorithm, EncodingKey, Header, encode};
    use serde::Serialize;
    use serde_json::{Value, json};
    use sha2::Sha256;
    use tokio::sync::{Notify, RwLock, oneshot};
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
            auth: AuthConfig {
                jwks_url: Url::parse("http://127.0.0.1:1/.well-known/jwks.json").unwrap(),
                issuer: "projecty.identity".to_owned(),
                audiences: Audiences {
                    auth_gate: "projecty.auth-gate".to_owned(),
                    rider_manager: "projecty.rider-manager".to_owned(),
                    moto_hub: "projecty.moto-hub".to_owned(),
                    rental_operations: "projecty.rental-operations".to_owned(),
                },
                jwks_cache_ttl: Duration::from_secs(300),
                unknown_kid_refresh_interval: Duration::from_secs(5),
                jwks_timeout: Duration::from_millis(100),
                clock_skew: Duration::from_secs(0),
                max_token_lifetime: Duration::from_secs(300),
                identity_signing_key: Secret::new(vec![b'x'; 32]).unwrap(),
                identity_signing_key_id: "local-v1".to_owned(),
                redis_url: SensitiveString::new("redis://127.0.0.1:1/".to_owned()),
                redis_timeout: Duration::from_millis(50),
            },
            rate_limit: RateLimitConfig {
                redis_url: SensitiveString::new("redis://127.0.0.1:1/".to_owned()),
                operation_timeout: Duration::from_millis(10),
                general: TokenBucketConfig {
                    capacity: 120,
                    refill_per_minute: 120,
                },
                auth: TokenBucketConfig {
                    capacity: 10,
                    refill_per_minute: 5,
                },
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

    struct TestIssuer {
        kid: String,
        private_key: Vec<u8>,
        public_key: Vec<u8>,
    }

    #[derive(Serialize)]
    struct TestClaims<'a> {
        sub: &'a str,
        iss: &'a str,
        aud: &'a str,
        exp: u64,
        nbf: u64,
        iat: u64,
        jti: &'a str,
        role: &'a [&'a str],
    }

    impl TestIssuer {
        fn new(kid: &str) -> Self {
            let document = Ed25519KeyPair::generate_pkcs8(&SystemRandom::new()).unwrap();
            let key_pair = Ed25519KeyPair::from_pkcs8(document.as_ref()).unwrap();
            Self {
                kid: kid.to_owned(),
                private_key: document.as_ref().to_vec(),
                public_key: key_pair.public_key().as_ref().to_vec(),
            }
        }

        fn jwks(&self) -> String {
            json!({
                "keys": [{
                    "kty": "OKP",
                    "crv": "Ed25519",
                    "use": "sig",
                    "alg": "EdDSA",
                    "kid": self.kid,
                    "x": URL_SAFE_NO_PAD.encode(&self.public_key),
                }]
            })
            .to_string()
        }

        fn token(&self, audience: &str, roles: &[&str]) -> String {
            self.token_with_lifetime(audience, roles, 300)
        }

        fn token_with_lifetime(&self, audience: &str, roles: &[&str], lifetime: u64) -> String {
            let now = jsonwebtoken::get_current_timestamp();
            let mut header = Header::new(Algorithm::EdDSA);
            header.kid = Some(self.kid.clone());
            encode(
                &header,
                &TestClaims {
                    sub: "rider-123",
                    iss: "projecty.identity",
                    aud: audience,
                    exp: now + lifetime,
                    nbf: now.saturating_sub(1),
                    iat: now,
                    jti: "test-token-id",
                    role: roles,
                },
                &EncodingKey::from_ed_der(&self.private_key),
            )
            .unwrap()
        }
    }

    struct SecurityUpstreamState {
        jwks: RwLock<Option<String>>,
        jwks_requests: AtomicUsize,
        upstream_requests: AtomicUsize,
    }

    async fn spawn_security_upstream(
        initial_jwks: Option<String>,
    ) -> (Url, Arc<SecurityUpstreamState>) {
        async fn serve_jwks(AxumState(state): AxumState<Arc<SecurityUpstreamState>>) -> Response {
            state.jwks_requests.fetch_add(1, Ordering::SeqCst);
            match state.jwks.read().await.clone() {
                Some(document) => (
                    StatusCode::OK,
                    [(http::header::CONTENT_TYPE, "application/json")],
                    document,
                )
                    .into_response(),
                None => StatusCode::SERVICE_UNAVAILABLE.into_response(),
            }
        }

        async fn capture(
            AxumState(state): AxumState<Arc<SecurityUpstreamState>>,
            request: Request,
        ) -> Response {
            state.upstream_requests.fetch_add(1, Ordering::SeqCst);
            let value = |name: &'static str| {
                request
                    .headers()
                    .get(name)
                    .and_then(|header| header.to_str().ok())
                    .map(str::to_owned)
            };
            Json(json!({
                "authorization": value("authorization"),
                "cookie": value("cookie"),
                "subject": value("x-identity-subject"),
                "roles": value("x-identity-roles"),
                "issued_at": value("x-identity-issued-at"),
                "key_id": value("x-identity-key-id"),
                "signature": value("x-identity-signature"),
            }))
            .into_response()
        }

        let state = Arc::new(SecurityUpstreamState {
            jwks: RwLock::new(initial_jwks),
            jwks_requests: AtomicUsize::new(0),
            upstream_requests: AtomicUsize::new(0),
        });
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let app = Router::new()
            .route("/.well-known/jwks.json", get(serve_jwks))
            .fallback(capture)
            .with_state(state.clone());
        tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        (Url::parse(&format!("http://{address}/")).unwrap(), state)
    }

    async fn response_json(response: Response) -> Value {
        let body = to_bytes(response.into_body(), 4096).await.unwrap();
        serde_json::from_slice(&body).unwrap()
    }

    struct StubRevocationStore {
        result: Result<bool, RevocationError>,
        checks: AtomicUsize,
    }

    impl RevocationStore for StubRevocationStore {
        fn is_revoked<'a>(
            &'a self,
            _token_id: &'a str,
        ) -> std::pin::Pin<Box<dyn Future<Output = Result<bool, RevocationError>> + Send + 'a>>
        {
            Box::pin(async move {
                self.checks.fetch_add(1, Ordering::SeqCst);
                self.result
            })
        }
    }

    fn app_with_revocation(
        config: Config,
        result: Result<bool, RevocationError>,
    ) -> (Router, Arc<StubRevocationStore>) {
        let store = Arc::new(StubRevocationStore {
            result,
            checks: AtomicUsize::new(0),
        });
        let app = build_app_with_revocation(config, store.clone()).unwrap();
        (app, store)
    }

    struct StubRateLimiter {
        result: Result<RateLimitDecision, RateLimitError>,
        checks: AtomicUsize,
    }

    impl RateLimiter for StubRateLimiter {
        fn check<'a>(
            &'a self,
            _key: &'a str,
            _bucket: TokenBucketConfig,
        ) -> std::pin::Pin<
            Box<dyn Future<Output = Result<RateLimitDecision, RateLimitError>> + Send + 'a>,
        > {
            Box::pin(async move {
                self.checks.fetch_add(1, Ordering::SeqCst);
                self.result
            })
        }
    }

    fn app_with_rate_limit(
        config: Config,
        result: Result<RateLimitDecision, RateLimitError>,
    ) -> (Router, Arc<StubRateLimiter>) {
        let rate_limiter = Arc::new(StubRateLimiter {
            result,
            checks: AtomicUsize::new(0),
        });
        let revocation = Arc::new(StubRevocationStore {
            result: Ok(false),
            checks: AtomicUsize::new(0),
        });
        let app = build_app_with_dependencies(config, revocation, rate_limiter.clone()).unwrap();
        (app, rate_limiter)
    }

    #[test]
    fn keys_authenticated_requests_by_identity_and_anonymous_requests_by_origin() {
        let mut first = HttpRequest::get("/api/rental/user")
            .body(Body::empty())
            .unwrap();
        first
            .extensions_mut()
            .insert(ConnectInfo(SocketAddr::from(([192, 0, 2, 10], 1000))));
        let mut second = HttpRequest::get("/api/rental/user")
            .body(Body::empty())
            .unwrap();
        second
            .extensions_mut()
            .insert(ConnectInfo(SocketAddr::from(([192, 0, 2, 11], 1000))));

        assert_eq!(
            rate_limit_principal(&first, Some("rider-1".to_owned())),
            rate_limit_principal(&second, Some("rider-1".to_owned()))
        );
        assert_ne!(
            rate_limit_principal(&first, None),
            rate_limit_principal(&second, None)
        );
    }

    #[test]
    fn auth_routes_use_the_stricter_bucket() {
        let config = test_config(Url::parse("http://127.0.0.1:1/").unwrap());

        assert_eq!(
            rate_limit_bucket(&config, UpstreamName::AuthGate),
            ("auth", config.rate_limit.auth)
        );
        assert_eq!(
            rate_limit_bucket(&config, UpstreamName::RentalOperations),
            ("general", config.rate_limit.general)
        );
        assert!(config.rate_limit.auth.capacity < config.rate_limit.general.capacity);
        assert!(
            config.rate_limit.auth.refill_per_minute < config.rate_limit.general.refill_per_minute
        );
    }

    #[tokio::test]
    async fn returns_429_with_retry_and_remaining_headers() {
        let (upstream, state) = spawn_security_upstream(None).await;
        let (app, rate_limiter) = app_with_rate_limit(
            test_config(upstream),
            Ok(RateLimitDecision {
                allowed: false,
                remaining: 0,
                retry_after_seconds: 12,
            }),
        );

        let response = app
            .oneshot(
                HttpRequest::post("/api/auth/login")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::TOO_MANY_REQUESTS);
        assert_eq!(response.headers()[http::header::RETRY_AFTER], "12");
        assert_eq!(response.headers()["x-ratelimit-remaining"], "0");
        assert_eq!(
            response.headers()[http::header::CONTENT_TYPE],
            "application/problem+json"
        );
        assert_eq!(rate_limiter.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn forwards_remaining_tokens_after_an_allowed_request() {
        let (upstream, state) = spawn_security_upstream(None).await;
        let (app, rate_limiter) = app_with_rate_limit(
            test_config(upstream),
            Ok(RateLimitDecision {
                allowed: true,
                remaining: 7,
                retry_after_seconds: 1,
            }),
        );

        let response = app
            .oneshot(
                HttpRequest::post("/api/auth/login")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(response.headers()["x-ratelimit-remaining"], "7");
        assert_eq!(rate_limiter.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn fails_open_and_exposes_the_degradation_counter() {
        let (upstream, state) = spawn_security_upstream(None).await;
        let (app, rate_limiter) =
            app_with_rate_limit(test_config(upstream), Err(RateLimitError::Unavailable));

        let response = app
            .clone()
            .oneshot(
                HttpRequest::post("/api/auth/login")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        let metrics_response = app
            .oneshot(HttpRequest::get("/metrics").body(Body::empty()).unwrap())
            .await
            .unwrap();
        let metrics_body = to_bytes(metrics_response.into_body(), 4096).await.unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        assert!(response.headers().get("x-ratelimit-remaining").is_none());
        assert_eq!(rate_limiter.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 1);
        assert!(
            std::str::from_utf8(&metrics_body)
                .unwrap()
                .contains("gateway_ratelimit_degraded_total 1")
        );
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
    async fn rejects_forged_identity_headers_before_the_upstream() {
        let (upstream, state) = spawn_security_upstream(None).await;
        let app = build_app(test_config(upstream)).unwrap();
        let response = app
            .oneshot(
                HttpRequest::post("/api/auth/login")
                    .header("x-identity-subject", "admin")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::BAD_REQUEST);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
        assert_eq!(
            response.headers()[http::header::CONTENT_TYPE],
            "application/problem+json"
        );
    }

    #[tokio::test]
    async fn rejects_encoded_and_trailing_paths_before_policy_classification() {
        let (upstream, state) = spawn_security_upstream(None).await;
        let app = build_app(test_config(upstream)).unwrap();

        for path in ["/api/rental/us%65r/victim", "/api/rental/user/victim/"] {
            let response = app
                .clone()
                .oneshot(HttpRequest::get(path).body(Body::empty()).unwrap())
                .await
                .unwrap();
            assert_eq!(response.status(), StatusCode::BAD_REQUEST, "{path}");
        }
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn verifies_eddsa_and_forwards_only_gateway_signed_identity() {
        let issuer = TestIssuer::new("active-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost?plan=weekly")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .header(http::header::COOKIE, "session=must-not-cross")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        let body = response_json(response).await;
        assert!(body["authorization"].is_null());
        assert!(body["cookie"].is_null());
        assert_eq!(body["subject"], "rider-123");
        assert_eq!(body["roles"], "Rider");
        assert_eq!(body["key_id"], "local-v1");
        let issued_at = body["issued_at"].as_str().unwrap();
        let signature = body["signature"]
            .as_str()
            .unwrap()
            .strip_prefix("v1=")
            .unwrap();
        let canonical = format!(
            "v1\nlocal-v1\nrider-123\nRider\n{issued_at}\nPOST\n/api/rental/calculate-final-cost?plan=weekly\nprojecty.rental-operations"
        );
        let mut mac = Hmac::<Sha256>::new_from_slice(&[b'x'; 32]).unwrap();
        mac.update(canonical.as_bytes());
        mac.verify_slice(&URL_SAFE_NO_PAD.decode(signature).unwrap())
            .unwrap();
        assert_eq!(state.jwks_requests.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn refuses_a_rider_token_on_an_admin_route() {
        let issuer = TestIssuer::new("admin-policy-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();
        let token = issuer.token("projecty.rider-manager", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::get("/api/riders/rider-123")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::FORBIDDEN);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn allows_a_non_revoked_token_on_rental_creation() {
        let issuer = TestIssuer::new("active-rental-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let (app, revocation) = app_with_revocation(config, Ok(false));
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/create")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(revocation.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 1);
    }

    #[tokio::test]
    async fn refuses_a_revoked_token_on_rental_creation() {
        let issuer = TestIssuer::new("revoked-rental-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let (app, revocation) = app_with_revocation(config, Ok(true));
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/create")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(revocation.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn fails_closed_when_revocation_cannot_be_checked() {
        let issuer = TestIssuer::new("redis-failure-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let (app, revocation) = app_with_revocation(config, Err(RevocationError::Unavailable));
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/create")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(response.headers()[http::header::RETRY_AFTER], "1");
        assert_eq!(revocation.checks.load(Ordering::SeqCst), 1);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn enforces_the_route_specific_audience() {
        let issuer = TestIssuer::new("audience-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();
        let token = issuer.token("projecty.rider-manager", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
        assert_eq!(
            response.headers()[WWW_AUTHENTICATE],
            "Bearer error=\"invalid_token\""
        );
    }

    #[tokio::test]
    async fn rejects_access_tokens_longer_than_the_declared_five_minutes() {
        let issuer = TestIssuer::new("long-lived-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();
        let token = issuer.token_with_lifetime("projecty.rental-operations", &["Rider"], 301);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn refreshes_once_for_an_unknown_kid_and_retires_the_old_key() {
        let first = TestIssuer::new("first-key");
        let second = TestIssuer::new("second-key");
        let (upstream, state) = spawn_security_upstream(Some(first.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();

        let first_token = first.token("projecty.rental-operations", &["Rider"]);
        let first_response = app
            .clone()
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {first_token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(first_response.status(), StatusCode::OK);

        *state.jwks.write().await = Some(second.jwks());
        let second_token = second.token("projecty.rental-operations", &["Rider"]);
        let second_response = app
            .clone()
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {second_token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(second_response.status(), StatusCode::OK);
        assert_eq!(state.jwks_requests.load(Ordering::SeqCst), 2);

        let retired_response = app
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {first_token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(retired_response.status(), StatusCode::UNAUTHORIZED);
        assert_eq!(state.jwks_requests.load(Ordering::SeqCst), 2);
    }

    #[tokio::test]
    async fn fails_closed_when_jwks_cannot_be_resolved() {
        let issuer = TestIssuer::new("unreachable-key");
        let (upstream, state) = spawn_security_upstream(None).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        let app = build_app(config).unwrap();
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let response = app
            .oneshot(
                HttpRequest::post("/api/rental/calculate-final-cost")
                    .header(AUTHORIZATION, format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 0);
    }

    #[tokio::test]
    async fn refuses_stale_cached_keys_when_jwks_is_unavailable() {
        let issuer = TestIssuer::new("expiring-cache-key");
        let (upstream, state) = spawn_security_upstream(Some(issuer.jwks())).await;
        let mut config = test_config(upstream.clone());
        config.auth.jwks_url = upstream.join(".well-known/jwks.json").unwrap();
        config.auth.jwks_cache_ttl = Duration::from_millis(1);
        let app = build_app(config).unwrap();
        let token = issuer.token("projecty.rental-operations", &["Rider"]);
        let request = || {
            HttpRequest::post("/api/rental/calculate-final-cost")
                .header(AUTHORIZATION, format!("Bearer {token}"))
                .body(Body::empty())
                .unwrap()
        };

        let warm_response = app.clone().oneshot(request()).await.unwrap();
        assert_eq!(warm_response.status(), StatusCode::OK);
        *state.jwks.write().await = None;
        tokio::time::sleep(Duration::from_millis(20)).await;

        let stale_response = app.oneshot(request()).await.unwrap();
        assert_eq!(stale_response.status(), StatusCode::SERVICE_UNAVAILABLE);
        assert_eq!(state.upstream_requests.load(Ordering::SeqCst), 1);
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
            .route("/api/auth/login", axum::routing::post(slow))
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
            reqwest::Client::new()
                .post(format!("http://{gateway_address}/api/auth/login"))
                .send()
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
