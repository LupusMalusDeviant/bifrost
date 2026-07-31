using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Postgres.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CallerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallerDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Tool = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RedactedArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTools",
                columns: table => new
                {
                    Tool = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
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
