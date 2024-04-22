using Microsoft.EntityFrameworkCore;
using MotoHub.Data;
using MotoHub.Models;

namespace MotoHub.Repositories
{
    public class MotorcycleRepository : IMotorcycleRepository
    {
        private readonly IApplicationDbContext _context;

        public MotorcycleRepository(IApplicationDbContext context)
        {
            _context = context;
        }

        public IEnumerable<Motorcycle> GetAll()
        {
            return _context.Motorcycles.ToList();
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

        public void Delete(string id)
        {
            var motorcycle = _context.Motorcycles.Find(id);
            if (motorcycle == null)
            {
                return;
            }
            _context.Motorcycles.Remove(motorcycle);
            _context.SaveChanges();
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
