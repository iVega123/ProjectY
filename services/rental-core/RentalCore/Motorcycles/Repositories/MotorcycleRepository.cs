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
            var decoded = CursorPagination.Decode(cursor);
            Guid? afterId = decoded is null
                ? null
                : Guid.TryParse(decoded, out var parsed)
                    ? parsed
                    : throw new FormatException("The pagination cursor is invalid.");
            var source = afterId is null
                ? _context.Motorcycles
                : _context.Motorcycles.FromSqlInterpolated($"""
                    SELECT * FROM motorcycles
                    WHERE retired_at IS NULL AND id > {afterId}
                    """);
            var query = source
                .AsNoTracking()
                .Where(motorcycle => motorcycle.RetiredAtUtc == null)
                .OrderBy(motorcycle => motorcycle.Id)
                .AsQueryable();

            var fetched = await query.Take(size + 1).ToListAsync();
            return CursorPagination.CreatePage(fetched, size, motorcycle => motorcycle.Id.ToString());
        }

        public Motorcycle? GetById(Guid id)
        {
            return _context.Motorcycles.Find(id);
        }

        public async Task<Motorcycle?> GetByIdAsync(Guid id)
        {
            return await _context.Motorcycles.FindAsync(id);
        }

        /// <summary>
        /// As motos pedidas, numa consulta.
        ///
        /// Aposentada continua respondendo, ao contrário da paginação: um
        /// aluguel antigo aponta para uma moto que saiu da frota, e omiti-la
        /// deixaria a linha do histórico sem modelo nem placa. Listar a frota e
        /// resolver uma referência são perguntas diferentes.
        /// </summary>
        public async Task<IReadOnlyList<Motorcycle>> GetByIdsAsync(IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0)
            {
                return [];
            }
            return await _context.Motorcycles
                .AsNoTracking()
                .Where(motorcycle => ids.Contains(motorcycle.Id))
                .ToListAsync();
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
