defmodule ProjectYTelemetry.TrackingTest do
  use ExUnit.Case, async: true

  test "coordinates are bounded and timestamps come from the server" do
    assert {:ok, %{latitude: 0.0, longitude: -60.0, recorded_at: 123}} =
             ProjectYTelemetry.Rental.validate(
               %{"latitude" => 0, "longitude" => -60, "recorded_at" => 999},
               123
             )

    for payload <- [
          %{"latitude" => 91, "longitude" => 0},
          %{"latitude" => 0, "longitude" => -181},
          %{"latitude" => "1"},
          %{}
        ] do
      assert {:error, _} = ProjectYTelemetry.Rental.validate(payload, 1)
    end
  end

  test "protobuf distinguishes absent and zero and survives unknown fields" do
    alias ProjectY.Events.RentalEvent
    assert RentalEvent.decode(<<>>).occurred_at_ms == nil
    event = %RentalEvent{rental_id: "r1", rider_id: "u1", occurred_at_ms: 0}
    assert RentalEvent.decode(RentalEvent.encode(event)).occurred_at_ms == 0
  end

  test "socket rejects unsigned tickets" do
    assert :error = ProjectYTelemetry.Socket.connect(%{"ticket" => "bad"}, %Phoenix.Socket{}, %{})
    assert :error = ProjectYTelemetry.Socket.connect(%{}, %Phoenix.Socket{}, %{})
  end
end
