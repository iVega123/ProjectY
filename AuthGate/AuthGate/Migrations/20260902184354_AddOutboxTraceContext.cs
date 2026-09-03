using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthGate.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxTraceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "OutboxMessages",
                type: "character varying(55)",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceState",
                table: "OutboxMessages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "TraceState",
                table: "OutboxMessages");
        }
    }
}
