using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunUsageLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BudgetPolicies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    UsdPerRun = table.Column<double>(type: "REAL", nullable: true),
                    UsdPerMonth = table.Column<double>(type: "REAL", nullable: true),
                    TokensPerMonth = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxModelTier = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunUsageRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentRunId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    Steps = table.Column<int>(type: "INTEGER", nullable: false),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReasoningTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostUsd = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunUsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetPolicies_Subject",
                table: "BudgetPolicies",
                column: "Subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunUsageRecords_ParentRunId",
                table: "RunUsageRecords",
                column: "ParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_RunUsageRecords_RunId",
                table: "RunUsageRecords",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunUsageRecords_UserId_CreatedAt",
                table: "RunUsageRecords",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BudgetPolicies");

            migrationBuilder.DropTable(
                name: "RunUsageRecords");
        }
    }
}
