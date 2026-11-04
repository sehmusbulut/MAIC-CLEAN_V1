using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestWidgetSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_ChannelId",
                table: "Conversations");

            migrationBuilder.AddColumn<Guid>(
                name: "SenderGuestSessionId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "Conversations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByGuestSessionId",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GuestAccessGeneration",
                table: "Channels",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "GuestSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    ChannelGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuestSessions", x => x.Id);
                    table.UniqueConstraint("AK_GuestSessions_WorkspaceId_ChannelId_ContactId_Id", x => new { x.WorkspaceId, x.ChannelId, x.ContactId, x.Id });
                    table.UniqueConstraint("AK_GuestSessions_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_GuestSessions_Digest", "\"CredentialHash\" ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("CK_GuestSessions_Lifecycle", "\"ChannelGeneration\" >= 0 AND \"ExpiresAt\" = \"CreatedAt\" + interval '24 hours'\nAND (\"RevokedAt\" IS NULL OR \"RevokedAt\" >= \"CreatedAt\")");
                    table.ForeignKey(
                        name: "FK_GuestSessions_Channels_WorkspaceId_ChannelId",
                        columns: x => new { x.WorkspaceId, x.ChannelId },
                        principalTable: "Channels",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuestSessions_Contacts_WorkspaceId_ContactId",
                        columns: x => new { x.WorkspaceId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GuestSessions_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_SenderGuestSessionId_Cl~",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "SenderGuestSessionId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkspaceId_SenderGuestSessionId",
                table: "Messages",
                columns: new[] { "WorkspaceId", "SenderGuestSessionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages",
                sql: "(\"SenderKind\" = 'MEMBER' AND \"SenderUserId\" IS NOT NULL AND \"AiExecutionId\" IS NULL AND \"SenderGuestSessionId\" IS NULL)\nOR (\"SenderKind\" = 'AI' AND \"SenderUserId\" IS NULL AND \"AiExecutionId\" IS NOT NULL AND \"SenderGuestSessionId\" IS NULL AND \"ClientMessageId\" = \"AiExecutionId\")\nOR (\"SenderKind\" = 'GUEST' AND \"SenderUserId\" IS NULL AND \"AiExecutionId\" IS NULL AND \"SenderGuestSessionId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_ChannelId_ContactId_CreatedByGues~",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "ChannelId", "ContactId", "CreatedByGuestSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_CreatedByGuestSessionId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "CreatedByGuestSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_Id_CreatedByGuestSessionId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "Id", "CreatedByGuestSessionId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Creator",
                table: "Conversations",
                sql: "(\"CreatedByUserId\" IS NOT NULL AND \"CreatedByGuestSessionId\" IS NULL)\nOR (\"CreatedByUserId\" IS NULL AND \"CreatedByGuestSessionId\" IS NOT NULL AND \"ClientRequestId\" = \"CreatedByGuestSessionId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Channels_GuestGeneration",
                table: "Channels",
                sql: "\"GuestAccessGeneration\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_GuestSessions_CredentialHash",
                table: "GuestSessions",
                column: "CredentialHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GuestSessions_WorkspaceId_ChannelId_CreatedAt",
                table: "GuestSessions",
                columns: new[] { "WorkspaceId", "ChannelId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GuestSessions_WorkspaceId_ContactId",
                table: "GuestSessions",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_GuestSessions_WorkspaceId_ChannelId_ContactId~",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "ChannelId", "ContactId", "CreatedByGuestSessionId" },
                principalTable: "GuestSessions",
                principalColumns: new[] { "WorkspaceId", "ChannelId", "ContactId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_GuestSessions_WorkspaceId_SenderGuestSessionId",
                table: "Messages",
                columns: new[] { "WorkspaceId", "SenderGuestSessionId" },
                principalTable: "GuestSessions",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // EF alternate keys cannot contain the nullable member/guest creator discriminator.
            // PostgreSQL can reference the unique nullable index and skips this FK for MEMBER/AI.
            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Conversations_GuestOwner",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "SenderGuestSessionId" },
                principalTable: "Conversations",
                principalColumns: new[] { "WorkspaceId", "Id", "CreatedByGuestSessionId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "GuestSessions") THEN
                        RAISE EXCEPTION 'Cannot downgrade while guest session provenance or credential tombstones exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Conversations_GuestOwner",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_GuestSessions_WorkspaceId_ChannelId_ContactId~",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_GuestSessions_WorkspaceId_SenderGuestSessionId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "GuestSessions");

            migrationBuilder.DropIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_SenderGuestSessionId_Cl~",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_WorkspaceId_SenderGuestSessionId",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_ChannelId_ContactId_CreatedByGues~",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_CreatedByGuestSessionId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_Id_CreatedByGuestSessionId",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Creator",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Channels_GuestGeneration",
                table: "Channels");

            migrationBuilder.DropColumn(
                name: "SenderGuestSessionId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "CreatedByGuestSessionId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "GuestAccessGeneration",
                table: "Channels");

            migrationBuilder.AlterColumn<Guid>(
                name: "CreatedByUserId",
                table: "Conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages",
                sql: "(\"SenderKind\" = 'MEMBER' AND \"SenderUserId\" IS NOT NULL AND \"AiExecutionId\" IS NULL)\nOR (\"SenderKind\" = 'AI' AND \"SenderUserId\" IS NULL AND \"AiExecutionId\" IS NOT NULL AND \"ClientMessageId\" = \"AiExecutionId\")");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_ChannelId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "ChannelId" });
        }
    }
}
