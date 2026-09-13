defmodule ProjectYTelemetry.GlobalMetricsTest do
  # #194 on one node: N connected clients cost one Prometheus query per metric
  # per interval, not N, and the topic they share tracks no Presence.
  use ExUnit.Case, async: false
  import Phoenix.ChannelTest
  alias ProjectYTelemetry.{FakePrometheus, GlobalMetrics, MetricsChannel, Presence, Socket}
  @endpoint ProjectYTelemetry.Endpoint
  @ticket_key String.duplicate("k", 32)

  setup do
    start_supervised!(
      {GlobalMetrics, interval: :manual, query: {FakePrometheus, :query, [self()]}}
    )

    :ok
  end

  test "a thousand connected clients cost one query per metric per interval" do
    for rider <- 1..1000 do
      {:ok, _, _} = join(client("rider-#{rider}"), MetricsChannel, "metrics:global")
    end

    send(GlobalMetrics, :measure)

    for _ <- 1..1000, do: assert_push("metrics", %{p99: 1.0, queue: 1.0, limited: 1.0})
    refute_push("metrics", _)
    assert Enum.sort(queries()) == Enum.sort(Keyword.values(GlobalMetrics.queries()))
  end

  test "a client that joins gets the last numbers at once instead of after a tick" do
    assert {:ok, %{metrics: nil}, _} = join(client("early"), MetricsChannel, "metrics:global")

    send(GlobalMetrics, :measure)
    assert_push("metrics", measured)

    assert {:ok, %{metrics: ^measured}, _} =
             join(client("late"), MetricsChannel, "metrics:global")
  end

  # Presence would replicate this topic's membership across nodes for tens of
  # thousands of members whose identity nobody reads. See MetricsChannel.
  test "the global topic tracks no Presence" do
    {:ok, _, _} = join(client("watcher"), MetricsChannel, "metrics:global")
    assert Presence.list("metrics:global") == %{}
  end

  describe "a ticket without a rental" do
    setup do
      previous = System.get_env("TELEMETRY_TICKET_KEY")
      System.put_env("TELEMETRY_TICKET_KEY", @ticket_key)

      on_exit(fn ->
        if previous,
          do: System.put_env("TELEMETRY_TICKET_KEY", previous),
          else: System.delete_env("TELEMETRY_TICKET_KEY")
      end)
    end

    # Signed the way the console's metricsTicket() signs it.
    test "opens metrics:global and no rental channel" do
      payload =
        %{rider_id: "rider-1", exp: System.system_time(:second) + 120}
        |> Jason.encode!()
        |> Base.url_encode64(padding: false)

      mac = Base.url_encode64(:crypto.mac(:hmac, :sha256, @ticket_key, payload), padding: false)

      assert {:ok, socket} = connect(Socket, %{"ticket" => payload <> "." <> mac})
      assert {:ok, _, _} = join(socket, MetricsChannel, "metrics:global")

      assert {:error, %{reason: "forbidden"}} =
               join(socket, ProjectYTelemetry.Channel, "rental:rider-1")
    end
  end

  defp client(rider),
    do: socket(Socket, "rider:" <> rider, %{identity: %{rider_id: rider, rental_id: nil, exp: 0}})

  defp queries do
    receive do
      {:queried, _node, promql} -> [promql | queries()]
    after
      0 -> []
    end
  end
end
