namespace RentalOperations.Model;

/// <summary>
/// Um aluguel como o banco o guarda.
///
/// O documento do MongoDB virou linha: o identificador é o UUID que a tabela
/// gera, e a moto é referenciada pelo id dela, não pela placa. A placa continua
/// aqui porque a API a devolve, mas é resultado de junção -- lê-se de
/// motorcycles a cada consulta, então uma correção de placa aparece no
/// histórico inteiro sem que nada precise reescrever os aluguéis.
/// </summary>
public sealed class Rental
{
    public Guid Id { get; set; }
    public required Guid MotorcycleId { get; set; }
    public required string UserId { get; set; }
    public string? RiderName { get; set; }

    /// <summary>Vem da junção com motorcycles; não existe como coluna em rentals.</summary>
    public string MotorcycleLicencePlate { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime PredictedEndDate { get; set; }
    public decimal InitCost { get; set; }
    public decimal FinalCost { get; set; }
    public decimal AdditionalCostsOrSavings { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public RentalStatus Status { get; set; } = RentalStatus.Active;
}
