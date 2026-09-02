use std::{future::Future, pin::Pin, time::Duration};

use redis::{Client, aio::ConnectionManager};
use tokio::{sync::OnceCell, time::timeout};

use crate::config::TokenBucketConfig;

const TOKEN_BUCKET_SCRIPT: &str = r#"
local now = redis.call('TIME')
local now_ms = (tonumber(now[1]) * 1000) + math.floor(tonumber(now[2]) / 1000)
local values = redis.call('HMGET', KEYS[1], 'tokens', 'updated_at')
local capacity = tonumber(ARGV[1]) * 1000
local refill_per_minute = tonumber(ARGV[2])
local tokens = tonumber(values[1]) or capacity
local updated_at = tonumber(values[2]) or now_ms
local elapsed_ms = math.max(0, now_ms - updated_at)
tokens = math.min(capacity, tokens + (elapsed_ms * refill_per_minute / 60))

local allowed = 0
local retry_ms = 0
if tokens >= 1000 then
  allowed = 1
  tokens = tokens - 1000
else
  retry_ms = math.ceil((1000 - tokens) * 60 / refill_per_minute)
end

redis.call('HSET', KEYS[1], 'tokens', tokens, 'updated_at', now_ms)
local ttl_ms = math.max(1000, math.ceil((capacity * 60 / refill_per_minute) * 2))
redis.call('PEXPIRE', KEYS[1], ttl_ms)
return { allowed, math.floor(tokens / 1000), retry_ms }
"#;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RateLimitDecision {
    pub allowed: bool,
    pub remaining: u64,
    pub retry_after_seconds: u64,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RateLimitError {
    Unavailable,
}

pub trait RateLimiter: Send + Sync {
    fn check<'a>(
        &'a self,
        key: &'a str,
        bucket: TokenBucketConfig,
    ) -> Pin<Box<dyn Future<Output = Result<RateLimitDecision, RateLimitError>> + Send + 'a>>;
}

pub struct RedisRateLimiter {
    client: Client,
    connection: OnceCell<ConnectionManager>,
    operation_timeout: Duration,
}

impl RedisRateLimiter {
    pub fn new(redis_url: &str, operation_timeout: Duration) -> Result<Self, redis::RedisError> {
        Ok(Self {
            client: Client::open(redis_url)?,
            connection: OnceCell::new(),
            operation_timeout,
        })
    }

    async fn evaluate(
        &self,
        key: &str,
        bucket: TokenBucketConfig,
    ) -> Result<RateLimitDecision, RateLimitError> {
        let connection = timeout(
            self.operation_timeout,
            self.connection
                .get_or_try_init(|| ConnectionManager::new(self.client.clone())),
        )
        .await
        .map_err(|_| RateLimitError::Unavailable)?
        .map_err(|_| RateLimitError::Unavailable)?;
        let mut connection = connection.clone();
        let result = timeout(
            self.operation_timeout,
            redis::cmd("EVAL")
                .arg(TOKEN_BUCKET_SCRIPT)
                .arg(1)
                .arg(key)
                .arg(bucket.capacity)
                .arg(bucket.refill_per_minute)
                .query_async::<(u64, u64, u64)>(&mut connection),
        )
        .await
        .map_err(|_| RateLimitError::Unavailable)?
        .map_err(|_| RateLimitError::Unavailable)?;

        Ok(RateLimitDecision {
            allowed: result.0 == 1,
            remaining: result.1,
            retry_after_seconds: result.2.div_ceil(1000).max(1),
        })
    }
}

impl RateLimiter for RedisRateLimiter {
    fn check<'a>(
        &'a self,
        key: &'a str,
        bucket: TokenBucketConfig,
    ) -> Pin<Box<dyn Future<Output = Result<RateLimitDecision, RateLimitError>> + Send + 'a>> {
        Box::pin(self.evaluate(key, bucket))
    }
}

#[cfg(test)]
mod tests {
    use std::time::{SystemTime, UNIX_EPOCH};

    use super::*;

    #[tokio::test]
    async fn two_gateway_instances_share_one_atomic_bucket() {
        let Ok(redis_url) = std::env::var("TEST_REDIS_URL") else {
            return;
        };
        let first = RedisRateLimiter::new(&redis_url, Duration::from_secs(2)).unwrap();
        let second = RedisRateLimiter::new(&redis_url, Duration::from_secs(2)).unwrap();
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let key = format!("projecty:test:ratelimit:{suffix}");
        let bucket = TokenBucketConfig {
            capacity: 1,
            refill_per_minute: 1,
        };

        let accepted = first.check(&key, bucket).await.unwrap();
        let refused = second.check(&key, bucket).await.unwrap();

        assert!(accepted.allowed);
        assert_eq!(accepted.remaining, 0);
        assert!(!refused.allowed);
        assert_eq!(refused.remaining, 0);
        assert!(refused.retry_after_seconds > 0);
    }
}
