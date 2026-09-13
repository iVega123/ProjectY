defmodule ProjectYTelemetry.Consumer do
  use GenServer
  require Logger
  def start_link(args), do: GenServer.start_link(__MODULE__, args, name: __MODULE__)

  def init(_) do
    Process.flag(:trap_exit, true)
    send(self(), :connect)
    {:ok, nil}
  end

  def handle_info(:connect, _) do
    host = System.get_env("KAFKA_HOST", "kafka") |> String.to_charlist()
    # The chaos overlay reaches the broker through its proxied listener on 9094.
    port = System.get_env("KAFKA_PORT", "9092") |> String.to_integer()

    case :brod.start_client([{host, port}], :tracking_kafka, auto_start_producers: true) do
      :ok ->
        subscribe()

      {:error, {:already_started, _}} ->
        subscribe()

      _ ->
        Process.send_after(self(), :connect, 5000)
        {:noreply, nil}
    end
  end

  def handle_info({:EXIT, _, _}, _) do
    Process.send_after(self(), :connect, 5000)
    {:noreply, nil}
  end

  defp subscribe do
    case :brod.start_link_group_subscriber_v2(%{
           client: :tracking_kafka,
           group_id: "telemetry-v1",
           topics: ["rental.started", "rental.closed"],
           cb_module: ProjectYTelemetry.Events,
           message_type: :message,
           consumer_config: [begin_offset: :earliest]
         }) do
      {:ok, pid} ->
        {:noreply, pid}

      _ ->
        Process.send_after(self(), :connect, 5000)
        {:noreply, nil}
    end
  end
end

defmodule ProjectYTelemetry.Events do
  require Record
  require OpenTelemetry.Tracer, as: Tracer

  Record.defrecordp(
    :kafka_message,
    Record.extract(:kafka_message, from_lib: "kafka_protocol/include/kpro_public.hrl")
  )

  def init(info, _), do: {:ok, info}

  def handle_message(message, state) do
    headers = kafka_message(message, :headers)
    :otel_propagator_text_map.extract(headers)

    Tracer.with_span "kafka tracking event", %{
      kind: :consumer,
      attributes: %{"messaging.system" => "kafka", "messaging.destination.name" => state.topic}
    } do
      event = ProjectY.Events.RentalEvent.decode(kafka_message(message, :value))

      if event.rental_id in [nil, ""] or event.rider_id in [nil, ""] or
           is_nil(event.occurred_at_ms),
         do: raise("invalid rental event")

      # Redis compares event time atomically, making duplicate and cross-topic
      # out-of-order delivery harmless. Kafka commits only after durable state.
      :ok = ProjectYTelemetry.Store.apply_event(event, state.topic)
      {:ok, :commit, state}
    end
  end
end
