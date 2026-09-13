use std::{
    collections::{HashMap, HashSet},
    sync::Arc,
    time::{Instant, SystemTime, UNIX_EPOCH},
};

use axum::http::{HeaderMap, HeaderValue, Method, Uri, header::AUTHORIZATION};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use futures_util::StreamExt;
use hmac::{Hmac, KeyInit, Mac};
use jsonwebtoken::{
    Algorithm, DecodingKey, Validation, decode, decode_header,
    jwk::{AlgorithmParameters, EllipticCurve, JwkSet, KeyAlgorithm, KeyOperations, PublicKeyUse},
};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use tokio::sync::{Mutex, RwLock};

use crate::config::AuthConfig;

const MAX_JWKS_BYTES: usize = 256 * 1024;
#[cfg(test)]
const DOTNET_ROLE_CLAIM: &str = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Identity {
    pub subject: String,
    pub roles: Vec<String>,
    pub token_id: String,
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
    jti: String,
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
        validation.set_required_spec_claims(&["sub", "iss", "aud", "exp", "iat", "jti"]);
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
        if !is_safe_header_component(&claims.sub)
            || claims.jti.len() > 128
            || !is_safe_header_component(&claims.jti)
        {
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
            token_id: claims.jti,
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

    /// O envelope de uma requisição, assinado agora.
    ///
    /// `body` são os bytes exatos que o upstream vai receber. O `v2` os liga à
    /// assinatura; sem isso um envelope capturado serviria, na mesma rota e
    /// dentro da janela, para qualquer outro corpo (#191).
    pub fn headers(
        &self,
        identity: &Identity,
        method: &Method,
        uri: &Uri,
        audience: &str,
        body: &[u8],
    ) -> Result<HeaderMap, AuthError> {
        let issued_at = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| AuthError::InvalidToken)?
            .as_secs();
        self.headers_at(identity, method, uri, audience, body, issued_at)
    }

    fn headers_at(
        &self,
        identity: &Identity,
        method: &Method,
        uri: &Uri,
        audience: &str,
        body: &[u8],
        issued_at: u64,
    ) -> Result<HeaderMap, AuthError> {
        let roles = identity.roles.join(",");
        let issued_at = issued_at.to_string();
        let path = uri
            .path_and_query()
            .map_or(uri.path(), |value| value.as_str());
        let bound = format!(
            "{}\n{}\n{}\n{}\n{}\n{}\n{}",
            self.key_id, identity.subject, roles, issued_at, method, path, audience
        );
        // As duas versões, lado a lado, enquanto houver verificador em `v1`: um
        // verificador que só conhece `v1` ignora o cabeçalho que não conhece, e
        // um que conhece `v2` confere `v2` sempre que ele vier. Mandar só `v2`
        // antes de todos saberem lê-lo trancaria os serviços para fora, que é a
        // falha do #136.
        let v1 = format!("v1={}", self.sign(&format!("v1\n{bound}")));
        let v2 = format!(
            "v2={}",
            self.sign(&format!("v2\n{bound}\n{}", body_digest(body)))
        );

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
            HeaderValue::from_str(&v1).expect("base64url signature is a valid header"),
        );
        headers.insert(
            "x-identity-signature-v2",
            HeaderValue::from_str(&v2).expect("base64url signature is a valid header"),
        );
        Ok(headers)
    }

    fn sign(&self, canonical: &str) -> String {
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.key)
            .expect("gateway identity signing key accepts arbitrary length");
        mac.update(canonical.as_bytes());
        URL_SAFE_NO_PAD.encode(mac.finalize().into_bytes())
    }
}

/// A última linha do `v2`: SHA-256 dos bytes do corpo, em hex minúsculo.
///
/// Sem corpo e corpo vazio são a mesma coisa, o digest de zero bytes. A regra é
/// escrita aqui, e repetida em cada verificador, para que nenhum deles precise
/// adivinhar se um GET assina uma linha vazia, um traço ou nada.
pub fn body_digest(body: &[u8]) -> String {
    use std::fmt::Write as _;

    let mut hex = String::with_capacity(64);
    for byte in Sha256::digest(body) {
        write!(hex, "{byte:02x}").expect("writing to a String cannot fail");
    }
    hex
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

    /// O JWKS e o token abaixo saíram do `identity`, em Go, e não deste
    /// arquivo.
    ///
    /// Todo o resto da suíte gera o par de chaves aqui mesmo, em Rust, e
    /// assina em Rust -- o que prova que este validador concorda consigo
    /// mesmo. Concordar consigo mesmo não é o risco: o risco é o emissor e o
    /// validador ficarem em linguagens diferentes e discordarem no formato,
    /// que é uma falha que não aparece como build quebrado, e sim como
    /// ninguém conseguindo entrar.
    ///
    /// O que este vetor congela: a forma do JWKS (OKP/Ed25519, `x` em
    /// base64url sem padding), a seleção pelo `kid`, os bytes da assinatura
    /// EdDSA, e os nomes e tipos das reivindicações -- inclusive `aud` como
    /// LISTA, que é o que permite um token só servir no rental-core e no
    /// billing.
    ///
    /// O que ele deliberadamente NÃO cobre: expiração. Um vetor fixo está
    /// sempre vencido, e a validade de tempo já é exercitada pelos testes que
    /// emitem localmente.
    const IDENTITY_JWKS: &str = concat!(
        r#"{"keys":[{"kty":"OKP","crv":"Ed25519","#,
        r#""x":"A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg","#,
        r#""kid":"20260908-goldvector","alg":"EdDSA","use":"sig"}]}"#
    );
    const IDENTITY_TOKEN: &str = concat!(
        "eyJhbGciOiJFZERTQSIsImtpZCI6IjIwMjYwOTA4LWdvbGR2ZWN0b3IiLCJ0eXAiOiJKV1QifQ.",
        "eyJyb2xlcyI6WyJSaWRlciJdLCJpc3MiOiJwcm9qZWN0eS5pZGVudGl0eSIsInN1YiI6IjhhMWY5",
        "YzJlLTAwMDAtNDAwMC04MDAwLTAwMDAwMDAwMDAwMSIsImF1ZCI6WyJwcm9qZWN0eS5yZW50YWwt",
        "Y29yZSIsInByb2plY3R5LmJpbGxpbmciXSwiZXhwIjoxNzkwMDAwMzAwLCJuYmYiOjE3OTAwMDAw",
        "MDAsImlhdCI6MTc5MDAwMDAwMCwianRpIjoiZGUzMDA0ZjQtZWY1Yy00MmQ3LTllMzctY2EwZjQy",
        "NWI3M2Q3In0.",
        "WRNgWXrewn8Npyd27gR15L5yi9d911N_uv_0riY2VZ3ddW-l4uZzrnNwqrJatZMqo00nTSAKKIa4",
        "Az1DnmRWCg"
    );

    #[test]
    fn accepts_a_token_the_go_identity_service_minted() {
        let set: JwkSet = serde_json::from_str(IDENTITY_JWKS).expect("JWKS do identity ilegível");

        let header = decode_header(IDENTITY_TOKEN).expect("cabeçalho ilegível");
        assert_eq!(header.alg, Algorithm::EdDSA);
        let kid = header.kid.expect("o identity precisa carimbar o kid");
        let jwk = set.find(&kid).expect("o JWKS não publica o kid do token");
        let key = DecodingKey::from_jwk(jwk).expect("a chave publicada não é utilizável");

        let mut validation = Validation::new(Algorithm::EdDSA);
        validation.set_issuer(&["projecty.identity"]);
        // A audiência do billing, e não a do rental-core: um token com `aud`
        // em lista vale nos dois, que é como o #137 deixa de precisar que o
        // billing responda pelo nome do rental-core.
        validation.set_audience(&["projecty.billing"]);
        validation.set_required_spec_claims(&["sub", "iss", "aud", "exp", "iat", "jti"]);
        validation.validate_exp = false;

        let claims = decode::<Claims>(IDENTITY_TOKEN, &key, &validation)
            .expect("o portão recusou um token do identity")
            .claims;

        assert_eq!(claims.sub, "8a1f9c2e-0000-4000-8000-000000000001");
        assert_eq!(claims.jti, "de3004f4-ef5c-42d7-9e37-ca0f425b73d7");
        assert_eq!(claims.exp - claims.iat, 300);

        let mut roles = Vec::new();
        claims.role.append_to(&mut roles);
        claims.roles.append_to(&mut roles);
        assert_eq!(roles, vec!["Rider".to_owned()]);
    }

    #[test]
    fn rejects_a_token_the_identity_did_not_sign() {
        let set: JwkSet = serde_json::from_str(IDENTITY_JWKS).unwrap();
        let jwk = set.find("20260908-goldvector").unwrap();
        let key = DecodingKey::from_jwk(jwk).unwrap();

        // Um byte mexido na assinatura. Sem isto, o teste acima passaria
        // igual com uma verificação que não verifica nada.
        let mut tampered = IDENTITY_TOKEN.to_owned();
        tampered.pop();
        tampered.push(if IDENTITY_TOKEN.ends_with('A') {
            'B'
        } else {
            'A'
        });

        let mut validation = Validation::new(Algorithm::EdDSA);
        validation.set_issuer(&["projecty.identity"]);
        validation.set_audience(&["projecty.billing"]);
        validation.validate_exp = false;

        assert!(decode::<Claims>(&tampered, &key, &validation).is_err());
    }

    /// Os envelopes que os três verificadores prendem nos testes deles.
    ///
    /// Os valores abaixo não saíram deste arquivo: foram calculados à parte, com
    /// `openssl dgst -sha256 -mac HMAC`, a partir da string canônica escrita no
    /// ADR 0008. Este teste prova que o portão produz exatamente esses bytes; o
    /// `TestAcceptsAV2EnvelopeTheGatewaySigned` (identity, Go), o
    /// `o envelope v2 que o portao assinou e aceito` (billing, Kotlin) e o
    /// `GatewayIdentityEnvelopeTests` (rental-core, C#) provam que cada
    /// verificador aceita os mesmos. Uma divergência em qualquer ponta falha
    /// de um lado em vez de virar porta aberta ou serviço trancado.
    struct Golden {
        method: Method,
        uri: &'static str,
        audience: &'static str,
        body: &'static [u8],
        v1: &'static str,
        v2: &'static str,
    }

    const GOLDEN_ISSUED_AT: u64 = 1_789_300_000;
    const EMPTY_BODY_DIGEST: &str =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    fn goldens() -> [Golden; 3] {
        [
            // O corpo que o #191 nomeia: a CNH de um piloto.
            Golden {
                method: Method::PUT,
                uri: "/update-image",
                audience: "projecty.identity",
                body: b"cnh-image:original",
                v1: "v1=WfqfOycgzRAvAjHP2W7VvzA6AJCQVrxU1GEY4KvWQJM",
                v2: "v2=V6Nzvgn7pJMZjCeBhFND7JmHakPWzdkexDLY8Xk89u8",
            },
            // Sem corpo: a regra do digest vazio, e a query dentro do caminho.
            Golden {
                method: Method::GET,
                uri: "/api/invoices?rentalIds=a,b",
                audience: "projecty.billing",
                body: b"",
                v1: "v1=jPNeT5mDVdMdyKNT5UXQYPfhI8Z-Hh9geXNfdYWKzl4",
                v2: "v2=w5r-_x9wbmOcOjdb-icslt_9rsL7Llm5Uy3FVQwVRzE",
            },
            Golden {
                method: Method::POST,
                uri: "/api/rental",
                audience: "projecty.rental-core",
                body: br#"{"motorcycleId":"00000000-0000-0000-0000-000000000001","plan":7}"#,
                v1: "v1=3vAv26TC3UzJTX5cWizLQx42_D7y7a4m9ZBX3r29gWY",
                v2: "v2=idQJwvMxcuM0EEsGGXa8tvOm32Dwcya9HJGh0vz8QwE",
            },
        ]
    }

    fn golden_signer() -> IdentitySigner {
        IdentitySigner {
            key: Arc::from(&[b'x'; 32][..]),
            key_id: "local-v1".to_owned(),
        }
    }

    fn golden_rider() -> Identity {
        Identity {
            subject: "rider-123".to_owned(),
            roles: vec!["Rider".to_owned()],
            token_id: "token-1".to_owned(),
        }
    }

    fn signed(golden: &Golden, body: &[u8]) -> HeaderMap {
        golden_signer()
            .headers_at(
                &golden_rider(),
                &golden.method,
                &golden.uri.parse().unwrap(),
                golden.audience,
                body,
                GOLDEN_ISSUED_AT,
            )
            .unwrap()
    }

    fn header<'a>(headers: &'a HeaderMap, name: &str) -> &'a str {
        headers.get(name).unwrap().to_str().unwrap()
    }

    #[test]
    fn signs_the_v2_envelopes_the_verifiers_pin() {
        for golden in goldens() {
            let headers = signed(&golden, golden.body);
            assert_eq!(
                header(&headers, "x-identity-signature-v2"),
                golden.v2,
                "{}",
                golden.uri
            );
        }
    }

    /// O portão em `v2` diante de um verificador ainda em `v1`.
    ///
    /// O verificador antigo lê cinco cabeçalhos e ignora o resto. O que ele
    /// precisa, então, é que `x-identity-signature` continue sendo o `v1` de
    /// sempre, byte a byte, e que nenhum dos cinco apareça duas vezes -- ele
    /// recusa cabeçalho repetido.
    #[test]
    fn still_signs_v1_unchanged_for_verifiers_that_have_not_rolled() {
        for golden in goldens() {
            let headers = signed(&golden, golden.body);
            assert_eq!(header(&headers, "x-identity-signature"), golden.v1);
            for name in [
                "x-identity-key-id",
                "x-identity-subject",
                "x-identity-roles",
                "x-identity-issued-at",
                "x-identity-signature",
            ] {
                assert_eq!(headers.get_all(name).iter().count(), 1, "{name}");
            }
        }
    }

    /// A troca de corpo muda o `v2` e não muda o `v1`.
    ///
    /// A primeira metade é o #191 fechado. A segunda é por que ele só fecha de
    /// vez quando os verificadores deixarem de aceitar `v1`: a assinatura antiga
    /// continua valendo para qualquer corpo.
    #[test]
    fn a_substituted_body_changes_the_v2_signature_and_not_the_v1() {
        let golden = &goldens()[0];
        let original = signed(golden, golden.body);
        let substituted = signed(golden, b"cnh-image:attacker");

        assert_ne!(
            header(&original, "x-identity-signature-v2"),
            header(&substituted, "x-identity-signature-v2")
        );
        assert_eq!(
            header(&original, "x-identity-signature"),
            header(&substituted, "x-identity-signature")
        );
    }

    #[test]
    fn no_body_is_the_digest_of_zero_bytes() {
        assert_eq!(body_digest(b""), EMPTY_BODY_DIGEST);
        assert_eq!(
            body_digest(b"cnh-image:original"),
            "5039a57d34197a36dd750d0174078d45d0ee5a06f719aca106c178c4f1fba47a"
        );
    }

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
            token_id: "token-1".to_owned(),
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
