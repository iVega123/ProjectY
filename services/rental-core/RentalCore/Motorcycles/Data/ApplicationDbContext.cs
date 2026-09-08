using Microsoft.EntityFrameworkCore;
using MotoHub.Models;
using ProjectY.Shared.Messaging;

namespace MotoHub.Data
{
    /// <summary>
    /// O EF Core mapeando o schema alvo, não o criando.
    ///
    /// O rental-core deixou de ter migrações próprias: quem cria as tabelas é
    /// deploy/db/sql, o mesmo arquivo que o CI aplica no CockroachDB e no
    /// PostgreSQL para provar que ele é portátil. Duas ferramentas donas do
    /// mesmo schema seria uma a mais, e a que sobra é a que a garantia central
    /// do sistema -- o índice único parcial -- já mora.
    ///
    /// O que as migrações do MotoHub fechavam continua fechado: cada uma está
    /// citada pelo nome em 002_rental_core.sql, ao lado do DDL que a substitui.
    /// </summary>
    public class ApplicationDbContext : DbContext, IApplicationDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<Motorcycle> Motorcycles { get; set; }
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var motorcycle = modelBuilder.Entity<Motorcycle>();
            motorcycle.ToTable("motorcycles");
            motorcycle.HasKey(item => item.Id);
            motorcycle.Property(item => item.Id).HasColumnName("id");
            motorcycle.Property(item => item.LicensePlate).HasColumnName("license_plate");
            motorcycle.Property(item => item.Model).HasColumnName("model");
            motorcycle.Property(item => item.Year).HasColumnName("year");
            motorcycle.Property(item => item.RegistrationDate).HasColumnName("registered_at");
            motorcycle.Property(item => item.RetiredAtUtc).HasColumnName("retired_at");
            motorcycle.Property(item => item.RetirementReason).HasColumnName("retirement_reason");
            motorcycle.HasIndex(item => item.LicensePlate).IsUnique();
            modelBuilder.ConfigureOutbox();
        }
    }
}
