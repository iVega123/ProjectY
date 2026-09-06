defmodule ProjectYTelemetry.Router do
  use Plug.Router
  plug(:match)
  plug(:dispatch)
  get("/health/live", do: send_resp(conn, 200, "ok"))
  get("/health/startup", do: send_resp(conn, 200, "ok"))

  get "/health/ready" do
    # Cassandra history is optional; Redis owns resumable active-rental state.
    status =
      case Redix.command(ProjectYTelemetry.Redis, ["PING"], timeout: 1000) do
        {:ok, "PONG"} -> 200
        _ -> 503
      end

    send_resp(conn, status, "readiness")
  end

  match(_, do: send_resp(conn, 404, "not found"))
end
