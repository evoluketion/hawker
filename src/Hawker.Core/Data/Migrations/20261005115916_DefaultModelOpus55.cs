using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DefaultModelOpus55 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "ClaudeModel",
                value: "claude-opus-5-5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "ClaudeModel",
                value: "claude-sonnet-5-5");
        }
    }
}
