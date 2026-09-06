import Config
config :phoenix, :json_library, Jason

config :projecty_telemetry, ProjectYTelemetry.Endpoint,
  adapter: Bandit.PhoenixAdapter,
  pubsub_server: ProjectYTelemetry.PubSub,
  server: true,
  http: [ip: {0, 0, 0, 0}, port: 4000],
  check_origin: ["//localhost:3001", "//localhost:4000"],
  secret_key_base: String.duplicate("test-only-", 8)

config :opentelemetry, resource: [service: [name: "telemetry"]]
config :opentelemetry, span_processor: :batch, traces_exporter: :otlp

if config_env() == :test do
  config :projecty_telemetry, dependencies: false
  config :projecty_telemetry, ProjectYTelemetry.Endpoint, server: false
  config :opentelemetry, traces_exporter: :none
end
