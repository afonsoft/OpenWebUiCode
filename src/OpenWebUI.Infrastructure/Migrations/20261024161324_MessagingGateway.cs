using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenWebUI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MessagingGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GatewayChannels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", nullable: false),
                    SecretRef = table.Column<string>(type: "TEXT", nullable: false),
                    WebhookSecret = table.Column<string>(type: "TEXT", nullable: false),
                    ToolsPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultModelId = table.Column<string>(type: "TEXT", nullable: true),
                    PersonaId = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayDeliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    PeerId = table.Column<string>(type: "TEXT", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: true),
                    RunId = table.Column<string>(type: "TEXT", nullable: true),
                    Excerpt = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewayPeers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalChatId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalName = table.Column<string>(type: "TEXT", nullable: true),
                    PairingCode = table.Column<string>(type: "TEXT", nullable: true),
                    PairingExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PairingAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    PairedUserId = table.Column<string>(type: "TEXT", nullable: true),
                    PairedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Blocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayPeers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatewaySessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastInboundAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewaySessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayChannels_OwnerUserId_Platform",
                table: "GatewayChannels",
                columns: new[] { "OwnerUserId", "Platform" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayDeliveries_ChannelId_CreatedAt",
                table: "GatewayDeliveries",
                columns: new[] { "ChannelId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayPeers_ChannelId_ExternalChatId_ExternalUserId",
                table: "GatewayPeers",
                columns: new[] { "ChannelId", "ExternalChatId", "ExternalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GatewayPeers_PairedUserId",
                table: "GatewayPeers",
                column: "PairedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GatewaySessions_ChatId",
                table: "GatewaySessions",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_GatewaySessions_PeerId",
                table: "GatewaySessions",
                column: "PeerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayChannels");

            migrationBuilder.DropTable(
                name: "GatewayDeliveries");

            migrationBuilder.DropTable(
                name: "GatewayPeers");

            migrationBuilder.DropTable(
                name: "GatewaySessions");
        }
    }
}
