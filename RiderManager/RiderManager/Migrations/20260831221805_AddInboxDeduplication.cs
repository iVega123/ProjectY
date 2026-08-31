using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiderManager.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxDeduplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxImageParts",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    SequenceNumber = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    EndOfFile = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxImageParts", x => new { x.UserId, x.FileName, x.SequenceNumber });
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    ConsumerName = table.Column<string>(type: "text", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.ConsumerName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Riders_UserId",
                table: "Riders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxImageParts_ReceivedAtUtc",
                table: "InboxImageParts",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ProcessedAtUtc",
                table: "InboxMessages",
                column: "ProcessedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxImageParts");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_Riders_UserId",
                table: "Riders");
        }
    }
}
