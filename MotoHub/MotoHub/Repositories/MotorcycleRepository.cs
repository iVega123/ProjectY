using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;

using ProjectY.Shared.Pagination;

namespace MotoHub.Repositories
{
    public class MotorcycleRepository : IMotorcycleRepository
    {
        private readonly IApplicationDbContext _context;

        public MotorcycleRepository(IApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<CursorPage<Motorcycle>> GetPageAsync(string? cursor, int? pageSize)
        {
            var size = CursorPagination.NormalizePageSize(pageSize);
            var afterId = CursorPagination.Decode(cursor);
            var query = _context.Motorcycles
                .AsNoTracking()
                .Where(motorcycle => motorcycle.RetiredAtUtc == null)
                .OrderBy(motorcycle => motorcycle.Id)
                .AsQueryable();
            if (afterId is not null)
            {
                query = query.Where(motorcycle => string.Compare(motorcycle.Id, afterId) > 0);
            }

            var fetched = await query.Take(size + 1).ToListAsync();
            return CursorPagination.CreatePage(fetched, size, motorcycle => motorcycle.Id);
        }

        public Motorcycle? GetById(string id)
        {
            return _context.Motorcycles.Find(id);
        }

        public void Add(Motorcycle motorcycle)
        {
            _context.Motorcycles.Add(motorcycle);
            _context.SaveChanges();
        }

        public void Update(Motorcycle motorcycle)
        {
            _context.Entry(motorcycle).State = EntityState.Modified;
            _context.SaveChanges();
        }

        public async Task<bool> RetireAsync(string id, DateTime retiredAtUtc, string reason)
        {
            var motorcycle = await _context.Motorcycles
                .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.RetiredAtUtc == null);
            if (motorcycle is null)
            {
                return false;
            }

            motorcycle.RetiredAtUtc = retiredAtUtc;
            motorcycle.RetirementReason = reason;
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task EnsureHistoricalReferenceAsync(string licensePlate, DateTime retiredAtUtc)
        {
            if (await _context.Motorcycles.AnyAsync(motorcycle => motorcycle.LicensePlate == licensePlate))
            {
                return;
            }

            var placeholder = new Motorcycle
            {
                LicensePlate = licensePlate,
                Model = "Legacy motorcycle (metadata unavailable)",
                Year = 0,
                RegistrationDate = retiredAtUtc,
                RetiredAtUtc = retiredAtUtc,
                RetirementReason = MotorcycleRetirementReasons.LegacyOrphanBackfill
            };

            _context.Motorcycles.Add(placeholder);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
                when (_context.Motorcycles.Any(motorcycle => motorcycle.LicensePlate == licensePlate))
            {
                _context.Entry(placeholder).State = EntityState.Detached;
            }
        }

        public bool LicensePlateExists(string licensePlate)
        {
            return _context.Motorcycles.Any(m => m.LicensePlate == licensePlate);
        }

        public async Task<Motorcycle?> GetByLicensePlateAsync(string licensePlate)
        {
            return await _context.Motorcycles.FirstOrDefaultAsync(m => m.LicensePlate == licensePlate);
        }
    }
}
