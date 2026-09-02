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
        let key = format!("projecty:revoked:jti:{token_id}");
        timeout(
            self.operation_timeout,
            redis::cmd("EXISTS")
                .arg(key)
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
