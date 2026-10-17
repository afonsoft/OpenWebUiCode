using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AutomationActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionJson",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionKind",
                table: "Automations",
                type: "TEXT",
                nullable: false,
                defaultValue: "prompt");

            migrationBuilder.AddColumn<string>(
                name: "ChatId",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionMode",
                table: "Automations",
                type: "TEXT",
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "ActionKind",
                table: "AutomationRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "prompt");

            migrationBuilder.AddColumn<string>(
                name: "Output",
                table: "AutomationRuns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActionJson",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "ActionKind",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "PermissionMode",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "ActionKind",
                table: "AutomationRuns");

            migrationBuilder.DropColumn(
                name: "Output",
                table: "AutomationRuns");
        }
    }
}
