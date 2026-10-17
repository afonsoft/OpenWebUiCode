using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunRecoveryEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "HeartbeatAt",
                table: "ChatRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerInstance",
                table: "ChatRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatRunEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatRunEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatRunEvents_RunId_Seq",
                table: "ChatRunEvents",
                columns: new[] { "RunId", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatRunEvents");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                table: "ChatRuns");

            migrationBuilder.DropColumn(
                name: "OwnerInstance",
                table: "ChatRuns");
        }
    }
}
