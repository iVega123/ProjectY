using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using RiderManager.Models;

namespace RiderManager.Data
{
    public interface IApplicationDbContext
    {
        DbSet<Rider> Riders { get; set; }
        int SaveChanges();
        EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
    }
}
