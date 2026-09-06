defmodule ProjectYTelemetry.Store do
  require Logger
  require OpenTelemetry.Tracer, as: Tracer
  @redis ProjectYTelemetry.Redis
  @ttl 7_776_000
  # Shared across rentals and replicas: one row/second bounds each rider/day partition.
  def reserve_position(rider) do
    case Redix.command(@redis, ["SET", "tracking:rate:" <> rider, "1", "NX", "PX", "1000"]) do
      {:ok, "OK"} -> :ok
      _ -> {:error, :rate_limited}
    end
  end

  def apply_event(event, topic) do
    script = """
    local prior = tonumber(redis.call('GET', KEYS[1]) or '-1')
    if tonumber(ARGV[1]) < prior then return 0 end
    if tonumber(ARGV[1]) == prior and ARGV[4] ~= 'rental.closed' then return 0 end
    redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
    if ARGV[4] == 'rental.closed' then redis.call('DEL', KEYS[2])
    else redis.call('SET', KEYS[2], ARGV[2], 'EX', ARGV[3]) end
    return 1
    """

    case Redix.command(@redis, [
           "EVAL",
           script,
           "2",
           "tracking:version:" <> event.rental_id,
           "tracking:active:" <> event.rental_id,
           event.occurred_at_ms,
           event.rider_id,
           @ttl,
           topic
         ]) do
      {:ok, _} -> :ok
      _ -> raise "tracking event persistence failed"
    end
  end

  def activate(id, rider) do
    case Redix.command(@redis, ["SET", "tracking:active:" <> id, rider, "EX", @ttl]) do
      {:ok, "OK"} -> :ok
      _ -> {:error, :redis}
    end
  end

  def close(id), do: Redix.command(@redis, ["DEL", "tracking:active:" <> id])

  def owner(id) do
    case Redix.command(@redis, ["GET", "tracking:active:" <> id]) do
      {:ok, rider} -> rider
      _ -> nil
    end
  end

  def last(id) do
    case Redix.command(@redis, ["GET", "tracking:last:" <> id]) do
      {:ok, bytes} when is_binary(bytes) -> Jason.decode!(bytes)
      _ -> nil
    end
  end

  def put(id, rider, p) do
    case Redix.command(@redis, ["SET", "tracking:last:" <> id, Jason.encode!(p), "EX", @ttl]) do
      {:ok, "OK"} ->
        history(id, rider, p)
        :ok

      _ ->
        {:error, :redis}
    end
  end

  defp history(id, rider, p) do
    Tracer.with_span "cassandra.position", %{attributes: %{"db.system.name" => "cassandra"}} do
      day = DateTime.from_unix!(p.recorded_at, :millisecond) |> DateTime.to_date()

      statement =
        "INSERT INTO projecty.rider_positions (rider_id, day, recorded_at, latitude, longitude, rental_id) VALUES (?, ?, ?, ?, ?, ?) USING TTL #{@ttl}"

      with {:ok, query} <-
             Xandra.prepare(ProjectYTelemetry.Cassandra, statement, timeout: 500),
           {:ok, _} <-
             Xandra.execute(
               ProjectYTelemetry.Cassandra,
               query,
               [
                 rider,
                 day,
                 p.recorded_at,
                 p.latitude,
                 p.longitude,
                 id
               ],
               timeout: 500
             ) do
        :ok
      else
        _ -> Logger.warning("tracking history unavailable; live position retained in Redis")
      end
    end
  catch
    :exit, _ ->
      Logger.warning("tracking history connection unavailable; live position retained in Redis")
      :ok
  end
end
