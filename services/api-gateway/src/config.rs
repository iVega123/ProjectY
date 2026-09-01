use std::{env, net::SocketAddr, time::Duration};

use url::Url;

const DEFAULT_BIND: &str = "0.0.0.0:8090";
const DEFAULT_HEALTH_URL: &str = "http://127.0.0.1:8090/health/ready";

#[derive(Clone, Debug)]
pub struct Config {
    pub bind: SocketAddr,
    pub health_url: Url,
    pub healthcheck_timeout: Duration,
    pub upstreams: Upstreams,
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

fn env_value(name: &str, default: &str) -> String {
    env::var(name).unwrap_or_else(|_| default.to_owned())
}

fn required_url(name: &str) -> Result<Url, String> {
    let value = env::var(name).map_err(|_| format!("{name} is required"))?;
    let url = parse_absolute_url(name, &value)?;
    Ok(normalize_base_url(url))
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
    }
}
