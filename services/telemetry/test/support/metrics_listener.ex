defmodule ProjectYTelemetry.MetricsListener do
  # A subscriber to metrics:global on whichever node starts it, relaying every
  # broadcast to a listener that may live on another node. It subscribes the way
  # a channel process does. The cluster test uses it on the peer instead of a
  # ChannelTest client because ChannelTest supervises channels from the test
  # process, and the test process lives on one node only.
  def start(listener) do
    pid =
      spawn(fn ->
        :ok = ProjectYTelemetry.Endpoint.subscribe("metrics:global")
        send(listener, {:subscribed, node()})
        relay(listener)
      end)

    {:ok, pid}
  end

  defp relay(listener) do
    receive do
      %Phoenix.Socket.Broadcast{event: event, payload: payload} ->
        send(listener, {:pushed, node(), event, payload})
    end

    relay(listener)
  end
end
