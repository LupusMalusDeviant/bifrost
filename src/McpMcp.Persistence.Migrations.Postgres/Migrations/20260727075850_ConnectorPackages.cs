using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ConnectorPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TrustLevel",
                table: "PublisherKeys",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "ConnectorPackages",
                columns: table => new
                {
                    PackageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Transport = table.Column<int>(type: "integer", nullable: false),
                    PublisherKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TrustLevel = table.Column<int>(type: "integer", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Directory = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    InstalledAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    ActivatedAtTicks = table.Column<long>(type: "bigint", nullable: true),
                    GrantedCapabilities = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorPackages", x => new { x.PackageId, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorPackages_PackageId_State",
                table: "ConnectorPackages",
                columns: new[] { "PackageId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorPackages");

            migrationBuilder.DropColumn(
                name: "TrustLevel",
                table: "PublisherKeys");
        }
    }
}
