# Generated from contracts/events/rental.proto. Regenerate with protoc-gen-elixir.
defmodule ProjectY.Events.RentalEvent do
  use Protobuf, syntax: :proto3
  field(:event_id, 1, proto3_optional: true, type: :string)
  field(:rental_id, 2, proto3_optional: true, type: :string)
  field(:rider_id, 3, proto3_optional: true, type: :string)
  field(:motorcycle_id, 4, proto3_optional: true, type: :string)
  field(:occurred_at_ms, 5, proto3_optional: true, type: :int64)
  field(:plan_days, 6, proto3_optional: true, type: :int32)
  field(:started_at_ms, 7, proto3_optional: true, type: :int64)
  field(:predicted_end_at_ms, 8, proto3_optional: true, type: :int64)
  field(:ended_at_ms, 9, proto3_optional: true, type: :int64)
end
