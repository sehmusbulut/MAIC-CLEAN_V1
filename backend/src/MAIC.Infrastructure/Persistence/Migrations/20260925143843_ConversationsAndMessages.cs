using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationsAndMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Channels_WorkspaceId_Id",
                table: "Channels");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Channels_WorkspaceId_Id",
                table: "Channels",
                columns: new[] { "WorkspaceId", "Id" });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.UniqueConstraint("AK_Conversations_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_Conversations_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Conversations_Sequence", "\"LastMessageSequence\" >= 0");
                    table.CheckConstraint("CK_Conversations_Status", "\"Status\" = 'OPEN'");
                    table.ForeignKey(
                        name: "FK_Conversations_Channels_WorkspaceId_ChannelId",
                        columns: x => new { x.WorkspaceId, x.ChannelId },
                        principalTable: "Channels",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Contacts_WorkspaceId_ContactId",
                        columns: x => new { x.WorkspaceId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false, collation: "C"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.CheckConstraint("CK_Messages_Content", "length(\"Content\") + length(regexp_replace(\"Content\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) <= 4000\nAND \"Content\" !~ U&'[\\0001-\\0008\\000B\\000C\\000E-\\001F\\007F-\\009F]'\nAND \"Content\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'");
                    table.CheckConstraint("CK_Messages_RequestKey", "\"ClientMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Messages_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_WorkspaceId_ConversationId",
                        columns: x => new { x.WorkspaceId, x.ConversationId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Messages_Users_SenderUserId",
                        column: x => x.SenderUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedByUserId",
                table: "Conversations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_ChannelId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_ContactId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_CreatedByUserId_ClientRequestId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "CreatedByUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_SenderUserId",
                table: "Messages",
                column: "SenderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_SenderUserId_ClientMess~",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "SenderUserId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_Sequence",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Channels_WorkspaceId_Id",
                table: "Channels");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_WorkspaceId_Id",
                table: "Channels",
                columns: new[] { "WorkspaceId", "Id" });
        }
    }
}
