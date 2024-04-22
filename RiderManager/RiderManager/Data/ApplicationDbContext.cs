using Microsoft.EntityFrameworkCore;
using RiderManager.Models;

namespace RiderManager.Data
{
    public class ApplicationDbContext : DbContext, IApplicationDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
            Database.EnsureCreated();
        }

        public DbSet<Rider> Riders { get; set; }
        public DbSet<PresignedUrl> PresignedUrls { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureRiderEntity(modelBuilder);
            ConfigurePresignedUrlEntity(modelBuilder);
        }

        private void ConfigureRiderEntity(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Rider>(entity =>
            {
                entity.HasIndex(r => r.CNPJ).IsUnique();
                entity.HasIndex(r => r.CNHNumber).IsUnique();

                entity.HasOne(r => r.CNHUrl)
                      .WithOne(p => p.Rider)
                      .HasForeignKey<PresignedUrl>(p => p.RiderId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private void ConfigurePresignedUrlEntity(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PresignedUrl>(entity =>
            {
                entity.HasIndex(p => p.ObjectName).IsUnique();
            });
        }
    }
}
