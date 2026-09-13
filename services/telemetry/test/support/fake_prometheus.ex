defmodule ProjectYTelemetry.FakePrometheus do
  # Answers every query with 1.0 and tells the listener which node asked. It is
  # compiled from test/support rather than written in a test script because the
  # cluster test's peer node only runs modules it can load from the code path.
  def query(promql, listener) do
    send(listener, {:queried, node(), promql})
    1.0
  end
end
