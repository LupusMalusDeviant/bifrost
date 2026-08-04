using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AuditIntegrityChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChainId",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ChainIndex",
                table: "AuditEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryHash",
                table: "AuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousHash",
                table: "AuditEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ChainId_ChainIndex",
                table: "AuditEvents",
                columns: new[] { "ChainId", "ChainIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_ChainId_ChainIndex",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ChainId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ChainIndex",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "EntryHash",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "PreviousHash",
                table: "AuditEvents");
        }
    }
}
