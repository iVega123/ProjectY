defmodule ProjectYTelemetry.RentalLifetimeTest do
  use ExUnit.Case, async: false
  alias ProjectYTelemetry.{Rental, Store}

  test "accepted positions renew lifetime, rejected writes do not, and stale timers are harmless" do
    start_supervised!(
      {Redix,
       {System.get_env("TEST_REDIS_URL", "redis://localhost:6379"),
        [name: ProjectYTelemetry.Redis]}}
    )

    id = "idle-test-#{System.unique_integer([:positive])}"
    rider = id <> "-rider"
    :ok = Store.activate(id, rider)
    {:ok, pid} = Rental.resume(id, rider)

    try do
      original = :sys.get_state(pid).idle_timer
      assert {:ok, position} = Rental.position(id, %{"latitude" => 0, "longitude" => -60})
      renewed = :sys.get_state(pid).idle_timer
      assert renewed != original
      assert Process.read_timer(original) == false
      assert Process.read_timer(renewed) > 86_399_000
      send(pid, {:timeout, original, :idle})
      assert Rental.last(id) == position
      assert {:error, _} = Rental.position(id, %{"latitude" => 91, "longitude" => 0})
      assert :sys.get_state(pid).idle_timer == renewed
      monitor = Process.monitor(pid)
      send(pid, {:timeout, renewed, :idle})
      assert_receive {:DOWN, ^monitor, :process, ^pid, :normal}
    after
      Rental.close(id)

      Redix.command!(
        ProjectYTelemetry.Redis,
        ["DEL", "tracking:last:" <> id, "tracking:rate:" <> rider]
      )
    end
  end
end
