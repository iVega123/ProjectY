defmodule ProjectYTelemetry.MetricsChannel do
  # The console's global numbers, pushed (#194). Two things the rental channel
  # does are left out on purpose.
  #
  # No Presence. It keeps each topic's membership as a CRDT replicated across
  # nodes: the right price on rental:{id}, where who is watching is the point,
  # and pure cost on a topic with tens of thousands of members nobody reads.
  # The broadcast needs no membership.
  #
  # No expiry. The ticket bounds when a connection may be opened; the connection
  # then staying open is what spares the stack a request per tab per interval.
  # A reconnect fetches a new ticket.
  use Phoenix.Channel
  alias ProjectYTelemetry.GlobalMetrics

  def join("metrics:global", _, socket),
    do: {:ok, %{metrics: GlobalMetrics.latest()}, socket}
end
