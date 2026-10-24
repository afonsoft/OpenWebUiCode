using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Instincts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instincts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: true),
                    Domain = table.Column<string>(type: "TEXT", nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    HitCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRefs = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instincts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instincts_UserId_Scope_WorkspaceId",
                table: "Instincts",
                columns: new[] { "UserId", "Scope", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Instincts_UserId_Status",
                table: "Instincts",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instincts");
        }
    }
}
