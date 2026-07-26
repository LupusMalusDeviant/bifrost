using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddTasksAndAuditCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Tool = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Origin = table.Column<int>(type: "integer", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<int>(type: "integer", nullable: true),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RedactedInputJson = table.Column<string>(type: "text", nullable: true),
                    RedactedResultJson = table.Column<string>(type: "text", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpectedInputSchemaJson = table.Column<string>(type: "text", nullable: true),
                    Cancellation = table.Column<int>(type: "integer", nullable: false),
                    ClaimedAtTicks = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CreatedAtTicks",
                table: "Tasks",
                column: "CreatedAtTicks");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_OwnerId_Tool_InputFingerprint_State",
                table: "Tasks",
                columns: new[] { "OwnerId", "Tool", "InputFingerprint", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_State_ExpiresAtTicks",
                table: "Tasks",
                columns: new[] { "State", "ExpiresAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AuditEvents");
        }
    }
}
