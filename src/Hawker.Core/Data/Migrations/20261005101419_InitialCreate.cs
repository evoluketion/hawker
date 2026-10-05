using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hawker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClaudeModel = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    MissedRunGraceMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Editions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                    ArticleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SortMode = table.Column<string>(type: "TEXT", nullable: false),
                    LookbackHours = table.Column<int>(type: "INTEGER", nullable: false),
                    PaperSize = table.Column<string>(type: "TEXT", nullable: false),
                    Greyscale = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrinterName = table.Column<string>(type: "TEXT", nullable: true),
                    Copies = table.Column<int>(type: "INTEGER", nullable: false),
                    Duplex = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoPrint = table.Column<bool>(type: "INTEGER", nullable: false),
                    IssueCounter = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Editions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Feeds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxItemsPerRun = table.Column<int>(type: "INTEGER", nullable: true),
                    FetchFullText = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScoreSource = table.Column<string>(type: "TEXT", nullable: false),
                    ScoreRegex = table.Column<string>(type: "TEXT", nullable: true),
                    MinScore = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Feeds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EditionRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EditionId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    IssueNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Log = table.Column<string>(type: "TEXT", nullable: false),
                    PdfPath = table.Column<string>(type: "TEXT", nullable: true),
                    Printed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ArticleUrls = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditionRuns_Editions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "Editions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditionSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EditionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Times = table.Column<string>(type: "TEXT", nullable: false),
                    Weekdays = table.Column<string>(type: "TEXT", nullable: false),
                    DayOfMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditionSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditionSchedules_Editions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "Editions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeenArticles",
                columns: table => new
                {
                    EditionId = table.Column<int>(type: "INTEGER", nullable: false),
                    UrlHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstSeen = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeenArticles", x => new { x.EditionId, x.UrlHash });
                    table.ForeignKey(
                        name: "FK_SeenArticles_Editions_EditionId",
                        column: x => x.EditionId,
                        principalTable: "Editions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EditionFeed",
                columns: table => new
                {
                    EditionsId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditionFeed", x => new { x.EditionsId, x.FeedsId });
                    table.ForeignKey(
                        name: "FK_EditionFeed_Editions_EditionsId",
                        column: x => x.EditionsId,
                        principalTable: "Editions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EditionFeed_Feeds_FeedsId",
                        column: x => x.FeedsId,
                        principalTable: "Feeds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppSettings",
                columns: new[] { "Id", "ClaudeModel", "EncryptedApiKey", "MissedRunGraceMinutes" },
                values: new object[] { 1, "claude-sonnet-5-5", null, 120 });

            migrationBuilder.CreateIndex(
                name: "IX_EditionFeed_FeedsId",
                table: "EditionFeed",
                column: "FeedsId");

            migrationBuilder.CreateIndex(
                name: "IX_EditionRuns_EditionId_StartedAt",
                table: "EditionRuns",
                columns: new[] { "EditionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EditionSchedules_EditionId",
                table: "EditionSchedules",
                column: "EditionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "EditionFeed");

            migrationBuilder.DropTable(
                name: "EditionRuns");

            migrationBuilder.DropTable(
                name: "EditionSchedules");

            migrationBuilder.DropTable(
                name: "SeenArticles");

            migrationBuilder.DropTable(
                name: "Feeds");

            migrationBuilder.DropTable(
                name: "Editions");
        }
    }
}
