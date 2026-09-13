defmodule ProjectYTelemetry.MixProject do
  use Mix.Project

  def project,
    do: [
      app: :projecty_telemetry,
      version: "0.1.0",
      elixir: "~> 1.18",
      elixirc_paths: elixirc_paths(Mix.env()),
      deps: deps()
    ]

  # :inets is the HTTP client GlobalMetrics queries Prometheus with.
  def application,
    do: [extra_applications: [:logger, :inets], mod: {ProjectYTelemetry.Application, []}]

  # test/support is compiled rather than scripted because the cluster test's
  # peer node can only run modules it loads from the code path.
  defp elixirc_paths(:test), do: ["lib", "test/support"]
  defp elixirc_paths(_), do: ["lib"]

  defp deps do
    [
      {:phoenix, "~> 1.8.0"},
      {:bandit, "~> 1.10"},
      {:dns_cluster, "~> 0.3"},
      {:jason, "~> 1.4"},
      {:xandra, "~> 0.20.0"},
      {:decimal, "~> 3.1", override: true},
      {:redix, "~> 1.5"},
      {:brod, "~> 4.5"},
      {:protobuf, "~> 0.17"},
      {:opentelemetry_api, "~> 1.5"},
      {:opentelemetry, "~> 1.7"},
      {:opentelemetry_exporter, "~> 1.10"},
      {:credo, "~> 1.7", only: [:dev, :test], runtime: false},
      {:sobelow, "~> 0.14", only: [:dev, :test], runtime: false}
    ]
  end
end
