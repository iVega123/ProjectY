using ProjectY.Shared.Pagination;
using RentalOperations.Model;

namespace RentalOperations.Repository;

/// <summary>
/// O que o serviço de aluguéis pede ao banco.
///
/// Encolheu ao sair do MongoDB, e o que sumiu é a parte interessante: não há
/// mais TryClaimRentalAsync, TryClaimRetirementAsync, ReleaseRentalClaimAsync
/// nem TryReserveLicensePlateRenameAsync. Aquilo era um protocolo de reserva
/// entre dois bancos, escrito para conseguir exclusão mútua sem transação. Com
/// motos e aluguéis na mesma transação, a exclusão é o índice único parcial
/// one_active_rental_per_motorcycle e a chave estrangeira -- o banco recusa, e
/// não há estado intermediário para alguém precisar reconciliar depois.
/// </summary>
public interface IRentalRepository
{
    Task<Rental> CreateRentalAsync(Rental rental, CancellationToken token = default);
    Task<Rental?> GetRentalByIdAsync(string id, CancellationToken token = default);
    Task<CursorPage<Rental>> GetRentalsByUserId(string userId, string? cursor, int? pageSize, CancellationToken token = default);
    Task<bool> HasOverlappingRentalAsync(Guid motorcycleId, DateTime startDate, DateTime endDate, CancellationToken token = default);
    Task<bool> IsMotorcycleCurrentlyRentedAsync(string licencePlate, CancellationToken token = default);
    Task UpdateRentalAsync(Rental rental, CancellationToken token = default);
}
