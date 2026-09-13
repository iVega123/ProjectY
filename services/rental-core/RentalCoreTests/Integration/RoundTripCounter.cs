using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace RentalCoreTests.Integration;

/// <summary>
/// Um repasse TCP que conta idas e voltas ao banco.
///
/// Uma ida e volta é a vez em que o cliente fala depois de o servidor ter
/// respondido: é o que custa uma espera inteira quando o banco está lento. Várias
/// mensagens enviadas antes de ler a resposta -- o reset do pool, um BEGIN
/// adiado -- contam uma vez, porque esperam uma vez. Contar comandos, em vez
/// disso, erraria para os dois lados.
///
/// A resposta é marcada antes de ser repassada. Assim o cliente não tem como
/// falar de novo antes de a contagem saber que ele pode.
/// </summary>
public sealed class RoundTripCounter : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _relays = [];
    private readonly Lock _gate = new();
    private readonly string _host;
    private readonly int _port;
    private readonly Task _accepting;
    private int _roundTrips;

    public RoundTripCounter(string host, int port)
    {
        _host = host;
        _port = port;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int RoundTrips => Volatile.Read(ref _roundTrips);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            var server = new TcpClient();
            await server.ConnectAsync(_host, _port, _stop.Token);
            lock (_gate)
            {
                _relays.Add(RelayAsync(client, server));
            }
        }
    }

    private async Task RelayAsync(TcpClient client, TcpClient server)
    {
        using (client)
        using (server)
        {
            var answered = new StrongBox<bool>(true);
            await Task.WhenAny(
                PumpAsync(client.GetStream(), server.GetStream(), fromClient: true, answered),
                PumpAsync(server.GetStream(), client.GetStream(), fromClient: false, answered));
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to, bool fromClient, StrongBox<bool> answered)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, _stop.Token)) > 0)
            {
                lock (answered)
                {
                    if (!fromClient)
                    {
                        answered.Value = true;
                    }
                    else if (answered.Value)
                    {
                        answered.Value = false;
                        Interlocked.Increment(ref _roundTrips);
                    }
                }

                await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Uma ponta fechou; o repasse inteiro termina com ela.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        Task[] relays;
        lock (_gate)
        {
            relays = [.. _relays];
        }

        await Task.WhenAll(relays);
        _stop.Dispose();
    }
}
