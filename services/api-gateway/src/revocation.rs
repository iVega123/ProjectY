use std::{future::Future, pin::Pin, time::Duration};

use redis::{Client, aio::ConnectionManager};
use tokio::{sync::OnceCell, time::timeout};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RevocationError {
    Unavailable,
}

pub trait RevocationStore: Send + Sync {
    fn is_revoked<'a>(
        &'a self,
        token_id: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, RevocationError>> + Send + 'a>>;
}

/// A chave que o identity grava ao revogar uma sessão, e que este portão lê.
///
/// As duas pontas estão em linguagens diferentes, e é aqui que elas podem
/// discordar sem que nada quebre: o identity gravaria uma chave que ninguém lê,
/// e a revogação continuaria parecendo feita. O formato fica preso nos dois
/// lados -- no teste abaixo e em `TestTheKeyIsTheOneTheGatewayReads`, em
/// services/identity/internal/revocations.
pub fn denylist_key(token_id: &str) -> String {
    format!("projecty:revoked:jti:{token_id}")
}

pub struct RedisRevocationStore {
    client: Client,
    connection: OnceCell<ConnectionManager>,
    operation_timeout: Duration,
}

impl RedisRevocationStore {
    pub fn new(redis_url: &str, operation_timeout: Duration) -> Result<Self, redis::RedisError> {
        Ok(Self {
            client: Client::open(redis_url)?,
            connection: OnceCell::new(),
            operation_timeout,
        })
    }

    async fn check(&self, token_id: &str) -> Result<bool, RevocationError> {
        let connection = timeout(
            self.operation_timeout,
            self.connection
                .get_or_try_init(|| ConnectionManager::new(self.client.clone())),
        )
        .await
        .map_err(|_| RevocationError::Unavailable)?
        .map_err(|_| RevocationError::Unavailable)?;
        let mut connection = connection.clone();
        timeout(
            self.operation_timeout,
            redis::cmd("EXISTS")
                .arg(denylist_key(token_id))
                .query_async::<bool>(&mut connection),
        )
        .await
        .map_err(|_| RevocationError::Unavailable)?
        .map_err(|_| RevocationError::Unavailable)
    }
}

impl RevocationStore for RedisRevocationStore {
    fn is_revoked<'a>(
        &'a self,
        token_id: &'a str,
    ) -> Pin<Box<dyn Future<Output = Result<bool, RevocationError>> + Send + 'a>> {
        Box::pin(self.check(token_id))
    }
}

#[cfg(test)]
mod tests {
    use super::denylist_key;

    #[test]
    fn the_denylist_key_is_the_one_identity_writes() {
        assert_eq!(
            denylist_key("de3004f4-ef5c-42d7-9e37-ca0f425b73d7"),
            "projecty:revoked:jti:de3004f4-ef5c-42d7-9e37-ca0f425b73d7"
        );
    }
}
