using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HarnessSeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TeamId",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamRole",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentMode",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventsJson",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonaId",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptTemplate",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RepoSlug",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Secret",
                table: "AutomationHooks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationHookDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    HookId = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveryKey = table.Column<string>(type: "TEXT", nullable: true),
                    EventType = table.Column<string>(type: "TEXT", nullable: true),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityWarning = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationHookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TeamId = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AssigneeChatId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedByChatId = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chats_TeamId",
                table: "Chats",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationHookDeliveries_HookId_DeliveryKey",
                table: "AutomationHookDeliveries",
                columns: new[] { "HookId", "DeliveryKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationHookDeliveries_HookId_ReceivedAt",
                table: "AutomationHookDeliveries",
                columns: new[] { "HookId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamTasks_AssigneeChatId",
                table: "TeamTasks",
                column: "AssigneeChatId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamTasks_TeamId_Status",
                table: "TeamTasks",
                columns: new[] { "TeamId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationHookDeliveries");

            migrationBuilder.DropTable(
                name: "TeamTasks");

            migrationBuilder.DropIndex(
                name: "IX_Chats_TeamId",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "TeamRole",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "AgentMode",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "EventsJson",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "PersonaId",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "PromptTemplate",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "RepoSlug",
                table: "AutomationHooks");

            migrationBuilder.DropColumn(
                name: "Secret",
                table: "AutomationHooks");
        }
    }
}
