using RentalOperations.Services;

namespace RentalOperations.Model;

/// <summary>Onde a moto está para um aluguel novo.</summary>
public enum MotorcycleAvailability
{
    Missing,
    Retired,
    Available
}

/// <summary>
/// O que a criação de um aluguel confere antes de escrever, lido de uma vez.
///
/// A projeção do piloto, a moto e a agenda voltam juntas porque cada leitura
/// separada pagava de novo a latência do banco (#206). Nada aqui decide a
/// corrida: dois pedidos simultâneos podem ler o mesmo "livre", e quem recusa o
/// segundo continua sendo a escrita.
/// </summary>
public sealed record RentalPreconditions(
    RiderView? Rider,
    MotorcycleAvailability Motorcycle,
    bool Overlaps);
