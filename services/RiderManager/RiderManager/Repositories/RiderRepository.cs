using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Models;

using ProjectY.Shared.Pagination;

namespace RiderManager.Repositories
{
    public class RiderRepository : IRiderRepository
    {
        private readonly ApplicationDbContext _context;

        public RiderRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<Rider> GetByIdAsync(string id)
        {
            return await _context.Riders.FindAsync(id);
        }

        public async Task<Rider> GetByUserIdAsync(string userId)
        {
            return await _context.Riders.FirstOrDefaultAsync(r => r.UserId == userId);
        }

        public async Task<CursorPage<Rider>> GetPageAsync(string? cursor, int? pageSize)
        {
            var size = CursorPagination.NormalizePageSize(pageSize);
            var afterId = CursorPagination.Decode(cursor);
            var source = afterId is null
                ? _context.Riders
                : _context.Riders.FromSqlInterpolated($$"""
                    SELECT * FROM "Riders"
                    WHERE "Id" > {{afterId}}
                    """);
            var query = source
                .AsNoTracking()
                .Include(rider => rider.CNHUrl)
                .OrderBy(rider => rider.Id)
                .AsQueryable();

            var fetched = await query.Take(size + 1).ToListAsync();
            return CursorPagination.CreatePage(fetched, size, rider => rider.Id);
        }

        public async Task AddAsync(Rider rider)
        {
            rider.Id = Guid.NewGuid().ToString();
            _context.Riders.Add(rider);
            AddVerificationEvents(rider);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Rider rider)
        {
            _context.Entry(rider).State = EntityState.Modified;
            AddVerificationEvents(rider);
            await _context.SaveChangesAsync();
        }

        // The outbox rows join the rider in one SaveChanges, so a rider that exists
        // always has its fact queued. Both topics are written during the rollout:
        // v1 for consumers still on it, v2 for those that need the carried name.
        private void AddVerificationEvents(Rider rider)
        {
            _context.EventOutbox.Add(RiderEventEnvelope.VerifiedLegacy(rider));
            _context.EventOutbox.Add(RiderEventEnvelope.Verified(rider));
        }

        // A revogação entra no mesmo SaveChanges da remoção: ou o piloto some e o
        // fato sai, ou nenhum dos dois acontece. Enfileirar depois abriria uma
        // janela em que a linha já não existe e a projeção ainda diz que existe --
        // e é exatamente nessa janela que alguém alugaria.
        public async Task DeleteAsync(string id)
        {
            var rider = await _context.Riders.FindAsync(id);
            if (rider != null)
            {
                _context.EventOutbox.Add(RiderEventEnvelope.RevokedLegacy(rider));
                _context.EventOutbox.Add(RiderEventEnvelope.Revoked(rider));
                _context.Riders.Remove(rider);
                await _context.SaveChangesAsync();
            }
        }
    }
}
