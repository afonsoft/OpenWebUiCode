using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelCombos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModelCombos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    RequestsPerMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    TokensPerMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RoundRobinCursor = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelCombos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModelComboItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ComboId = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelComboItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelComboItems_ModelCombos_ComboId",
                        column: x => x.ComboId,
                        principalTable: "ModelCombos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModelComboItems_ComboId_Position",
                table: "ModelComboItems",
                columns: new[] { "ComboId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_ModelCombos_UserId_Name",
                table: "ModelCombos",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelComboItems");

            migrationBuilder.DropTable(
                name: "ModelCombos");
        }
    }
}
