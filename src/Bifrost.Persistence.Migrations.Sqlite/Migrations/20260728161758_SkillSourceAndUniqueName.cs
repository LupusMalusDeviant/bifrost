using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SkillSourceAndUniqueName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourcePackageId",
                table: "Assets",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePackageVersion",
                table: "Assets",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Name_Version",
                table: "Assets",
                columns: new[] { "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assets_Name_Version",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "SourcePackageId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "SourcePackageVersion",
                table: "Assets");
        }
    }
}
