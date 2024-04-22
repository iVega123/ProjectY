using Microsoft.EntityFrameworkCore;
using RiderManager.Data;
using RiderManager.Models;

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

        public async Task<List<Rider>> GetAllAsync()
        {
            return await _context.Riders.ToListAsync();
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
