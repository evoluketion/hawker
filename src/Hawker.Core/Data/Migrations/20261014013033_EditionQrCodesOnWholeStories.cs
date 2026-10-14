using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class EditionQrCodesOnWholeStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "QrCodesOnWholeStories",
                table: "Editions",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QrCodesOnWholeStories",
                table: "Editions");
        }
    }
}
