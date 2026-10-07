using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunTrackingAndPrintRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "EditionRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PrintedAt",
                table: "EditionRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ScheduledFor",
                table: "EditionRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PrintRetryCount",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrintRetryDelaySeconds",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "AppSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "PrintRetryCount", "PrintRetryDelaySeconds" },
                values: new object[] { 0, 60 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Error",
                table: "EditionRuns");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "EditionRuns");

            migrationBuilder.DropColumn(
                name: "ScheduledFor",
                table: "EditionRuns");

            migrationBuilder.DropColumn(
                name: "PrintRetryCount",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "PrintRetryDelaySeconds",
                table: "AppSettings");
        }
    }
}
