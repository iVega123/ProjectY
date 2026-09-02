use std::sync::atomic::{AtomicU64, Ordering};

#[derive(Default)]
pub struct GatewayMetrics {
    rate_limit_degraded_total: AtomicU64,
}

impl GatewayMetrics {
    pub fn record_rate_limit_degraded(&self) {
        self.rate_limit_degraded_total
            .fetch_add(1, Ordering::Relaxed);
    }

    pub fn rate_limit_degraded_total(&self) -> u64 {
        self.rate_limit_degraded_total.load(Ordering::Relaxed)
    }

    pub fn render(&self) -> String {
        format!(
            "# HELP gateway_ratelimit_degraded_total Requests allowed because the Redis rate limiter was unavailable.\n\
# TYPE gateway_ratelimit_degraded_total counter\n\
gateway_ratelimit_degraded_total {}\n",
            self.rate_limit_degraded_total()
        )
    }
}
