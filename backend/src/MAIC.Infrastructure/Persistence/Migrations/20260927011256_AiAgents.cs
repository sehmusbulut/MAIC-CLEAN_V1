using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiAgents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Instructions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true, collation: "C"),
                    Language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true, collation: "C"),
                    ModelTier = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    KnowledgeScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HandoffOnRequest = table.Column<bool>(type: "boolean", nullable: false),
                    HandoffWhenUnanswered = table.Column<bool>(type: "boolean", nullable: false),
                    MaxAiReplies = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAgents", x => x.Id);
                    table.UniqueConstraint("AK_AiAgents_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_AiAgents_Instructions", "\"Instructions\" IS NULL OR (\n    length(\"Instructions\") + length(regexp_replace(\"Instructions\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) <= 8000\n    AND \"Instructions\" !~ U&'[\\0001-\\0008\\000B\\000C\\000E-\\001F\\007F-\\009F]'\n    AND \"Instructions\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]')");
                    table.CheckConstraint("CK_AiAgents_KnowledgeScope", "\"KnowledgeScope\" IN ('ALL_SOURCES', 'SELECTED_SOURCES')");
                    table.CheckConstraint("CK_AiAgents_Language", "\"Language\" IS NULL OR (length(\"Language\") <= 35 AND \"Language\" ~ '^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$')");
                    table.CheckConstraint("CK_AiAgents_MaxAiReplies", "\"MaxAiReplies\" IS NULL OR \"MaxAiReplies\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_AiAgents_ModelTier", "\"ModelTier\" IN ('FAST', 'BALANCED', 'QUALITY')");
                    table.CheckConstraint("CK_AiAgents_Name", "length(\"Name\") + length(regexp_replace(\"Name\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) BETWEEN 1 AND 200\nAND \"Name\" !~ U&'[\\0001-\\001F\\007F-\\009F]'\nAND \"Name\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'");
                    table.CheckConstraint("CK_AiAgents_Timestamps", "\"UpdatedAt\" >= \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_AiAgents_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiAgentKnowledgeSources",
                columns: table => new
                {
                    AiAgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAgentKnowledgeSources", x => new { x.AiAgentId, x.KnowledgeSourceId });
                    table.ForeignKey(
                        name: "FK_AiAgentKnowledgeSources_AiAgents_WorkspaceId_AiAgentId",
                        columns: x => new { x.WorkspaceId, x.AiAgentId },
                        principalTable: "AiAgents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiAgentKnowledgeSources_KnowledgeSources_WorkspaceId_Knowle~",
                        columns: x => new { x.WorkspaceId, x.KnowledgeSourceId },
                        principalTable: "KnowledgeSources",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAgentKnowledgeSources_WorkspaceId_AiAgentId",
                table: "AiAgentKnowledgeSources",
                columns: new[] { "WorkspaceId", "AiAgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAgentKnowledgeSources_WorkspaceId_KnowledgeSourceId",
                table: "AiAgentKnowledgeSources",
                columns: new[] { "WorkspaceId", "KnowledgeSourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAgentKnowledgeSources");

            migrationBuilder.DropTable(
                name: "AiAgents");
        }
    }
}
