using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditDetailsAndRedactionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallerRoles",
                table: "AuditEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "AuditEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedactedResponseJson",
                table: "AuditEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RedactionRules",
                columns: table => new
                {
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Patterns = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RedactionRules", x => x.Tool);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RedactionRules");

            migrationBuilder.DropColumn(
                name: "CallerRoles",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "RedactedResponseJson",
                table: "AuditEvents");
        }
    }
}
