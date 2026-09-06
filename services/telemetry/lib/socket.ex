defmodule ProjectYTelemetry.Socket do
  use Phoenix.Socket
  channel("rental:*", ProjectYTelemetry.Channel)

  # The console BFF issues a short-lived HMAC ticket only after an authenticated
  # gateway rental read. Ticket identity is independent of client position data.
  def connect(%{"ticket" => ticket}, socket, _info) do
    with [payload, signature] <- String.split(ticket, "."),
         {:ok, decoded} <- Base.url_decode64(payload, padding: false),
         {:ok, mac} <- Base.url_decode64(signature, padding: false),
         expected <-
           :crypto.mac(:hmac, :sha256, System.fetch_env!("TELEMETRY_TICKET_KEY"), payload),
         true <- Plug.Crypto.secure_compare(mac, expected),
         {:ok, %{"rider_id" => rider, "rental_id" => rental, "exp" => exp}} <-
           Jason.decode(decoded),
         true <-
           is_integer(exp) and exp > System.system_time(:second) and
             exp <= System.system_time(:second) + 300 do
      {:ok, assign(socket, :identity, %{rider_id: rider, rental_id: rental, exp: exp})}
    else
      _ -> :error
    end
  end

  def connect(_, _, _), do: :error
  def id(socket), do: "rider:" <> socket.assigns.identity.rider_id
end

defmodule ProjectYTelemetry.Channel do
  use Phoenix.Channel
  alias ProjectYTelemetry.{Rental, Presence}

  def join("rental:" <> rental, _, socket) do
    identity = socket.assigns.identity

    if rental == identity.rental_id and identity.exp > System.system_time(:second) do
      case Rental.resume(rental, identity.rider_id) do
        {:ok, _} ->
          send(self(), :presence)

          Process.send_after(
            self(),
            :expire,
            max(1, identity.exp - System.system_time(:second)) * 1000
          )

          {:ok, %{position: Rental.last(rental)}, socket}

        _ ->
          {:error, %{reason: "awaiting rental.started"}}
      end
    else
      {:error, %{reason: "forbidden"}}
    end
  end

  def handle_info(:presence, socket) do
    {:ok, _} =
      Presence.track(socket, socket.assigns.identity.rider_id, %{
        online_at: System.system_time(:second)
      })

    push(socket, "presence_state", Presence.list(socket))
    {:noreply, socket}
  end

  def handle_info(:expire, socket), do: {:stop, :normal, socket}

  def handle_in("position", payload, socket) do
    result =
      if socket.assigns.identity.exp > System.system_time(:second),
        do: Rental.position(socket.assigns.identity.rental_id, payload),
        else: {:error, "ticket expired"}

    case result do
      {:ok, position} ->
        broadcast!(socket, "position", position)
        {:reply, :ok, socket}

      {:error, reason} ->
        {:reply, {:error, %{reason: reason}}, socket}
    end
  end
end
