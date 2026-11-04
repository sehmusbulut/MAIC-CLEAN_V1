using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeQueryUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Append-only daily ledger of knowledge queries per workspace (all API instances), for the daily query quota.
            migrationBuilder.Sql("""
                CREATE TABLE "KnowledgeQueryUsage" (
                    "WorkspaceId" uuid NOT NULL,
                    "Day" date NOT NULL,
                    "Queries" bigint NOT NULL,
                    CONSTRAINT "PK_KnowledgeQueryUsage" PRIMARY KEY ("Day", "WorkspaceId"),
                    CONSTRAINT "FK_KnowledgeQueryUsage_Workspaces" FOREIGN KEY ("WorkspaceId")
                        REFERENCES "Workspaces" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "CK_KnowledgeQueryUsage_Queries" CHECK ("Queries" >= 0)
                );
                CREATE INDEX "IX_KnowledgeQueryUsage_Workspace" ON "KnowledgeQueryUsage" ("WorkspaceId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "KnowledgeQueryUsage";""");
        }
    }
}
