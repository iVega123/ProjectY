use std::process::ExitCode;

use api_gateway::{build_app, config::Config, healthcheck, serve};
use tokio::net::TcpListener;
use tracing::info;

mod telemetry;

#[tokio::main]
async fn main() -> ExitCode {
    let arguments: Vec<_> = std::env::args().skip(1).collect();
    let result = match arguments.as_slice() {
        [] => run_server_with_telemetry().await,
        [argument] if argument == "--healthcheck" => run_healthcheck().await,
        _ => Err("usage: api-gateway [--healthcheck]".to_owned()),
    };

    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("{message}");
            ExitCode::FAILURE
        }
    }
}

async fn run_server_with_telemetry() -> Result<(), String> {
    let telemetry = telemetry::TelemetryGuard::initialize()
        .map_err(|error| format!("could not initialize OpenTelemetry: {error}"))?;
    let result = run_server().await;
    telemetry.shutdown();
    result
}

async fn run_healthcheck() -> Result<(), String> {
    let (url, timeout) = Config::healthcheck_from_env()?;
    healthcheck(url, timeout).await
}

async fn run_server() -> Result<(), String> {
    let config = Config::from_env()?;
    let bind = config.bind;
    let listener = TcpListener::bind(bind)
        .await
        .map_err(|error| format!("could not bind gateway to {bind}: {error}"))?;
    let app = build_app(config).map_err(|error| format!("could not build gateway: {error}"))?;

    info!(address = %bind, "api gateway listening");
    serve(listener, app, shutdown_signal())
        .await
        .map_err(|error| format!("gateway server failed: {error}"))?;
    info!("api gateway drained all in-flight requests");
    Ok(())
}

async fn shutdown_signal() {
    let ctrl_c = async {
        tokio::signal::ctrl_c()
            .await
            .expect("failed to install Ctrl+C handler");
    };

    #[cfg(unix)]
    let terminate = async {
        tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("failed to install SIGTERM handler")
            .recv()
            .await;
    };

    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {},
        _ = terminate => {},
    }

    info!("shutdown requested; stopping accepts and draining in-flight requests");
}
