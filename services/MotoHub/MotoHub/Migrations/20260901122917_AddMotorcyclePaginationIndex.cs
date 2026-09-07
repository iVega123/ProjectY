using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MotoHub.Migrations
{
    /// <inheritdoc />
    public partial class AddMotorcyclePaginationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Motorcycles_Active_Id",
                table: "Motorcycles",
                column: "Id",
                filter: "\"RetiredAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Motorcycles_Active_Id",
                table: "Motorcycles");
        }
    }
}
