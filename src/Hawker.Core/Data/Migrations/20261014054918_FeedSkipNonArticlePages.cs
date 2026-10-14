using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeedSkipNonArticlePages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipNonArticlePages",
                table: "Feeds",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipNonArticlePages",
                table: "Feeds");
        }
    }
}
