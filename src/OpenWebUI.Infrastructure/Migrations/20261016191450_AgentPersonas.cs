using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentPersonas",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SystemPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    GuardrailLevel = table.Column<string>(type: "TEXT", nullable: false),
                    ToolsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPersonas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentPersonaSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PersonaId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatKey = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPersonaSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentTurns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PersonaId = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    ChatKey = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    InText = table.Column<string>(type: "TEXT", nullable: false),
                    OutText = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTurns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPersonas_UserId_Name",
                table: "AgentPersonas",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPersonaSessions_PersonaId_ChatKey",
                table: "AgentPersonaSessions",
                columns: new[] { "PersonaId", "ChatKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurns_PersonaId_Timestamp",
                table: "AgentTurns",
                columns: new[] { "PersonaId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurns_RunId",
                table: "AgentTurns",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPersonas");

            migrationBuilder.DropTable(
                name: "AgentPersonaSessions");

            migrationBuilder.DropTable(
                name: "AgentTurns");
        }
    }
}
