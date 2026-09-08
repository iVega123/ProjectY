using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using MotoHub.Models;

namespace MotoHub.Data
{
    public interface IApplicationDbContext
    {
        DbSet<Motorcycle> Motorcycles { get; set; }
        int SaveChanges();
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    }
}
