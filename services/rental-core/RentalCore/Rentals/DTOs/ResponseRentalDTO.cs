namespace RentalOperations.DTOs
{
    public class ResponseRentalDTO
    {
        public string RentalId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// A referência estável, e a chave pela qual o BFF compõe a tela.
        /// </summary>
        public string MotorcycleId { get; set; } = string.Empty;

        /// <summary>
        /// A placa continua na resposta porque uma tela precisa mostrar algo que
        /// um humano reconheça. Ela vem da junção com <c>motorcycles</c>, não de
        /// uma cópia guardada no aluguel: corrigir a placa corrige o histórico
        /// inteiro, em vez de bifurcá-lo entre aluguéis antigos e novos.
        /// </summary>
        public string MotorcycleLicencePlate { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime PredictedEndDate { get; set; }
        public DateTime? ActualEndDate { get; set; }
        public decimal OriginalTotalCost { get; set; }
        public decimal FinalTotalCost { get; set; }
        public decimal AdditionalCostsOrSavings { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

}
