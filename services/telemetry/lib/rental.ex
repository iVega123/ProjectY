defmodule ProjectYTelemetry.Rental do
  use GenServer, restart: :temporary
  require OpenTelemetry.Tracer, as: Tracer
  alias ProjectYTelemetry.Store
  @max_age 86_400_000
  def start_link({id, rider}), do: GenServer.start_link(__MODULE__, {id, rider}, name: via(id))
  defp via(id), do: {:via, Registry, {ProjectYTelemetry.Registry, id}}

  def open(id, rider) do
    with :ok <- Store.activate(id, rider), do: ensure(id, rider)
  end

  def resume(id, rider) do
    if Store.owner(id) == rider, do: ensure(id, rider), else: {:error, :not_active}
  end

  defp ensure(id, rider) do
    case DynamicSupervisor.start_child(ProjectYTelemetry.Rentals, {__MODULE__, {id, rider}}) do
      {:error, {:already_started, pid}} -> {:ok, pid}
      result -> result
    end
  end

  def close(id) do
    Store.close(id)

    case Registry.lookup(ProjectYTelemetry.Registry, id) do
      [{pid, _}] -> DynamicSupervisor.terminate_child(ProjectYTelemetry.Rentals, pid)
      [] -> :ok
    end
  end

  def last(id), do: GenServer.call(via(id), :last)
  def position(id, payload), do: GenServer.call(via(id), {:position, payload})

  def init({id, rider}) do
    Process.send_after(self(), :idle, @max_age)
    {:ok, %{id: id, rider: rider, last: Store.last(id), accepted_at: 0}}
  end

  def handle_info(:idle, state), do: {:stop, :normal, state}
  def handle_call(:last, _, state), do: {:reply, state.last, state}

  def handle_call({:position, payload}, _, state) do
    now = System.system_time(:millisecond)

    with true <- Store.owner(state.id) == state.rider,
         true <- now - state.accepted_at >= 1000,
         {:ok, position} <- validate(payload, now),
         :ok <- Store.put(state.id, state.rider, position) do
      Tracer.with_span "tracking.position", %{attributes: %{"messaging.system" => "phoenix"}} do
        {:reply, {:ok, position}, %{state | last: position, accepted_at: now}}
      end
    else
      _ -> {:reply, {:error, "invalid position, inactive rental or rate exceeded"}, state}
    end
  end

  def validate(%{"latitude" => lat, "longitude" => lon}, now)
      when is_number(lat) and is_number(lon) and lat >= -90 and lat <= 90 and lon >= -180 and
             lon <= 180 do
    {:ok, %{latitude: lat * 1.0, longitude: lon * 1.0, recorded_at: now}}
  end

  def validate(_, _), do: {:error, :invalid_coordinates}
end
