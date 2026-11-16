using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AutomationObservability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "AutomationHookDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationHookDeliveries_HookId_IdempotencyKey",
                table: "AutomationHookDeliveries",
                columns: new[] { "HookId", "IdempotencyKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AutomationHookDeliveries_HookId_IdempotencyKey",
                table: "AutomationHookDeliveries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "AutomationHookDeliveries");
        }
    }
}
