defmodule ProjectYTelemetry.Endpoint do
  use Phoenix.Endpoint, otp_app: :projecty_telemetry
  socket("/socket", ProjectYTelemetry.Socket, websocket: true, longpoll: false)
  plug(ProjectYTelemetry.Router)
end
