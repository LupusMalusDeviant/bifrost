using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bifrost.Persistence.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class UpstreamOAuthTokenBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "UpstreamOAuthTokens",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                table: "UpstreamOAuthTokens",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Revision",
                table: "UpstreamOAuthTokens",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "UpstreamOAuthTokens");

            migrationBuilder.DropColumn(
                name: "Resource",
                table: "UpstreamOAuthTokens");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "UpstreamOAuthTokens");
        }
    }
}
