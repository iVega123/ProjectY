using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AuthGate.Model;
using ProjectY.Shared.Messaging;

namespace AuthGate.Data
{
    public class ApplicationDbContext : IdentityDbContext<IdentityUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<ApplicationUser>()
                .Property(e => e.UserType)
                .HasConversion<string>();

            builder.Entity<RiderUser>()
                .HasIndex(u => u.CNPJ)
                .IsUnique();

            builder.Entity<RiderUser>()
                .HasIndex(u => u.CNHNumber)
                .IsUnique();

            SeedRoles(builder);
            builder.ConfigureOutbox();
        }
        private static void SeedRoles(ModelBuilder builder)
        {
            builder.Entity<IdentityRole>().HasData(
                new IdentityRole
                {
                    Id = "00000000-0000-0000-0000-000000000001",
                    Name = "Admin",
                    ConcurrencyStamp = "1",
                    NormalizedName = "ADMIN"
                },
                new IdentityRole
                {
                    Id = "00000000-0000-0000-0000-000000000002",
                    Name = "Rider",
                    ConcurrencyStamp = "2",
                    NormalizedName = "RIDER"
                }
            );
        }
    }
}
