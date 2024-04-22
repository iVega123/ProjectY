using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AuthGate.Migrations
{
    /// <inheritdoc />
    public partial class AddMoreLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "58b203f6-1beb-4647-b7cf-040d012861a0");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "f034fb12-d12e-4fde-9f0c-0cbd07042014");

            migrationBuilder.DropColumn(
                name: "ImagemCNH",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "CNPJ",
                table: "AspNetUsers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(14)",
                oldMaxLength: 14,
                oldNullable: true);

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { "06ee79b8-d618-44d6-96ce-95263fea0173", "1", "Admin", "ADMIN" },
                    { "689fe083-2735-4518-a9c0-ffe5e57f08d5", "2", "Rider", "RIDER" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "06ee79b8-d618-44d6-96ce-95263fea0173");

            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: "689fe083-2735-4518-a9c0-ffe5e57f08d5");

            migrationBuilder.AlterColumn<string>(
                name: "CNPJ",
                table: "AspNetUsers",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImagemCNH",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { "58b203f6-1beb-4647-b7cf-040d012861a0", "2", "Rider", "RIDER" },
                    { "f034fb12-d12e-4fde-9f0c-0cbd07042014", "1", "Admin", "ADMIN" }
                });
        }
    }
}
