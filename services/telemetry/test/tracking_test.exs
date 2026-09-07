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
    # Golden emitted by the .NET v1 producer; fields 10-12 are unknown to this v0 decoder.
    golden = Base.decode16!("0A016512017222016D50005A0342524C6203416461")

    assert %RentalEvent{event_id: "e", rental_id: "r", motorcycle_id: "m"} =
             RentalEvent.decode(golden)
  end

  test "socket rejects unsigned tickets" do
    assert :error = ProjectYTelemetry.Socket.connect(%{"ticket" => "bad"}, %Phoenix.Socket{}, %{})
    assert :error = ProjectYTelemetry.Socket.connect(%{}, %Phoenix.Socket{}, %{})
  end
end
