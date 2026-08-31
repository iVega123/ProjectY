using Microsoft.EntityFrameworkCore;
using MotoHub.Models;
using ProjectY.Shared.Messaging;

namespace MotoHub.Data
{
    public class ApplicationDbContext : DbContext, IApplicationDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Motorcycle> Motorcycles { get; set; }
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Motorcycle>()
                .HasIndex(m => m.LicensePlate)
                .IsUnique();
            modelBuilder.ConfigureOutbox();
        }
    }
}
