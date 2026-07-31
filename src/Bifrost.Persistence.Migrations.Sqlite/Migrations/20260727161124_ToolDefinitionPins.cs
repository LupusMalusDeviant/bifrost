using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
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
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AcceptedHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AcceptedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PendingSinceTicks = table.Column<long>(type: "INTEGER", nullable: true)
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
