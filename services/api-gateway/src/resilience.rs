use std::{
    sync::{Arc, Mutex},
    time::Duration,
};

use serde::Serialize;
use tokio::{
    sync::{OwnedSemaphorePermit, Semaphore},
    time::Instant,
};

use crate::config::{ResilienceConfig, UpstreamName, UpstreamResilienceConfig};

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum CircuitState {
    Closed,
    Open,
    HalfOpen,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct CircuitSnapshot {
    pub upstream: &'static str,
    pub state: CircuitState,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Rejection {
    CircuitOpen(Duration),
    BulkheadFull,
}

#[derive(Debug)]
pub struct Admission {
    _permit: OwnedSemaphorePermit,
    breaker: BreakerAdmission,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct BreakerAdmission {
    generation: u64,
    state: CircuitState,
}

pub struct UpstreamPolicy {
    config: UpstreamResilienceConfig,
    breaker: Mutex<CircuitBreaker>,
    bulkhead: Arc<Semaphore>,
}

impl UpstreamPolicy {
    fn new(config: UpstreamResilienceConfig) -> Self {
        Self {
            config,
            breaker: Mutex::new(CircuitBreaker::new(config)),
            bulkhead: Arc::new(Semaphore::new(config.max_concurrency)),
        }
    }

    pub fn config(&self) -> UpstreamResilienceConfig {
        self.config
    }

    pub fn try_admit(&self) -> Result<Admission, Rejection> {
        let permit = self
            .bulkhead
            .clone()
            .try_acquire_owned()
            .map_err(|_| Rejection::BulkheadFull)?;
        let breaker = self
            .breaker
            .lock()
            .expect("circuit breaker lock is not poisoned")
            .before_request(Instant::now())?;
        Ok(Admission {
            _permit: permit,
            breaker,
        })
    }

    pub fn record_success(&self, admission: &Admission) {
        self.breaker
            .lock()
            .expect("circuit breaker lock is not poisoned")
            .record_success(admission.breaker);
    }

    pub fn record_failure(&self, admission: &Admission) {
        self.breaker
            .lock()
            .expect("circuit breaker lock is not poisoned")
            .record_failure(admission.breaker, Instant::now());
    }

    pub fn state(&self) -> CircuitState {
        self.breaker
            .lock()
            .expect("circuit breaker lock is not poisoned")
            .state
    }
}

pub struct ResilienceRegistry {
    auth_gate: Arc<UpstreamPolicy>,
    rider_manager: Arc<UpstreamPolicy>,
    moto_hub: Arc<UpstreamPolicy>,
    rental_operations: Arc<UpstreamPolicy>,
}

impl ResilienceRegistry {
    pub fn new(config: &ResilienceConfig) -> Self {
        Self {
            auth_gate: Arc::new(UpstreamPolicy::new(config.auth_gate)),
            rider_manager: Arc::new(UpstreamPolicy::new(config.rider_manager)),
            moto_hub: Arc::new(UpstreamPolicy::new(config.moto_hub)),
            rental_operations: Arc::new(UpstreamPolicy::new(config.rental_operations)),
        }
    }

    pub fn policy(&self, upstream: UpstreamName) -> Arc<UpstreamPolicy> {
        match upstream {
            UpstreamName::AuthGate => self.auth_gate.clone(),
            UpstreamName::RiderManager => self.rider_manager.clone(),
            UpstreamName::MotoHub => self.moto_hub.clone(),
            UpstreamName::RentalOperations => self.rental_operations.clone(),
        }
    }

    pub fn snapshots(&self) -> Vec<CircuitSnapshot> {
        let mut snapshots = Vec::with_capacity(UpstreamName::ALL.len());
        for upstream in UpstreamName::ALL {
            snapshots.push(CircuitSnapshot {
                upstream: upstream.as_str(),
                state: self.policy(upstream).state(),
            });
        }
        snapshots
    }

    pub fn render_metrics(&self) -> String {
        let snapshots = self.snapshots();
        let mut output = String::from(
            "# HELP gateway_upstream_circuit_breaker_state Current circuit breaker state by upstream.\n\
# TYPE gateway_upstream_circuit_breaker_state gauge\n",
        );
        for snapshot in snapshots {
            for state in [
                CircuitState::Closed,
                CircuitState::Open,
                CircuitState::HalfOpen,
            ] {
                let value = u8::from(snapshot.state == state);
                output.push_str(&format!(
                    "gateway_upstream_circuit_breaker_state{{upstream=\"{}\",state=\"{}\"}} {value}\n",
                    snapshot.upstream,
                    state.as_str()
                ));
            }
        }
        output
    }
}

impl CircuitState {
    const fn as_str(self) -> &'static str {
        match self {
            Self::Closed => "closed",
            Self::Open => "open",
            Self::HalfOpen => "half_open",
        }
    }
}

struct CircuitBreaker {
    state: CircuitState,
    generation: u64,
    consecutive_failures: u32,
    opened_at: Option<Instant>,
    half_open_probe_in_flight: bool,
    failure_threshold: u32,
    open_duration: Duration,
}

impl CircuitBreaker {
    fn new(config: UpstreamResilienceConfig) -> Self {
        Self {
            state: CircuitState::Closed,
            generation: 0,
            consecutive_failures: 0,
            opened_at: None,
            half_open_probe_in_flight: false,
            failure_threshold: config.breaker_failure_threshold,
            open_duration: config.breaker_open_duration,
        }
    }

    fn before_request(&mut self, now: Instant) -> Result<BreakerAdmission, Rejection> {
        match self.state {
            CircuitState::Closed => Ok(self.admission()),
            CircuitState::Open => {
                let elapsed = now.saturating_duration_since(
                    self.opened_at
                        .expect("an open circuit records when it opened"),
                );
                if elapsed >= self.open_duration {
                    self.state = CircuitState::HalfOpen;
                    self.half_open_probe_in_flight = true;
                    Ok(self.admission())
                } else {
                    Err(Rejection::CircuitOpen(self.open_duration - elapsed))
                }
            }
            CircuitState::HalfOpen if !self.half_open_probe_in_flight => {
                self.half_open_probe_in_flight = true;
                Ok(self.admission())
            }
            CircuitState::HalfOpen => Err(Rejection::CircuitOpen(self.open_duration)),
        }
    }

    fn admission(&self) -> BreakerAdmission {
        BreakerAdmission {
            generation: self.generation,
            state: self.state,
        }
    }

    fn record_success(&mut self, admission: BreakerAdmission) {
        if !self.accepts(admission) {
            return;
        }

        if self.state == CircuitState::HalfOpen {
            self.advance_generation();
        }
        self.state = CircuitState::Closed;
        self.consecutive_failures = 0;
        self.opened_at = None;
        self.half_open_probe_in_flight = false;
    }

    fn record_failure(&mut self, admission: BreakerAdmission, now: Instant) {
        if !self.accepts(admission) {
            return;
        }

        if self.state == CircuitState::HalfOpen {
            self.open(now);
            return;
        }
        if self.state == CircuitState::Closed {
            self.consecutive_failures = self.consecutive_failures.saturating_add(1);
            if self.consecutive_failures >= self.failure_threshold {
                self.open(now);
            }
        }
    }

    fn open(&mut self, now: Instant) {
        self.advance_generation();
        self.state = CircuitState::Open;
        self.opened_at = Some(now);
        self.half_open_probe_in_flight = false;
    }

    fn accepts(&self, admission: BreakerAdmission) -> bool {
        admission.generation == self.generation && admission.state == self.state
    }

    fn advance_generation(&mut self) {
        self.generation = self.generation.wrapping_add(1);
    }
}

pub fn full_jitter_delay(config: UpstreamResilienceConfig, retry_number: u32) -> Duration {
    let exponent = retry_number.saturating_sub(1).min(31);
    let ceiling = config
        .retry_base_delay
        .saturating_mul(1_u32 << exponent)
        .min(config.retry_max_delay);
    let ceiling_millis = u64::try_from(ceiling.as_millis()).unwrap_or(u64::MAX);
    Duration::from_millis(fastrand::u64(0..=ceiling_millis))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn config() -> UpstreamResilienceConfig {
        UpstreamResilienceConfig {
            timeout: Duration::from_millis(50),
            max_concurrency: 1,
            breaker_failure_threshold: 2,
            breaker_open_duration: Duration::from_millis(100),
            max_retries: 3,
            retry_base_delay: Duration::from_millis(10),
            retry_max_delay: Duration::from_millis(25),
        }
    }

    #[test]
    fn breaker_opens_allows_one_half_open_probe_and_closes_on_success() {
        let now = Instant::now();
        let mut breaker = CircuitBreaker::new(config());

        let first = breaker.before_request(now).unwrap();
        breaker.record_failure(first, now);
        assert_eq!(breaker.state, CircuitState::Closed);
        let second = breaker.before_request(now).unwrap();
        breaker.record_failure(second, now);
        assert_eq!(breaker.state, CircuitState::Open);
        assert!(
            breaker
                .before_request(now + Duration::from_millis(99))
                .is_err()
        );
        let probe = breaker
            .before_request(now + Duration::from_millis(100))
            .unwrap();
        assert_eq!(breaker.state, CircuitState::HalfOpen);
        assert!(
            breaker
                .before_request(now + Duration::from_millis(100))
                .is_err()
        );

        breaker.record_success(probe);
        assert_eq!(breaker.state, CircuitState::Closed);
        assert!(
            breaker
                .before_request(now + Duration::from_millis(100))
                .is_ok()
        );
    }

    #[test]
    fn failed_half_open_probe_reopens_the_breaker() {
        let now = Instant::now();
        let mut breaker = CircuitBreaker::new(config());
        let first = breaker.before_request(now).unwrap();
        breaker.record_failure(first, now);
        let second = breaker.before_request(now).unwrap();
        breaker.record_failure(second, now);
        let probe = breaker
            .before_request(now + Duration::from_millis(100))
            .unwrap();

        breaker.record_failure(probe, now + Duration::from_millis(101));

        assert_eq!(breaker.state, CircuitState::Open);
        assert!(
            breaker
                .before_request(now + Duration::from_millis(150))
                .is_err()
        );
    }

    #[test]
    fn pre_open_success_cannot_close_a_new_breaker_generation() {
        let now = Instant::now();
        let mut policy = config();
        policy.breaker_failure_threshold = 1;
        let mut breaker = CircuitBreaker::new(policy);
        let older_request = breaker.before_request(now).unwrap();
        let failing_request = breaker.before_request(now).unwrap();

        breaker.record_failure(failing_request, now);
        assert_eq!(breaker.state, CircuitState::Open);

        breaker.record_success(older_request);

        assert_eq!(breaker.state, CircuitState::Open);
        assert!(
            breaker
                .before_request(now + Duration::from_millis(99))
                .is_err()
        );
    }

    #[test]
    fn bulkhead_rejects_instead_of_queueing() {
        let policy = UpstreamPolicy::new(config());
        let first = policy.try_admit().unwrap();

        assert_eq!(policy.try_admit().unwrap_err(), Rejection::BulkheadFull);

        drop(first);
        assert!(policy.try_admit().is_ok());
    }

    #[test]
    fn full_jitter_stays_within_exponential_and_maximum_bounds() {
        let policy = config();
        for _ in 0..100 {
            assert!(full_jitter_delay(policy, 1) <= Duration::from_millis(10));
            assert!(full_jitter_delay(policy, 2) <= Duration::from_millis(20));
            assert!(full_jitter_delay(policy, 3) <= Duration::from_millis(25));
            assert!(full_jitter_delay(policy, 20) <= Duration::from_millis(25));
        }
    }
}
