defmodule ProjectYTelemetry.Application do
  use Application

  def start(_type, _args) do
    dependencies =
      if Application.get_env(:projecty_telemetry, :dependencies, true) do
        [
          {Redix,
           {System.get_env("REDIS_URL", "redis://redis:6379"), [name: ProjectYTelemetry.Redis]}},
          {Xandra,
           [
             nodes: [System.get_env("CASSANDRA_HOST", "cassandra")],
             name: ProjectYTelemetry.Cassandra
           ]},
          ProjectYTelemetry.Consumer
        ]
      else
        []
      end

    Supervisor.start_link(
      [
        {Phoenix.PubSub, name: ProjectYTelemetry.PubSub},
        {Registry, keys: :unique, name: ProjectYTelemetry.Registry},
        {DynamicSupervisor, strategy: :one_for_one, name: ProjectYTelemetry.Rentals},
        ProjectYTelemetry.Presence,
        ProjectYTelemetry.Endpoint
      ] ++ dependencies,
      strategy: :one_for_one,
      name: ProjectYTelemetry.Supervisor
    )
  end
end
