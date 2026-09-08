namespace RentalOperations.DTOs
{
    /// <summary>
    /// A moto é pedida pelo id, não pela placa.
    ///
    /// A placa muda: o schema alvo tem um UPDATE para corrigi-la, e enquanto ela
    /// era a referência esse UPDATE precisava reescrever todo aluguel que a
    /// tivesse copiado. Pior, um cliente que guardasse a placa de ontem passava
    /// a pedir uma moto que não existe mais, e recebia "não existe" em vez de
    /// "mudou de nome". O id não muda, então nenhuma das duas coisas acontece.
    /// </summary>
    public class RentalCreateDto
    {
        public Guid MotorcycleId { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime PredictedEndDate { get; set; }
    }
}
