using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CallerDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RedactedArgumentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTools",
                columns: table => new
                {
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTools", x => x.Tool);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_CallerId_Tool_Fingerprint_State",
                table: "ApprovalRequests",
                columns: new[] { "CallerId", "Tool", "Fingerprint", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "ApprovalTools");
        }
    }
}
