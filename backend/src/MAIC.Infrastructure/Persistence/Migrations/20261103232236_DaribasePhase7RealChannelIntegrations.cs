using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DaribasePhase7RealChannelIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelIntegrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "DISCONNECTED"),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ExternalAccountId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    SecondaryAccountId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AccessToken = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, defaultValue: ""),
                    AppSecret = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WebhookVerifyToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VerifiedMetadataJson = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    LastTestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastWebhookReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelIntegrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelIntegrations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelIntegrations_WorkspaceId_Provider",
                table: "ChannelIntegrations",
                columns: new[] { "WorkspaceId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelIntegrations");
        }
    }
}
