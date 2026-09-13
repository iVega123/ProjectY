namespace RentalOperations.Services;

/// <summary>
/// Publica no Kafka o que a transação do aluguel deixou no outbox.
///
/// Marcar como publicado continua vindo depois do envio, e a ordem é essa de
/// propósito: uma queda entre os dois republica o mesmo evento, e o id do evento
/// -- derivado do aluguel e do assunto -- faz o inbox do outro lado reconhecê-lo.
/// O contrário (marcar antes) perderia o evento em silêncio, que é a falha que o
/// outbox existe para impedir.
///
/// O que mudou no #70 é quem pode enviar: a linha é reivindicada antes, por
/// <see cref="RentalOutboxDispatcher"/>, e é isso que deixa duas réplicas rodarem
/// sem publicar tudo duas vezes. O laço segue enquanto a passada reivindicar alguma
/// linha e só espera quando esvazia ou quando o broker falha: publicar o evento de
/// uma moto é o que libera o seguinte dela, então uma passada parcial também deixa
/// trabalho.
/// </summary>
public sealed partial class RentalKafkaRelay(
    RentalOutboxDispatcher dispatcher,
    IConfiguration config,
    ILogger<RentalKafkaRelay> log) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrap)) return;
        using var transport = new KafkaRentalEventTransport(
            bootstrap,
            config["Kafka:SchemaRegistryUrl"] ?? throw new InvalidOperationException("Kafka schema registry URL is required."),
            Path.Combine(AppContext.BaseDirectory, "event-contracts"));

        while (!stoppingToken.IsCancellationRequested)
        {
            var drained = true;
            try
            {
                var pass = await dispatcher.DispatchOnceAsync(transport, stoppingToken);
                if (pass.Failure is not null)
                    LogRelayDelayed(log, pass.Failure);
                // Per replica, so a two-replica run can show that both drained and neither repeated.
                if (pass.Published > 0)
                    LogPublished(log, pass.Published);
                drained = pass.Failure is not null || pass.Claimed == 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { LogRelayDelayed(log, error); }
            if (drained) await Task.Delay(PollInterval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka relay delayed; rental events retained")]
    private static partial void LogRelayDelayed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka relay published {PublishedCount} rental events")]
    private static partial void LogPublished(ILogger logger, int publishedCount);
}
