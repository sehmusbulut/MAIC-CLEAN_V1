using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationAiExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "SenderUserId",
                table: "Messages",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AiExecutionId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderKind",
                table: "Messages",
                type: "character varying(6)",
                maxLength: 6,
                nullable: false,
                defaultValue: "MEMBER");

            migrationBuilder.AddColumn<Guid>(
                name: "AiExecutionId",
                table: "HandoffRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AiAgentId",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AiBindingRevision",
                table: "Conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Messages_WorkspaceId_ConversationId_Id",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "Id" });

            migrationBuilder.CreateTable(
                name: "AiExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentSnapshot = table.Column<string>(type: "text", nullable: false),
                    ConversationRevision = table.Column<long>(type: "bigint", nullable: false),
                    MessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    AssignmentSequence = table.Column<long>(type: "bigint", nullable: false),
                    HandoffSequence = table.Column<long>(type: "bigint", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(11)", maxLength: 11, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Failure = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RetrievalOutcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RetrievalReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CitationsJson = table.Column<string>(type: "jsonb", nullable: false),
                    GenerationReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReservedCost = table.Column<decimal>(type: "numeric(20,9)", precision: 20, scale: 9, nullable: false),
                    ActualCost = table.Column<decimal>(type: "numeric(20,9)", precision: 20, scale: 9, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    ProviderAttempted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiExecutions", x => x.Id);
                    table.UniqueConstraint("AK_AiExecutions_WorkspaceId_ConversationId_Id", x => new { x.WorkspaceId, x.ConversationId, x.Id });
                    table.CheckConstraint("CK_AiExecutions_Fences", "\"ConversationRevision\" >= 0 AND \"MessageSequence\" > 0 AND \"AssignmentSequence\" >= 0 AND \"HandoffSequence\" >= 0");
                    table.CheckConstraint("CK_AiExecutions_Lifecycle", "\"ExpiresAt\" > \"CreatedAt\" AND ((\"State\" = 'IN_PROGRESS' AND \"CompletedAt\" IS NULL AND \"Outcome\" IS NULL)\nOR (\"State\" = 'TERMINAL' AND \"CompletedAt\" IS NOT NULL AND \"Outcome\" IS NOT NULL\n    AND \"Outcome\" IN ('ANSWERED','FALLBACK','HANDOFF','UNAVAILABLE','SUPERSEDED','CANCELLED')))");
                    table.CheckConstraint("CK_AiExecutions_Usage", "\"ReservedCost\" >= 0 AND (\"ActualCost\" IS NULL OR \"ActualCost\" >= 0)\nAND (\"InputTokens\" IS NULL OR \"InputTokens\" >= 0) AND (\"OutputTokens\" IS NULL OR \"OutputTokens\" >= 0)\nAND (\"GenerationReservedAt\" IS NOT NULL OR (\"ReservedCost\" = 0 AND NOT \"ProviderAttempted\"))");
                    table.ForeignKey(
                        name: "FK_AiExecutions_Conversations_WorkspaceId_ConversationId",
                        columns: x => new { x.WorkspaceId, x.ConversationId },
                        principalTable: "Conversations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiExecutions_Messages_WorkspaceId_ConversationId_TriggerMes~",
                        columns: x => new { x.WorkspaceId, x.ConversationId, x.TriggerMessageId },
                        principalTable: "Messages",
                        principalColumns: new[] { "WorkspaceId", "ConversationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiExecutions_Users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AiExecutionId",
                table: "Messages",
                column: "AiExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_AiExecutionId",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "AiExecutionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages",
                sql: "(\"SenderKind\" = 'MEMBER' AND \"SenderUserId\" IS NOT NULL AND \"AiExecutionId\" IS NULL)\nOR (\"SenderKind\" = 'AI' AND \"SenderUserId\" IS NULL AND \"AiExecutionId\" IS NOT NULL AND \"ClientMessageId\" = \"AiExecutionId\")");

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_AiExecutionId",
                table: "HandoffRequests",
                column: "AiExecutionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HandoffRequests_WorkspaceId_ConversationId_AiExecutionId",
                table: "HandoffRequests",
                columns: new[] { "WorkspaceId", "ConversationId", "AiExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WorkspaceId_AiAgentId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "AiAgentId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_AiRevision",
                table: "Conversations",
                sql: "\"AiBindingRevision\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_ActorId",
                table: "AiExecutions",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_CreatedAt",
                table: "AiExecutions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_GenerationReservedAt",
                table: "AiExecutions",
                column: "GenerationReservedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_WorkspaceId_ConversationId_TriggerMessageId",
                table: "AiExecutions",
                columns: new[] { "WorkspaceId", "ConversationId", "TriggerMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiExecutions_WorkspaceId_CreatedAt",
                table: "AiExecutions",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_AiAgents_WorkspaceId_AiAgentId",
                table: "Conversations",
                columns: new[] { "WorkspaceId", "AiAgentId" },
                principalTable: "AiAgents",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_HandoffRequests_AiExecutions_WorkspaceId_ConversationId_AiE~",
                table: "HandoffRequests",
                columns: new[] { "WorkspaceId", "ConversationId", "AiExecutionId" },
                principalTable: "AiExecutions",
                principalColumns: new[] { "WorkspaceId", "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_AiExecutions_WorkspaceId_ConversationId_AiExecutio~",
                table: "Messages",
                columns: new[] { "WorkspaceId", "ConversationId", "AiExecutionId" },
                principalTable: "AiExecutions",
                principalColumns: new[] { "WorkspaceId", "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Legacy messages cannot represent AI provenance or the durable execution ledger. Refuse atomically.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "AiExecutions")
                        OR EXISTS (SELECT 1 FROM "Messages" WHERE "SenderKind" <> 'MEMBER' OR "SenderUserId" IS NULL)
                        OR EXISTS (SELECT 1 FROM "HandoffRequests" WHERE "AiExecutionId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade ConversationAiExecution while AI execution history exists';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_AiAgents_WorkspaceId_AiAgentId",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_HandoffRequests_AiExecutions_WorkspaceId_ConversationId_AiE~",
                table: "HandoffRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_AiExecutions_WorkspaceId_ConversationId_AiExecutio~",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "AiExecutions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Messages_WorkspaceId_ConversationId_Id",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_AiExecutionId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_WorkspaceId_ConversationId_AiExecutionId",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_Sender",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_HandoffRequests_AiExecutionId",
                table: "HandoffRequests");

            migrationBuilder.DropIndex(
                name: "IX_HandoffRequests_WorkspaceId_ConversationId_AiExecutionId",
                table: "HandoffRequests");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WorkspaceId_AiAgentId",
                table: "Conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_AiRevision",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AiExecutionId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "SenderKind",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AiExecutionId",
                table: "HandoffRequests");

            migrationBuilder.DropColumn(
                name: "AiAgentId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AiBindingRevision",
                table: "Conversations");

            migrationBuilder.AlterColumn<Guid>(
                name: "SenderUserId",
                table: "Messages",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
