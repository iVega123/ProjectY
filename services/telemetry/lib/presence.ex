defmodule ProjectYTelemetry.Presence do
  use Phoenix.Presence, otp_app: :projecty_telemetry, pubsub_server: ProjectYTelemetry.PubSub
end
