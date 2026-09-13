defmodule ProjectYTelemetry.DegradationTest do
  # Two rows of the degradation table, against a real Redis and no Cassandra.
  use ExUnit.Case, async: false
  alias ProjectYTelemetry.{Rental, Store}

  setup do
    start_supervised!(
      {Redix,
       {System.get_env("TEST_REDIS_URL", "redis://localhost:6379"),
        [name: ProjectYTelemetry.Redis]}}
    )

    id = "degradation-#{System.unique_integer([:positive])}"
    rider = id <> "-rider"
    :ok = Store.activate(id, rider)

    on_exit(fn ->
      {:ok, redis} = Redix.start_link(System.get_env("TEST_REDIS_URL", "redis://localhost:6379"))

      Redix.command!(redis, [
        "DEL",
        "tracking:active:" <> id,
        "tracking:last:" <> id,
        "tracking:rate:" <> rider
      ])

      Redix.stop(redis)
    end)

    %{id: id, rider: rider}
  end

  # Cassandra down: trip history stops, the current position does not. No
  # Cassandra connection is started in the test environment, so every history
  # write fails. If that failure propagated, the position would be refused.
  test "a position is accepted and served while trip history is unavailable", %{
    id: id,
    rider: rider
  } do
    {:ok, _} = Rental.resume(id, rider)

    assert {:ok, position} = Rental.position(id, %{"latitude" => -23.5, "longitude" => -46.6})
    assert %{"latitude" => -23.5, "longitude" => -46.6} = Store.last(id)
    assert Rental.last(id) == position

    Rental.close(id)
  end

  # Tracking down: the live map stops, the last known position does not. A
  # rental process that dies takes its memory with it; the position it had
  # accepted comes back from Redis when the rider reconnects.
  test "the last known position survives the tracking process that accepted it", %{
    id: id,
    rider: rider
  } do
    {:ok, pid} = Rental.resume(id, rider)
    assert {:ok, _} = Rental.position(id, %{"latitude" => 1.25, "longitude" => 2.5})

    monitor = Process.monitor(pid)
    Process.exit(pid, :kill)
    assert_receive {:DOWN, ^monitor, :process, ^pid, :killed}

    {:ok, restarted} = Rental.resume(id, rider)
    assert restarted != pid
    assert %{"latitude" => 1.25, "longitude" => 2.5} = Rental.last(id)

    Rental.close(id)
  end
end
