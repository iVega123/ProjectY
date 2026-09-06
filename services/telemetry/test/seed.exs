event = %ProjectY.Events.RentalEvent{
  event_id: "smoke-start-" <> Integer.to_string(System.system_time(:millisecond)),
  rental_id: "integration-rental", rider_id: "integration-rider",
  motorcycle_id: "integration-moto", occurred_at_ms: System.system_time(:millisecond)
}
:ok = :brod.produce_sync(:tracking_kafka, "rental.started", 0, "integration-moto", ProjectY.Events.RentalEvent.encode(event))
