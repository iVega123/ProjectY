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
            var query = _context.Riders
                .AsNoTracking()
                .Include(rider => rider.CNHUrl)
                .OrderBy(rider => rider.Id)
                .AsQueryable();
            if (afterId is not null)
            {
                query = query.Where(rider => string.Compare(rider.Id, afterId) > 0);
            }

            var fetched = await query.Take(size + 1).ToListAsync();
            return CursorPagination.CreatePage(fetched, size, rider => rider.Id);
        }

        public async Task AddAsync(Rider rider)
        {
            rider.Id = Guid.NewGuid().ToString();
            _context.Riders.Add(rider);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Rider rider)
        {
            _context.Entry(rider).State = EntityState.Modified;
            await _context.SaveChangesAsync();
        }

        public async Task DeleteAsync(string id)
        {
            var rider = await _context.Riders.FindAsync(id);
            if (rider != null)
            {
                _context.Riders.Remove(rider);
                await _context.SaveChangesAsync();
            }
        }
    }
}
