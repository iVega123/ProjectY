use std::{env, fmt, net::SocketAddr, sync::Arc, time::Duration};

use url::Url;

const DEFAULT_BIND: &str = "0.0.0.0:8090";
const DEFAULT_HEALTH_URL: &str = "http://127.0.0.1:8090/health/ready";

#[derive(Clone, Debug)]
pub struct Config {
    pub bind: SocketAddr,
    pub health_url: Url,
    pub healthcheck_timeout: Duration,
    pub upstreams: Upstreams,
    pub auth: AuthConfig,
    pub rate_limit: RateLimitConfig,
    pub resilience: ResilienceConfig,
}

#[derive(Clone, Debug)]
pub struct ResilienceConfig {
    pub auth_gate: UpstreamResilienceConfig,
    pub rider_manager: UpstreamResilienceConfig,
    pub moto_hub: UpstreamResilienceConfig,
    pub rental_operations: UpstreamResilienceConfig,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct UpstreamResilienceConfig {
    pub timeout: Duration,
    pub max_concurrency: usize,
    pub breaker_failure_threshold: u32,
    pub breaker_open_duration: Duration,
    pub max_retries: u32,
    pub retry_base_delay: Duration,
    pub retry_max_delay: Duration,
}

#[derive(Clone, Debug)]
pub struct RateLimitConfig {
    pub redis_url: SensitiveString,
    pub operation_timeout: Duration,
    pub general: TokenBucketConfig,
    pub auth: TokenBucketConfig,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TokenBucketConfig {
    pub capacity: u64,
    pub refill_per_minute: u64,
}

#[derive(Clone, Debug)]
pub struct AuthConfig {
    pub jwks_url: Url,
    pub issuer: String,
    pub audiences: Audiences,
    pub jwks_cache_ttl: Duration,
    pub unknown_kid_refresh_interval: Duration,
    pub jwks_timeout: Duration,
    pub clock_skew: Duration,
    pub max_token_lifetime: Duration,
    pub identity_signing_key: Secret,
    pub identity_signing_key_id: String,
    pub redis_url: SensitiveString,
    pub redis_timeout: Duration,
}

#[derive(Clone, Debug)]
pub struct Audiences {
    pub auth_gate: String,
    pub rider_manager: String,
    pub moto_hub: String,
    pub rental_operations: String,
}

#[derive(Clone)]
pub struct Secret(Arc<[u8]>);

#[derive(Clone)]
pub struct SensitiveString(Arc<str>);

impl Secret {
    pub(crate) fn new(value: Vec<u8>) -> Result<Self, String> {
        if value.len() < 32 {
            return Err("identity signing key must contain at least 32 bytes".to_owned());
        }
        Ok(Self(Arc::from(value)))
    }

    pub fn expose(&self) -> &[u8] {
        &self.0
    }
}

impl fmt::Debug for Secret {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("<redacted>")
    }
}

impl SensitiveString {
    pub(crate) fn new(value: String) -> Self {
        Self(Arc::from(value))
    }

    pub fn expose(&self) -> &str {
        &self.0
    }
}

impl fmt::Debug for SensitiveString {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("<redacted>")
    }
}

#[derive(Clone, Debug)]
pub struct Upstreams {
    pub auth_gate: Url,
    pub rider_manager: Url,
    pub moto_hub: Url,
    pub rental_operations: Url,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UpstreamName {
    AuthGate,
    RiderManager,
    MotoHub,
    RentalOperations,
}

impl Config {
    pub fn from_env() -> Result<Self, String> {
        let bind = env_value("GATEWAY_BIND", DEFAULT_BIND)
            .parse()
            .map_err(|error| format!("GATEWAY_BIND must be a socket address: {error}"))?;

        Ok(Self {
            bind,
            health_url: parse_absolute_url(
                "GATEWAY_HEALTH_URL",
                &env_value("GATEWAY_HEALTH_URL", DEFAULT_HEALTH_URL),
            )?,
            healthcheck_timeout: Duration::from_millis(parse_timeout_ms()?),
            upstreams: Upstreams {
                auth_gate: required_url("GATEWAY_UPSTREAM_AUTH_GATE")?,
                rider_manager: required_url("GATEWAY_UPSTREAM_RIDER_MANAGER")?,
                moto_hub: required_url("GATEWAY_UPSTREAM_MOTO_HUB")?,
                rental_operations: required_url("GATEWAY_UPSTREAM_RENTAL_OPERATIONS")?,
            },
            auth: AuthConfig {
                jwks_url: required_absolute_url("GATEWAY_JWKS_URL")?,
                issuer: required_value("GATEWAY_JWT_ISSUER")?,
                audiences: Audiences {
                    auth_gate: required_value("GATEWAY_JWT_AUDIENCE_AUTH_GATE")?,
                    rider_manager: required_value("GATEWAY_JWT_AUDIENCE_RIDER_MANAGER")?,
                    moto_hub: required_value("GATEWAY_JWT_AUDIENCE_MOTO_HUB")?,
                    rental_operations: required_value("GATEWAY_JWT_AUDIENCE_RENTAL_OPERATIONS")?,
                },
                jwks_cache_ttl: duration_from_env("GATEWAY_JWKS_CACHE_TTL_SECS", 300)?,
                unknown_kid_refresh_interval: duration_from_env(
                    "GATEWAY_JWKS_UNKNOWN_KID_REFRESH_SECS",
                    5,
                )?,
                jwks_timeout: Duration::from_millis(positive_u64_from_env(
                    "GATEWAY_JWKS_TIMEOUT_MS",
                    2000,
                )?),
                clock_skew: duration_from_env("GATEWAY_JWT_CLOCK_SKEW_SECS", 30)?,
                max_token_lifetime: duration_from_env("GATEWAY_JWT_MAX_LIFETIME_SECS", 300)?,
                identity_signing_key: signing_secret()?,
                identity_signing_key_id: identity_key_id()?,
                redis_url: SensitiveString::new(required_value("GATEWAY_REDIS_URL")?),
                redis_timeout: Duration::from_millis(positive_u64_from_env(
                    "GATEWAY_REDIS_TIMEOUT_MS",
                    250,
                )?),
            },
            rate_limit: RateLimitConfig {
                redis_url: SensitiveString::new(required_value("GATEWAY_REDIS_URL")?),
                operation_timeout: Duration::from_millis(positive_u64_from_env(
                    "GATEWAY_RATE_LIMIT_REDIS_TIMEOUT_MS",
                    100,
                )?),
                general: token_bucket_from_env("GENERAL", 120, 120)?,
                auth: token_bucket_from_env("AUTH", 10, 5)?,
            },
            resilience: ResilienceConfig {
                auth_gate: upstream_resilience_from_env("AUTH_GATE", 1500)?,
                rider_manager: upstream_resilience_from_env("RIDER_MANAGER", 2000)?,
                moto_hub: upstream_resilience_from_env("MOTO_HUB", 2000)?,
                rental_operations: upstream_resilience_from_env("RENTAL_OPERATIONS", 2500)?,
            },
        })
    }

    pub fn healthcheck_from_env() -> Result<(Url, Duration), String> {
        let url = parse_absolute_url(
            "GATEWAY_HEALTH_URL",
            &env_value("GATEWAY_HEALTH_URL", DEFAULT_HEALTH_URL),
        )?;
        Ok((url, Duration::from_millis(parse_timeout_ms()?)))
    }
}

impl Upstreams {
    pub fn resolve(&self, path: &str) -> Option<(UpstreamName, &Url)> {
        let path = path.to_ascii_lowercase();
        if path == "/api/auth" || path.starts_with("/api/auth/") {
            Some((UpstreamName::AuthGate, &self.auth_gate))
        } else if path == "/api/riders"
            || path.starts_with("/api/riders/")
            || path == "/update-image"
        {
            Some((UpstreamName::RiderManager, &self.rider_manager))
        } else if path == "/api/motorcycles" || path.starts_with("/api/motorcycles/") {
            Some((UpstreamName::MotoHub, &self.moto_hub))
        } else if path == "/api/rental" || path.starts_with("/api/rental/") {
            Some((UpstreamName::RentalOperations, &self.rental_operations))
        } else {
            None
        }
    }
}

impl Audiences {
    pub fn for_upstream(&self, upstream: UpstreamName) -> &str {
        match upstream {
            UpstreamName::AuthGate => &self.auth_gate,
            UpstreamName::RiderManager => &self.rider_manager,
            UpstreamName::MotoHub => &self.moto_hub,
            UpstreamName::RentalOperations => &self.rental_operations,
        }
    }
}

impl ResilienceConfig {
    pub fn for_upstream(&self, upstream: UpstreamName) -> UpstreamResilienceConfig {
        match upstream {
            UpstreamName::AuthGate => self.auth_gate,
            UpstreamName::RiderManager => self.rider_manager,
            UpstreamName::MotoHub => self.moto_hub,
            UpstreamName::RentalOperations => self.rental_operations,
        }
    }
}

impl UpstreamName {
    pub const ALL: [Self; 4] = [
        Self::AuthGate,
        Self::RiderManager,
        Self::MotoHub,
        Self::RentalOperations,
    ];

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::AuthGate => "auth_gate",
            Self::RiderManager => "rider_manager",
            Self::MotoHub => "moto_hub",
            Self::RentalOperations => "rental_operations",
        }
    }
}

fn env_value(name: &str, default: &str) -> String {
    env::var(name).unwrap_or_else(|_| default.to_owned())
}

fn required_url(name: &str) -> Result<Url, String> {
    let value = required_value(name)?;
    let url = parse_absolute_url(name, &value)?;
    Ok(normalize_base_url(url))
}

fn required_absolute_url(name: &str) -> Result<Url, String> {
    let value = required_value(name)?;
    parse_absolute_url(name, &value)
}

fn required_value(name: &str) -> Result<String, String> {
    let value = env::var(name).map_err(|_| format!("{name} is required"))?;
    if value.trim().is_empty() {
        Err(format!("{name} must not be empty"))
    } else {
        Ok(value)
    }
}

fn signing_secret() -> Result<Secret, String> {
    let value = required_value("GATEWAY_IDENTITY_SIGNING_KEY")?;
    Secret::new(value.into_bytes())
        .map_err(|_| "GATEWAY_IDENTITY_SIGNING_KEY must contain at least 32 bytes".to_owned())
}

fn identity_key_id() -> Result<String, String> {
    let value = required_value("GATEWAY_IDENTITY_SIGNING_KEY_ID")?;
    if value.len() > 128 || !value.bytes().all(|byte| matches!(byte, 0x21..=0x7e)) {
        return Err(
            "GATEWAY_IDENTITY_SIGNING_KEY_ID must be at most 128 visible ASCII characters"
                .to_owned(),
        );
    }
    Ok(value)
}

fn duration_from_env(name: &str, default: u64) -> Result<Duration, String> {
    Ok(Duration::from_secs(positive_u64_from_env(name, default)?))
}

fn positive_u64_from_env(name: &str, default: u64) -> Result<u64, String> {
    env_value(name, &default.to_string())
        .parse::<u64>()
        .map_err(|error| format!("{name} must be an integer: {error}"))
        .and_then(|value| {
            if value == 0 {
                Err(format!("{name} must be greater than zero"))
            } else {
                Ok(value)
            }
        })
}

fn token_bucket_from_env(
    name: &str,
    default_capacity: u64,
    default_refill_per_minute: u64,
) -> Result<TokenBucketConfig, String> {
    let capacity = positive_u64_from_env(
        &format!("GATEWAY_RATE_LIMIT_{name}_CAPACITY"),
        default_capacity,
    )?;
    let refill_per_minute = positive_u64_from_env(
        &format!("GATEWAY_RATE_LIMIT_{name}_REFILL_PER_MINUTE"),
        default_refill_per_minute,
    )?;
    if capacity > 1_000_000 || refill_per_minute > 1_000_000 {
        return Err(format!(
            "GATEWAY_RATE_LIMIT_{name}_* values must not exceed 1000000"
        ));
    }
    Ok(TokenBucketConfig {
        capacity,
        refill_per_minute,
    })
}

fn upstream_resilience_from_env(
    name: &str,
    default_timeout_ms: u64,
) -> Result<UpstreamResilienceConfig, String> {
    let prefix = format!("GATEWAY_UPSTREAM_{name}");
    let timeout = Duration::from_millis(positive_u64_from_env(
        &format!("{prefix}_TIMEOUT_MS"),
        default_timeout_ms,
    )?);
    let max_concurrency = positive_u64_from_env(&format!("{prefix}_MAX_CONCURRENCY"), 64)?;
    let breaker_failure_threshold =
        positive_u64_from_env(&format!("{prefix}_BREAKER_FAILURE_THRESHOLD"), 5)?;
    let breaker_open_duration = Duration::from_millis(positive_u64_from_env(
        &format!("{prefix}_BREAKER_OPEN_MS"),
        30_000,
    )?);
    let max_retries = non_negative_u64_from_env(&format!("{prefix}_MAX_RETRIES"), 2)?;
    let retry_base_delay = Duration::from_millis(positive_u64_from_env(
        &format!("{prefix}_RETRY_BASE_MS"),
        25,
    )?);
    let retry_max_delay = Duration::from_millis(positive_u64_from_env(
        &format!("{prefix}_RETRY_MAX_MS"),
        250,
    )?);
    if retry_base_delay > retry_max_delay {
        return Err(format!(
            "{prefix}_RETRY_BASE_MS must not exceed {prefix}_RETRY_MAX_MS"
        ));
    }
    if max_concurrency > 100_000 {
        return Err(format!("{prefix}_MAX_CONCURRENCY must not exceed 100000"));
    }
    if breaker_failure_threshold > 1_000_000 {
        return Err(format!(
            "{prefix}_BREAKER_FAILURE_THRESHOLD must not exceed 1000000"
        ));
    }
    if max_retries > 10 {
        return Err(format!("{prefix}_MAX_RETRIES must not exceed 10"));
    }
    Ok(UpstreamResilienceConfig {
        timeout,
        max_concurrency: usize::try_from(max_concurrency)
            .map_err(|_| format!("{prefix}_MAX_CONCURRENCY is too large"))?,
        breaker_failure_threshold: u32::try_from(breaker_failure_threshold)
            .map_err(|_| format!("{prefix}_BREAKER_FAILURE_THRESHOLD is too large"))?,
        breaker_open_duration,
        max_retries: u32::try_from(max_retries)
            .map_err(|_| format!("{prefix}_MAX_RETRIES is too large"))?,
        retry_base_delay,
        retry_max_delay,
    })
}

fn non_negative_u64_from_env(name: &str, default: u64) -> Result<u64, String> {
    env::var(name)
        .map(|value| {
            value
                .parse::<u64>()
                .map_err(|error| format!("{name} must be a non-negative integer: {error}"))
        })
        .unwrap_or(Ok(default))
}

fn normalize_base_url(mut url: Url) -> Url {
    if !url.path().ends_with('/') {
        let path = format!("{}/", url.path());
        url.set_path(&path);
    }
    url
}

fn parse_absolute_url(name: &str, value: &str) -> Result<Url, String> {
    let url = Url::parse(value).map_err(|error| format!("{name} must be a URL: {error}"))?;
    if !matches!(url.scheme(), "http" | "https") || url.host_str().is_none() {
        return Err(format!("{name} must be an absolute http(s) URL"));
    }
    if url.query().is_some() || url.fragment().is_some() {
        return Err(format!("{name} must not contain a query or fragment"));
    }
    Ok(url)
}

fn parse_timeout_ms() -> Result<u64, String> {
    env_value("GATEWAY_HEALTHCHECK_TIMEOUT_MS", "2000")
        .parse::<u64>()
        .map_err(|error| format!("GATEWAY_HEALTHCHECK_TIMEOUT_MS must be an integer: {error}"))
        .and_then(|value| {
            if value == 0 {
                Err("GATEWAY_HEALTHCHECK_TIMEOUT_MS must be greater than zero".to_owned())
            } else {
                Ok(value)
            }
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn upstreams() -> Upstreams {
        Upstreams {
            auth_gate: Url::parse("http://auth-gate:8080/").unwrap(),
            rider_manager: Url::parse("http://rider-manager:8000/").unwrap(),
            moto_hub: Url::parse("http://moto-hub:8100/").unwrap(),
            rental_operations: Url::parse("http://rental-operations:8200/").unwrap(),
        }
    }

    #[test]
    fn resolves_only_owned_route_prefixes() {
        let upstreams = upstreams();

        assert_eq!(
            upstreams.resolve("/api/auth/login").map(|route| route.0),
            Some(UpstreamName::AuthGate)
        );
        assert_eq!(
            upstreams.resolve("/api/Riders/123").map(|route| route.0),
            Some(UpstreamName::RiderManager)
        );
        assert_eq!(
            upstreams.resolve("/api/motorcycles").map(|route| route.0),
            Some(UpstreamName::MotoHub)
        );
        assert_eq!(
            upstreams.resolve("/api/rental/user").map(|route| route.0),
            Some(UpstreamName::RentalOperations)
        );
        assert!(upstreams.resolve("/api/authentic-looking").is_none());
        assert!(upstreams.resolve("/api/rider/123").is_none());
        assert!(upstreams.resolve("/api/motorcycle/ABC1234").is_none());
        assert!(upstreams.resolve("/health/ready").is_none());
    }

    #[test]
    fn keeps_the_healthcheck_path_exact_and_normalizes_upstream_bases() {
        let health =
            parse_absolute_url("GATEWAY_HEALTH_URL", "http://127.0.0.1:8090/health/ready").unwrap();
        assert_eq!(health.path(), "/health/ready");

        let upstream =
            normalize_base_url(parse_absolute_url("UPSTREAM", "http://service:8080/base").unwrap());
        assert_eq!(upstream.path(), "/base/");

        let jwks = parse_absolute_url(
            "GATEWAY_JWKS_URL",
            "http://identity:8080/.well-known/jwks.json",
        )
        .unwrap();
        assert_eq!(jwks.path(), "/.well-known/jwks.json");
    }

    #[test]
    fn redacts_the_internal_signing_key_from_debug_output() {
        let secret = Secret::new(b"test-only-key-with-at-least-32-bytes".to_vec()).unwrap();
        assert_eq!(format!("{secret:?}"), "<redacted>");
        let redis_url = SensitiveString::new("redis://:password@redis:6379/".to_owned());
        assert_eq!(format!("{redis_url:?}"), "<redacted>");
    }
}
