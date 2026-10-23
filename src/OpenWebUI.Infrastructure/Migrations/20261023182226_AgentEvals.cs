using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentEvals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEphemeral",
                table: "Chats",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "EvalBaselines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    EvalRunId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalBaselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalDatasets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    TagsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalDatasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalResults",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EvalRunId = table.Column<string>(type: "TEXT", nullable: false),
                    CaseId = table.Column<string>(type: "TEXT", nullable: false),
                    Rep = table.Column<int>(type: "INTEGER", nullable: false),
                    ChatRunId = table.Column<string>(type: "TEXT", nullable: true),
                    ChatId = table.Column<string>(type: "TEXT", nullable: true),
                    Pass = table.Column<bool>(type: "INTEGER", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    GraderDetailJson = table.Column<string>(type: "TEXT", nullable: true),
                    FinalAnswer = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentName = table.Column<string>(type: "TEXT", nullable: true),
                    ToolsetJson = table.Column<string>(type: "TEXT", nullable: true),
                    K = table.Column<int>(type: "INTEGER", nullable: false),
                    Concurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    TotalJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    DoneJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvalCases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DatasetId = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Expected = table.Column<string>(type: "TEXT", nullable: true),
                    Grader = table.Column<string>(type: "TEXT", nullable: false),
                    Rubric = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectToolsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ForbidToolsJson = table.Column<string>(type: "TEXT", nullable: true),
                    MaxSteps = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EvalDatasetId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvalCases_EvalDatasets_EvalDatasetId",
                        column: x => x.EvalDatasetId,
                        principalTable: "EvalDatasets",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvalBaselines_UserId_Name",
                table: "EvalBaselines",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvalCases_DatasetId",
                table: "EvalCases",
                column: "DatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalCases_EvalDatasetId",
                table: "EvalCases",
                column: "EvalDatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalDatasets_UserId_CreatedAt",
                table: "EvalDatasets",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvalResults_EvalRunId_CaseId",
                table: "EvalResults",
                columns: new[] { "EvalRunId", "CaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_Status",
                table: "EvalRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_UserId_StartedAt",
                table: "EvalRuns",
                columns: new[] { "UserId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvalBaselines");

            migrationBuilder.DropTable(
                name: "EvalCases");

            migrationBuilder.DropTable(
                name: "EvalResults");

            migrationBuilder.DropTable(
                name: "EvalRuns");

            migrationBuilder.DropTable(
                name: "EvalDatasets");

            migrationBuilder.DropColumn(
                name: "IsEphemeral",
                table: "Chats");
        }
    }
}
