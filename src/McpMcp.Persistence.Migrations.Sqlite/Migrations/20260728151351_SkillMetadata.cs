using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SkillMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "References",
                table: "Assets",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredTools",
                table: "Assets",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhenToUse",
                table: "Assets",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "References",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "RequiredTools",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "WhenToUse",
                table: "Assets");
        }
    }
}
