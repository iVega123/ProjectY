use std::{
    collections::{HashMap, HashSet},
    sync::Arc,
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use axum::http::{HeaderMap, HeaderValue, Method, Uri, header::AUTHORIZATION};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use futures_util::StreamExt;
use hmac::{Hmac, Mac};
use jsonwebtoken::{
    Algorithm, DecodingKey, Validation, decode, decode_header,
    jwk::{AlgorithmParameters, EllipticCurve, JwkSet, KeyAlgorithm, KeyOperations, PublicKeyUse},
};
use serde::Deserialize;
use sha2::Sha256;
use tokio::sync::{Mutex, RwLock};

use crate::config::AuthConfig;

const MAX_JWKS_BYTES: usize = 256 * 1024;
#[cfg(test)]
const DOTNET_ROLE_CLAIM: &str = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Identity {
    pub subject: String,
    pub roles: Vec<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthError {
    MissingToken,
    InvalidToken,
    JwksUnavailable,
}

pub struct Authenticator {
    config: AuthConfig,
    client: reqwest::Client,
    cache: RwLock<KeyCache>,
    refresh: Mutex<()>,
}

#[derive(Default)]
struct KeyCache {
    keys: HashMap<String, DecodingKey>,
    fetched_at: Option<Instant>,
    last_unknown_kid_refresh: Option<Instant>,
}

#[derive(Debug, Deserialize)]
struct Claims {
    sub: String,
    exp: u64,
    iat: u64,
    #[serde(default)]
    role: RoleClaim,
    #[serde(default)]
    roles: RoleClaim,
    #[serde(
        rename = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
        default
    )]
    dotnet_role: RoleClaim,
}

#[derive(Debug, Default, Deserialize)]
#[serde(untagged)]
enum RoleClaim {
    One(String),
    Many(Vec<String>),
    #[default]
    Missing,
}

impl RoleClaim {
    fn append_to(self, roles: &mut Vec<String>) {
        match self {
            Self::One(role) => roles.push(role),
            Self::Many(values) => roles.extend(values),
            Self::Missing => {}
        }
    }
}

impl Authenticator {
    pub fn new(config: AuthConfig, client: reqwest::Client) -> Self {
        Self {
            config,
            client,
            cache: RwLock::new(KeyCache::default()),
            refresh: Mutex::new(()),
        }
    }

    pub async fn authenticate(
        &self,
        headers: &HeaderMap,
        expected_audience: &str,
    ) -> Result<Identity, AuthError> {
        let token = bearer_token(headers)?;
        let header = decode_header(token).map_err(|_| AuthError::InvalidToken)?;
        if header.alg != Algorithm::EdDSA {
            return Err(AuthError::InvalidToken);
        }
        let kid = header.kid.as_deref().ok_or(AuthError::InvalidToken)?;
        let key = self.key_for(kid).await?;

        let mut validation = Validation::new(Algorithm::EdDSA);
        validation.set_issuer(&[&self.config.issuer]);
        validation.set_audience(&[expected_audience]);
        validation.set_required_spec_claims(&["sub", "iss", "aud", "exp", "iat"]);
        validation.validate_nbf = true;
        validation.leeway = self.config.clock_skew.as_secs();

        let claims = decode::<Claims>(token, &key, &validation)
            .map_err(|_| AuthError::InvalidToken)?
            .claims;
        let now = jsonwebtoken::get_current_timestamp();
        if claims.iat > now.saturating_add(self.config.clock_skew.as_secs())
            || claims.exp <= claims.iat
            || claims.exp - claims.iat > self.config.max_token_lifetime.as_secs()
        {
            return Err(AuthError::InvalidToken);
        }
        if !is_safe_header_component(&claims.sub) {
            return Err(AuthError::InvalidToken);
        }

        let mut roles = Vec::new();
        claims.role.append_to(&mut roles);
        claims.roles.append_to(&mut roles);
        claims.dotnet_role.append_to(&mut roles);
        if roles.len() > 32
            || roles.iter().any(|role| !is_safe_header_component(role))
            || roles.iter().map(String::len).sum::<usize>() + roles.len().saturating_sub(1) > 1024
        {
            return Err(AuthError::InvalidToken);
        }
        roles.sort_unstable();
        roles.dedup();

        Ok(Identity {
            subject: claims.sub,
            roles,
        })
    }

    async fn key_for(&self, kid: &str) -> Result<DecodingKey, AuthError> {
        if let Some(key) = self.fresh_key(kid).await {
            return Ok(key);
        }

        let _refresh_guard = self.refresh.lock().await;
        if let Some(key) = self.fresh_key(kid).await {
            return Ok(key);
        }

        let now = Instant::now();
        {
            let mut cache = self.cache.write().await;
            let cache_is_fresh = cache
                .fetched_at
                .is_some_and(|fetched| now.duration_since(fetched) < self.config.jwks_cache_ttl);
            if cache_is_fresh {
                let refresh_is_rate_limited = cache.last_unknown_kid_refresh.is_some_and(|last| {
                    now.duration_since(last) < self.config.unknown_kid_refresh_interval
                });
                if refresh_is_rate_limited {
                    return Err(AuthError::InvalidToken);
                }
                cache.last_unknown_kid_refresh = Some(now);
            }
        }

        let keys = self.fetch_keys().await?;
        let key = keys.get(kid).cloned();
        let mut cache = self.cache.write().await;
        cache.keys = keys;
        cache.fetched_at = Some(Instant::now());
        if key.is_none() {
            cache.last_unknown_kid_refresh = Some(Instant::now());
        }
        key.ok_or(AuthError::InvalidToken)
    }

    async fn fresh_key(&self, kid: &str) -> Option<DecodingKey> {
        let cache = self.cache.read().await;
        let is_fresh = cache.fetched_at.is_some_and(|fetched| {
            Instant::now().duration_since(fetched) < self.config.jwks_cache_ttl
        });
        is_fresh.then(|| cache.keys.get(kid).cloned()).flatten()
    }

    async fn fetch_keys(&self) -> Result<HashMap<String, DecodingKey>, AuthError> {
        let response = self
            .client
            .get(self.config.jwks_url.clone())
            .timeout(self.config.jwks_timeout)
            .send()
            .await
            .map_err(|_| AuthError::JwksUnavailable)?
            .error_for_status()
            .map_err(|_| AuthError::JwksUnavailable)?;
        if response
            .content_length()
            .is_some_and(|length| length > MAX_JWKS_BYTES as u64)
        {
            return Err(AuthError::JwksUnavailable);
        }
        let mut bytes = Vec::new();
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|_| AuthError::JwksUnavailable)?;
            if bytes.len().saturating_add(chunk.len()) > MAX_JWKS_BYTES {
                return Err(AuthError::JwksUnavailable);
            }
            bytes.extend_from_slice(&chunk);
        }
        let set: JwkSet = serde_json::from_slice(&bytes).map_err(|_| AuthError::JwksUnavailable)?;

        let mut keys = HashMap::new();
        let mut seen_kids = HashSet::new();
        for jwk in &set.keys {
            let Some(kid) = jwk.common.key_id.as_deref() else {
                continue;
            };
            if !seen_kids.insert(kid) {
                return Err(AuthError::JwksUnavailable);
            }
            let is_eddsa = matches!(
                &jwk.algorithm,
                AlgorithmParameters::OctetKeyPair(parameters)
                    if parameters.curve == EllipticCurve::Ed25519
            ) && jwk
                .common
                .key_algorithm
                .as_ref()
                .is_none_or(|algorithm| algorithm == &KeyAlgorithm::EdDSA)
                && jwk
                    .common
                    .public_key_use
                    .as_ref()
                    .is_none_or(|usage| usage == &PublicKeyUse::Signature)
                && jwk
                    .common
                    .key_operations
                    .as_ref()
                    .is_none_or(|operations| operations.contains(&KeyOperations::Verify));
            if is_eddsa {
                let key = DecodingKey::from_jwk(jwk).map_err(|_| AuthError::JwksUnavailable)?;
                keys.insert(kid.to_owned(), key);
            }
        }
        if keys.is_empty() {
            return Err(AuthError::JwksUnavailable);
        }
        Ok(keys)
    }
}

pub struct IdentitySigner {
    key: Arc<[u8]>,
    key_id: String,
}

impl IdentitySigner {
    pub fn new(config: &AuthConfig) -> Self {
        Self {
            key: Arc::from(config.identity_signing_key.expose()),
            key_id: config.identity_signing_key_id.clone(),
        }
    }

    pub fn headers(
        &self,
        identity: &Identity,
        method: &Method,
        uri: &Uri,
        audience: &str,
    ) -> Result<HeaderMap, AuthError> {
        let roles = identity.roles.join(",");
        let issued_at = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| AuthError::InvalidToken)?
            .as_secs()
            .to_string();
        let path = uri
            .path_and_query()
            .map_or(uri.path(), |value| value.as_str());
        let canonical = format!(
            "v1\n{}\n{}\n{}\n{}\n{}\n{}\n{}",
            self.key_id, identity.subject, roles, issued_at, method, path, audience
        );
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.key)
            .expect("gateway identity signing key accepts arbitrary length");
        mac.update(canonical.as_bytes());
        let signature = format!("v1={}", URL_SAFE_NO_PAD.encode(mac.finalize().into_bytes()));

        let mut headers = HeaderMap::new();
        headers.insert(
            "x-identity-key-id",
            HeaderValue::from_str(&self.key_id).map_err(|_| AuthError::InvalidToken)?,
        );
        headers.insert(
            "x-identity-subject",
            HeaderValue::from_str(&identity.subject).map_err(|_| AuthError::InvalidToken)?,
        );
        headers.insert(
            "x-identity-roles",
            HeaderValue::from_str(&roles).map_err(|_| AuthError::InvalidToken)?,
        );
        headers.insert(
            "x-identity-issued-at",
            HeaderValue::from_str(&issued_at).expect("unix timestamp is a valid header"),
        );
        headers.insert(
            "x-identity-signature",
            HeaderValue::from_str(&signature).expect("base64url signature is a valid header"),
        );
        Ok(headers)
    }
}

fn bearer_token(headers: &HeaderMap) -> Result<&str, AuthError> {
    let value = headers
        .get(AUTHORIZATION)
        .ok_or(AuthError::MissingToken)?
        .to_str()
        .map_err(|_| AuthError::InvalidToken)?;
    let (scheme, token) = value.split_once(' ').ok_or(AuthError::InvalidToken)?;
    if !scheme.eq_ignore_ascii_case("Bearer")
        || token.is_empty()
        || token.contains(char::is_whitespace)
    {
        return Err(AuthError::InvalidToken);
    }
    Ok(token)
}

fn is_safe_header_component(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 512
        && value
            .bytes()
            .all(|byte| matches!(byte, 0x21..=0x2b | 0x2d..=0x7e))
}

pub fn has_reserved_identity_header(headers: &HeaderMap) -> bool {
    headers
        .keys()
        .any(|name| name.as_str().starts_with("x-identity-"))
}

pub fn is_admin(identity: &Identity) -> bool {
    identity
        .roles
        .iter()
        .any(|role| role.eq_ignore_ascii_case("Admin"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_ambiguous_or_malformed_bearer_headers() {
        let mut headers = HeaderMap::new();
        assert_eq!(bearer_token(&headers), Err(AuthError::MissingToken));

        headers.insert(AUTHORIZATION, HeaderValue::from_static("Basic abc"));
        assert_eq!(bearer_token(&headers), Err(AuthError::InvalidToken));

        headers.insert(AUTHORIZATION, HeaderValue::from_static("Bearer a b"));
        assert_eq!(bearer_token(&headers), Err(AuthError::InvalidToken));

        headers.insert(AUTHORIZATION, HeaderValue::from_static("bearer abc"));
        assert_eq!(bearer_token(&headers), Ok("abc"));
    }

    #[test]
    fn detects_every_reserved_identity_header() {
        let mut headers = HeaderMap::new();
        headers.insert("x-identity-subject", HeaderValue::from_static("forged"));
        assert!(has_reserved_identity_header(&headers));
    }

    #[test]
    fn accepts_only_header_safe_identity_components() {
        assert!(is_safe_header_component("rider-123"));
        assert!(!is_safe_header_component("rider,admin"));
        assert!(!is_safe_header_component("line\nbreak"));
        assert!(!is_safe_header_component(""));
    }

    #[test]
    fn recognizes_admin_case_insensitively() {
        assert!(is_admin(&Identity {
            subject: "admin-1".to_owned(),
            roles: vec!["admin".to_owned()],
        }));
    }

    #[test]
    fn dotnet_role_claim_name_stays_in_sync() {
        assert_eq!(
            DOTNET_ROLE_CLAIM,
            "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
        );
    }
}
