defmodule ProjectYTelemetry.MixProject do
  use Mix.Project
  def project, do: [app: :projecty_telemetry, version: "0.1.0", elixir: "~> 1.18", deps: deps()]
  def application, do: [extra_applications: [:logger], mod: {ProjectYTelemetry.Application, []}]

  defp deps do
    [
      {:phoenix, "~> 1.8.0"},
      {:bandit, "~> 1.10"},
      {:jason, "~> 1.4"},
      {:xandra, "~> 0.19.4"},
      {:decimal, "~> 3.1", override: true},
      {:redix, "~> 1.5"},
      {:brod, "~> 4.5"},
      {:protobuf, "~> 0.17"},
      {:opentelemetry_api, "~> 1.5"},
      {:opentelemetry, "~> 1.7"},
      {:opentelemetry_exporter, "~> 1.10"}
    ]
  end
end
