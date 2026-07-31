using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Postgres.Migrations
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedactedResponseJson",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RedactionRules",
                columns: table => new
                {
                    Tool = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Patterns = table.Column<string>(type: "text", nullable: false)
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
