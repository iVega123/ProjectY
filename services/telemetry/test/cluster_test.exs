defmodule ProjectYTelemetry.ClusterTest do
  # #194: "telemetry running two replicas delivers a broadcast to clients on
  # both -- proven, not assumed." Two BEAM nodes with the application started on
  # each, a GlobalMetrics on each, a channel client on this node and a
  # subscriber on the other, and one tick.
  use ExUnit.Case, async: false
  import Phoenix.ChannelTest

  alias ProjectYTelemetry.{
    Endpoint,
    FakePrometheus,
    GlobalMetrics,
    MetricsChannel,
    MetricsListener,
    Socket
  }

  @endpoint ProjectYTelemetry.Endpoint

  setup_all do
    %{peer: start_peer!()}
  end

  test "one tick reaches clients on both nodes and queries Prometheus once per metric", %{
    peer: peer
  } do
    {:ok, _} = :erpc.call(peer, MetricsListener, :start, [self()])
    assert_receive {:subscribed, ^peer}
    await_cluster!(peer)

    options = [interval: :manual, query: {FakePrometheus, :query, [self()]}]
    start_supervised!({GlobalMetrics, options})

    {:ok, _} =
      :erpc.call(peer, DynamicSupervisor, :start_child, [
        ProjectYTelemetry.Rentals,
        {GlobalMetrics, options}
      ])

    {:ok, _, _} = join(client("here"), MetricsChannel, "metrics:global")

    # Both replicas tick, as their timers would. Only the leader measures.
    send(GlobalMetrics, :measure)
    send({GlobalMetrics, peer}, :measure)

    assert_push("metrics", %{p99: 1.0, queue: 1.0, limited: 1.0} = snapshot)
    assert_receive {:pushed, ^peer, "metrics", ^snapshot}
    refute_push("metrics", _)
    refute_receive {:pushed, ^peer, "metrics", _}

    leader = Enum.min([node(), peer])
    queried = queries()
    assert Enum.all?(queried, fn {asked_by, _} -> asked_by == leader end)

    assert Enum.sort(Enum.map(queried, &elem(&1, 1))) ==
             Enum.sort(Keyword.values(GlobalMetrics.queries()))

    # The follower kept the snapshot, so a client joining there gets numbers at
    # once instead of after the next tick.
    assert eventually(fn -> :erpc.call(peer, GlobalMetrics, :latest, []) == snapshot end)
  end

  defp start_peer! do
    unless Node.alive?() do
      # :peer needs this node distributed, and distribution needs epmd.
      {_, 0} = System.cmd("epmd", ["-daemon"])
      {:ok, _} = Node.start(:"primary@127.0.0.1", :longnames)
    end

    {:ok, pid, peer} =
      :peer.start(%{
        name: :"replica-#{System.unique_integer([:positive])}",
        host: ~c"127.0.0.1",
        longnames: true,
        args: [~c"-setcookie", Atom.to_charlist(Node.get_cookie())]
      })

    on_exit(fn -> :peer.stop(pid) end)
    :ok = :erpc.call(peer, :code, :add_paths, [:code.get_path()])

    for app <- [:phoenix, :opentelemetry, :projecty_telemetry] do
      :erpc.call(peer, Application, :load, [app])

      for {key, value} <- Application.get_all_env(app) do
        :ok = :erpc.call(peer, Application, :put_env, [app, key, value])
      end
    end

    {:ok, _} = :erpc.call(peer, Application, :ensure_all_started, [:projecty_telemetry])
    peer
  end

  # Connected is not yet joined: PubSub learns of the other node's server a
  # moment after the connection. Probe until a broadcast from here arrives there.
  defp await_cluster!(peer, attempts \\ 50) do
    Endpoint.broadcast!("metrics:global", "probe", %{})

    receive do
      {:pushed, ^peer, "probe", _} -> :ok
    after
      100 ->
        if attempts > 0,
          do: await_cluster!(peer, attempts - 1),
          else: flunk("the peer never received a broadcast")
    end
  end

  defp eventually(check, attempts \\ 20) do
    cond do
      check.() ->
        true

      attempts > 0 ->
        Process.sleep(50)
        eventually(check, attempts - 1)

      true ->
        false
    end
  end

  defp client(rider),
    do: socket(Socket, "rider:" <> rider, %{identity: %{rider_id: rider, rental_id: nil, exp: 0}})

  defp queries do
    receive do
      {:queried, asked_by, promql} -> [{asked_by, promql} | queries()]
    after
      0 -> []
    end
  end
end
