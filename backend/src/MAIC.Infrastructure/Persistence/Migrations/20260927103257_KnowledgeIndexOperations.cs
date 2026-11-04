using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeIndexOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw SQL like the other vector tables. LastSeenAt records the last use of a space by any host (retirement);
            // KnowledgeIndexUsage is an append-only daily ledger of chunks sent for indexing, deliberately without a
            // foreign key to documents so deleting or re-ingesting content never erases its cost.
            migrationBuilder.Sql("""
                ALTER TABLE "KnowledgeEmbeddingSpaces" ADD COLUMN "LastSeenAt" timestamp with time zone NOT NULL DEFAULT now();

                CREATE TABLE "KnowledgeIndexUsage" (
                    "SpaceKey" character varying(64) COLLATE "C" NOT NULL,
                    "WorkspaceId" uuid NOT NULL,
                    "Day" date NOT NULL,
                    "Chunks" bigint NOT NULL,
                    CONSTRAINT "PK_KnowledgeIndexUsage" PRIMARY KEY ("SpaceKey", "Day", "WorkspaceId"),
                    CONSTRAINT "FK_KnowledgeIndexUsage_Spaces" FOREIGN KEY ("SpaceKey")
                        REFERENCES "KnowledgeEmbeddingSpaces" ("Key") ON DELETE CASCADE,
                    CONSTRAINT "FK_KnowledgeIndexUsage_Workspaces" FOREIGN KEY ("WorkspaceId")
                        REFERENCES "Workspaces" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "CK_KnowledgeIndexUsage_Chunks" CHECK ("Chunks" >= 0)
                );
                CREATE INDEX "IX_KnowledgeIndexUsage_Workspace" ON "KnowledgeIndexUsage" ("WorkspaceId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "KnowledgeIndexUsage";
                ALTER TABLE "KnowledgeEmbeddingSpaces" DROP COLUMN IF EXISTS "LastSeenAt";
                """);
        }
    }
}
