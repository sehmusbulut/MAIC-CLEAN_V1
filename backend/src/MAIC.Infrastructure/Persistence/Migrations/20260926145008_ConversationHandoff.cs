using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Conversations",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4)",
                oldMaxLength: 4);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedUserId",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastAssignmentSequence",
                table: "Conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastHandoffSequence",
                table: "Conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_WorkspaceMembers_WorkspaceId_UserId",
                table: "WorkspaceMembers",
                columns: new[] { "WorkspaceId", "UserId" });

            migrationBuilder.CreateTable(
                name: "ConversationAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    PreviousAssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationAssignments", x => x.Id);
                    table.CheckConstraint("CK_ConversationAssignments_Changed", "\"PreviousAssignedUserId\" IS DISTINCT FROM \"AssignedUserId\"");
                    table.CheckConstraint("CK_ConversationAssignments_Reason", "\"Reason\" IN ('MANUAL', 'HANDOFF_ACCEPTED', 'MEMBER_REMOVED')");
                    table.CheckConstraint("CK_ConversationAssignments_ReasonTarget", "(\"Reason\" <> 'HANDOFF_ACCEPTED' OR (\"AssignedUserId\" IS NOT NULL AND \"AssignedUserId\" = \"ActorUserId\"))\nAND (\"Reason\" <> 'MEMBER_REMOVED' OR \"AssignedUserId\" IS NULL)");
                    table.CheckConstraint("CK_ConversationAssignments_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Conversations_WorkspaceId_Conversat~",
                        columns: x => new { x.WorkspaceId, x.ConversationId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Users_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationAssignments_Users_PreviousAssignedUserId",
                        column: x => x.PreviousAssignedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HandoffRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TerminalActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TerminalAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalReason = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HandoffRequests", x => x.Id);
                    table.CheckConstraint("CK_HandoffRequests_Lifecycle", "(\"Status\" = 'PENDING' AND \"TerminalActorUserId\" IS NULL AND \"TerminalAt\" IS NULL AND \"TerminalReason\" IS NULL)\nOR (\"TerminalActorUserId\" IS NOT NULL AND \"TerminalAt\" IS NOT NULL AND \"TerminalAt\" >= \"CreatedAt\"\n    AND \"TerminalReason\" IS NOT NULL\n    AND ((\"Status\" = 'ACCEPTED' AND \"TerminalReason\" = 'ACCEPTED')\n        OR (\"Status\" = 'CANCELLED' AND \"TerminalReason\" IN ('MANUAL_ASSIGNMENT', 'RESOLVED'))))");
                    table.CheckConstraint("CK_HandoffRequests_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_HandoffRequests_Sequence", "\"Sequence\" > 0");
                    table.ForeignKey(
                        name: "FK_HandoffRequests_Conversations_WorkspaceId_ConversationId",
                        columns: x => new { x.WorkspaceId, x.ConversationId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HandoffRequests_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HandoffRequests_Users_TerminalActorUserId",
                        column: x => x.TerminalActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_AssignedUserId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "AssignedUserId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations",
                sql: "\"Status\" IN ('OPEN', 'RESOLVED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_WorkflowSequences",
                table: "Conversations",
                sql: "\"LastAssignmentSequence\" >= 0 AND \"LastHandoffSequence\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_ActorUserId",
                table: "ConversationAssignments",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_AssignedUserId",
                table: "ConversationAssignments",
                column: "AssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_PreviousAssignedUserId",
                table: "ConversationAssignments",
                column: "PreviousAssignedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationAssignments_WorkspaceId_ConversationId_Sequence",
                table: "ConversationAssignments",
                columns: new[] { "WorkspaceId", "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_RequesterUserId",
                table: "HandoffRequests",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_TerminalActorUserId",
                table: "HandoffRequests",
                column: "TerminalActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_WorkspaceId_ConversationId",
                table: "HandoffRequests",
                columns: new[] { "WorkspaceId", "ConversationId" },
                unique: true,
                filter: "\"Status\" = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_WorkspaceId_ConversationId_RequesterUserId_~",
                table: "HandoffRequests",
                columns: new[] { "WorkspaceId", "ConversationId", "RequesterUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_WorkspaceId_ConversationId_Sequence",
                table: "HandoffRequests",
                columns: new[] { "WorkspaceId", "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_WorkspaceMembers_WorkspaceId_AssignedUserId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "AssignedUserId" },
                principalTable: "WorkspaceMembers",
                principalColumns: new[] { "WorkspaceId", "UserId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_WorkspaceMembers_WorkspaceId_AssignedUserId",
                table: "Conversations");

            migrationBuilder.DropTable(
                name: "ConversationAssignments");

            migrationBuilder.DropTable(
                name: "HandoffRequests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_WorkspaceMembers_WorkspaceId_UserId",
                table: "WorkspaceMembers");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_AssignedUserId",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_WorkflowSequences",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastAssignmentSequence",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastHandoffSequence",
                table: "Conversations");

            // The previous schema supports OPEN only. Keep conversations/messages during downgrade.
            migrationBuilder.Sql("UPDATE \"Conversations\" SET \"Status\" = 'OPEN' WHERE \"Status\" = 'RESOLVED'");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Conversations",
                type: "character varying(4)",
                maxLength: 4,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations",
                sql: "\"Status\" = 'OPEN'");
        }
    }
}
