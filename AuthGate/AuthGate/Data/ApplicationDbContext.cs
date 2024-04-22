using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AuthGate.Model;

namespace AuthGate.Data
{
    public class ApplicationDbContext : IdentityDbContext<IdentityUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
            Database.EnsureCreated();
        }
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
        }
        private static void SeedRoles(ModelBuilder builder)
        {
            builder.Entity<IdentityRole>().HasData(
                new IdentityRole { Name = "Admin", ConcurrencyStamp = "1", NormalizedName = "ADMIN" },
                new IdentityRole { Name = "Rider", ConcurrencyStamp = "2", NormalizedName = "RIDER" }
            );
        }
    }
}
