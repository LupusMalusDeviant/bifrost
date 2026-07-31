using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddToolDescriptionOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ToolDescriptionOverrides",
                columns: table => new
                {
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolDescriptionOverrides", x => x.Tool);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToolDescriptionOverrides");
        }
    }
}
