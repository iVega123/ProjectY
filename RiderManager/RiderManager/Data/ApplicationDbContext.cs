using Microsoft.EntityFrameworkCore;
using RiderManager.Models;

namespace RiderManager.Data
{
    public class ApplicationDbContext : DbContext, IApplicationDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Rider> Riders { get; set; }
        public DbSet<PresignedUrl> PresignedUrls { get; set; }
        public DbSet<InboxMessage> InboxMessages { get; set; }
        public DbSet<InboxImagePart> InboxImageParts { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureRiderEntity(modelBuilder);
            ConfigurePresignedUrlEntity(modelBuilder);
            ConfigureInboxEntities(modelBuilder);
        }

        private void ConfigureRiderEntity(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Rider>(entity =>
            {
                entity.HasIndex(r => r.CNPJ).IsUnique();
                entity.HasIndex(r => r.CNHNumber).IsUnique();
                entity.HasIndex(r => r.UserId);

                entity.HasOne(r => r.CNHUrl)
                      .WithOne(p => p.Rider)
                      .HasForeignKey<PresignedUrl>(p => p.RiderId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureInboxEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<InboxMessage>(entity =>
            {
                entity.HasKey(message => new { message.MessageId, message.ConsumerName });
                entity.HasIndex(message => message.ProcessedAtUtc);
            });

            modelBuilder.Entity<InboxImagePart>(entity =>
            {
                entity.HasKey(part => new { part.UserId, part.FileName, part.SequenceNumber });
                entity.HasIndex(part => part.ReceivedAtUtc);
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
