using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace McpMcp.Persistence.Migrations.Sqlite.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ServerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<int>(type: "INTEGER", nullable: true),
                    InputFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RedactedInputJson = table.Column<string>(type: "TEXT", nullable: true),
                    RedactedResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ExpectedInputSchemaJson = table.Column<string>(type: "TEXT", nullable: true),
                    Cancellation = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimedAtTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "INTEGER", nullable: false)
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
