defmodule ProjectYTelemetry.GlobalMetrics do
  # The three numbers on the console's panel are global: rental p99, messages
  # ready in RabbitMQ, gateway 429s. Nothing in them depends on who is asking,
  # so they are measured once per interval for the whole cluster and pushed on
  # metrics:global, instead of every signed-in tab asking every ten seconds
  # (#194).
  #
  # Every node runs one of these, and only the leader queries Prometheus: the
  # smallest node name among the connected ones. N replicas therefore cost one
  # query per metric per interval, and distributed PubSub carries the broadcast
  # to clients on every node. A partition gives each side its own leader, so
  # both query -- twice the queries, never a missing tick.
  #
  # Every node also listens on the topic and keeps the last snapshot, so a
  # client that joins on a follower gets numbers at once instead of after the
  # next tick.
  use GenServer
  alias Phoenix.PubSub
  alias ProjectYTelemetry.Endpoint

  @topic "metrics:global"
  @interval 10_000
  # The series the rental creation SLO dashboard reads.
  @queries [
    p99:
      ~s|histogram_quantile(0.99, sum by (le) (rate(traces_span_metrics_duration_milliseconds_bucket{service_name="rental-core",span_name=~"POST /?api/Rental/create"}[5m])))|,
    queue: "sum(rabbitmq_detailed_queue_messages_ready)",
    limited:
      ~s|sum(increase(traces_span_metrics_calls_total{service_name="api-gateway",http_response_status_code="429"}[5m]))|
  ]

  def queries, do: @queries

  def start_link(opts),
    do: GenServer.start_link(__MODULE__, opts, name: Keyword.get(opts, :name, __MODULE__))

  def latest(server \\ __MODULE__), do: GenServer.call(server, :latest)

  # The smallest name wins, so every node reaches the same answer from the same
  # membership without asking the others.
  def leader?, do: Enum.min([node() | Node.list()]) == node()

  @impl true
  def init(opts) do
    :ok = PubSub.subscribe(ProjectYTelemetry.PubSub, @topic)
    interval = Keyword.get(opts, :interval, @interval)
    # :manual is for tests, which send :measure themselves instead of waiting.
    if is_integer(interval), do: {:ok, _} = :timer.send_interval(interval, :measure)
    {:ok, %{query: Keyword.get(opts, :query, {__MODULE__, :prometheus, []}), latest: nil}}
  end

  @impl true
  def handle_call(:latest, _from, state), do: {:reply, state.latest, state}

  @impl true
  def handle_info(:measure, state) do
    if leader?() do
      snapshot = measure(state.query)
      Endpoint.broadcast!(@topic, "metrics", snapshot)
      {:noreply, %{state | latest: snapshot}}
    else
      {:noreply, state}
    end
  end

  def handle_info(%Phoenix.Socket.Broadcast{event: "metrics", payload: snapshot}, state),
    do: {:noreply, %{state | latest: snapshot}}

  defp measure({module, function, args}) do
    measured_at = DateTime.utc_now() |> DateTime.truncate(:millisecond) |> DateTime.to_iso8601()

    @queries
    |> Task.async_stream(
      fn {name, promql} -> {name, apply(module, function, [promql | args])} end,
      timeout: 5_000,
      on_timeout: :kill_task
    )
    |> Enum.zip_with(@queries, fn
      {:ok, measured}, _ -> measured
      {:exit, _}, {name, _} -> {name, nil}
    end)
    |> Map.new()
    |> Map.put(:measuredAt, measured_at)
  end

  # One instant query. A failure is nil: the panel shows that number as
  # unavailable, never as zero, and the other two still arrive.
  def prometheus(promql) do
    url =
      System.get_env("PROMETHEUS_URL", "http://prometheus:9090") <>
        "/api/v1/query?" <> URI.encode_query(query: promql)

    with {:ok, {{_, 200, _}, _, body}} <-
           :httpc.request(
             :get,
             {String.to_charlist(url), []},
             [timeout: 4_000, connect_timeout: 2_000],
             body_format: :binary
           ),
         {:ok, %{"data" => %{"result" => [%{"value" => [_, raw]} | _]}}} when is_binary(raw) <-
           Jason.decode(body),
         {value, ""} <- Float.parse(raw) do
      value
    else
      _ -> nil
    end
  end
end
