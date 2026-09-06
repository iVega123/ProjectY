defmodule ProjectY.Events.RentalEvent do
  @moduledoc false

  use Protobuf,
    full_name: "project_y.events.RentalEvent",
    protoc_gen_elixir_version: "0.17.0",
    syntax: :proto3

  field(:event_id, 1, proto3_optional: true, type: :string, json_name: "eventId")
  field(:rental_id, 2, proto3_optional: true, type: :string, json_name: "rentalId")
  field(:rider_id, 3, proto3_optional: true, type: :string, json_name: "riderId")
  field(:motorcycle_id, 4, proto3_optional: true, type: :string, json_name: "motorcycleId")
  field(:occurred_at_ms, 5, proto3_optional: true, type: :int64, json_name: "occurredAtMs")
  field(:plan_days, 6, proto3_optional: true, type: :int32, json_name: "planDays")
  field(:started_at_ms, 7, proto3_optional: true, type: :int64, json_name: "startedAtMs")

  field(:predicted_end_at_ms, 8,
    proto3_optional: true,
    type: :int64,
    json_name: "predictedEndAtMs"
  )

  field(:ended_at_ms, 9, proto3_optional: true, type: :int64, json_name: "endedAtMs")
end
