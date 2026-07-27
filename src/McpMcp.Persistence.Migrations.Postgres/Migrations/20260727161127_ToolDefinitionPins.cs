using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ToolDefinitionPins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolDefinitionPins",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tool = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AcceptedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    PendingHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PendingSinceTicks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolDefinitionPins", x => new { x.ServerId, x.Tool });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolDefinitionPins");
        }
    }
}
