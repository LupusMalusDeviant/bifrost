using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalDefaultEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalPolicySettings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Queue")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalPolicySettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalPolicySettings");
        }
    }
}
