import Config

if config_env() != :test do
  config :projecty_telemetry, ProjectYTelemetry.Endpoint,
    secret_key_base: Base.encode64(:crypto.hash(:sha512, System.fetch_env!("TELEMETRY_SECRET_KEY_BASE"))),
    check_origin: String.split(System.get_env("TELEMETRY_ORIGINS", "//localhost:3001"), ",")
end
