using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RiderManager.Data;

namespace RiderManager.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260905120000_DiscardPersistedDocumentUrls")]
public sealed class DiscardPersistedDocumentUrls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            UPDATE "PresignedUrls" SET "Url" = NULL, "Expiry" = TIMESTAMPTZ '1970-01-01 00:00:00+00';
            """);

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
