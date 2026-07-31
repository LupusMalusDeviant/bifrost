using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalEnforcementMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "ApprovalTools",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Queue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "ApprovalTools");
        }
    }
}
