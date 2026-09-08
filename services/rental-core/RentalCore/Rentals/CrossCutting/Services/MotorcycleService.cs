using RentalOperations.CrossCutting.Model;

namespace RentalOperations.CrossCutting.Services
{
    /// <summary>
    /// A metade de aluguéis perguntando à metade de motos -- por chamada de
    /// método.
    ///
    /// Isto era um HttpClient apontando para o próprio processo: os dois
    /// serviços viraram um em #134, mas continuaram conversando pela rede
    /// porque colapsar a chamada era um passo separado. É este passo. O que
    /// desaparece junto é tudo que existia por causa da rede -- o timeout de um
    /// segundo, a propagação de identidade para si mesmo, e a tradução de
    /// falhas HTTP em exceções que ninguém sabia interpretar.
    ///
    /// A interface fica. Ela é a costura entre os dois domínios, e continuará
    /// sendo quando um deles sair de novo.
    /// </summary>
    public sealed class MotorcycleService(MotoHub.Services.IMotorcycleService motorcycles)
        : IMotorcycleService
    {
        public async Task<Motorcycle?> GetMotorcycleByIdAsync(string licensePlate)
        {
            var found = await motorcycles.GetMotorcycleByLicensePlateAsync(licensePlate);
            return found is null
                ? null
                : new Motorcycle
                {
                    id = found.Id ?? string.Empty,
                    year = found.Year,
                    model = found.Model ?? string.Empty,
                    licensePlate = found.LicensePlate,
                    retiredAtUtc = found.RetiredAtUtc,
                    retirementReason = found.RetirementReason
                };
        }
    }
}
