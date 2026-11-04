using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeVectors : Migration
    {
        private static class Sql
        {
            public const string Up = """
                CREATE EXTENSION IF NOT EXISTS vector;

                CREATE TABLE "KnowledgeEmbeddingSpaces" (
                    "Key" character varying(64) COLLATE "C" NOT NULL,
                    "Dimensions" integer NOT NULL,
                    "Model" character varying(200) COLLATE "C" NOT NULL,
                    "CreatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_KnowledgeEmbeddingSpaces" PRIMARY KEY ("Key"),
                    CONSTRAINT "CK_KnowledgeEmbeddingSpaces_Key" CHECK ("Key" ~ '^[a-z0-9][a-z0-9._-]{0,63}$' AND "Key" <> 'disabled'),
                    CONSTRAINT "CK_KnowledgeEmbeddingSpaces_Dimensions" CHECK ("Dimensions" BETWEEN 1 AND 4096),
                    CONSTRAINT "CK_KnowledgeEmbeddingSpaces_Model" CHECK (length(btrim("Model")) > 0)
                );

                -- Every ID is bound by a composite foreign key: the chunk must belong to the document and workspace, and the
                -- source must be the document's. Vectors cascade with their chunk, document (and so source and workspace)
                -- and space. Per-space HNSW indexes are partial expression indexes created at space registration.
                CREATE TABLE "KnowledgeChunkVectors" (
                    "SpaceKey" character varying(64) COLLATE "C" NOT NULL,
                    "WorkspaceId" uuid NOT NULL,
                    "KnowledgeSourceId" uuid NOT NULL,
                    "DocumentId" uuid NOT NULL,
                    "ChunkId" uuid NOT NULL,
                    "Sequence" integer NOT NULL,
                    "Embedding" vector NOT NULL,
                    CONSTRAINT "PK_KnowledgeChunkVectors" PRIMARY KEY ("SpaceKey", "WorkspaceId", "ChunkId"),
                    CONSTRAINT "CK_KnowledgeChunkVectors_Sequence" CHECK ("Sequence" > 0),
                    CONSTRAINT "FK_KnowledgeChunkVectors_Spaces" FOREIGN KEY ("SpaceKey")
                        REFERENCES "KnowledgeEmbeddingSpaces" ("Key") ON DELETE CASCADE,
                    CONSTRAINT "FK_KnowledgeChunkVectors_Chunks" FOREIGN KEY ("WorkspaceId", "DocumentId", "ChunkId")
                        REFERENCES "KnowledgeDocumentChunks" ("WorkspaceId", "DocumentId", "Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_KnowledgeChunkVectors_Documents" FOREIGN KEY ("WorkspaceId", "KnowledgeSourceId", "DocumentId")
                        REFERENCES "KnowledgeDocuments" ("WorkspaceId", "KnowledgeSourceId", "Id") ON DELETE CASCADE
                );
                CREATE INDEX "IX_KnowledgeChunkVectors_Chunk" ON "KnowledgeChunkVectors" ("WorkspaceId", "DocumentId", "ChunkId");
                CREATE INDEX "IX_KnowledgeChunkVectors_Source" ON "KnowledgeChunkVectors" ("WorkspaceId", "KnowledgeSourceId", "DocumentId");

                CREATE TABLE "KnowledgeIndexStates" (
                    "SpaceKey" character varying(64) COLLATE "C" NOT NULL,
                    "WorkspaceId" uuid NOT NULL,
                    "DocumentId" uuid NOT NULL,
                    "DocumentProcessedAt" timestamp with time zone NOT NULL,
                    "Status" character varying(8) NOT NULL,
                    "Attempts" integer NOT NULL,
                    "MisconfiguredStrikes" integer NOT NULL,
                    "LeaseToken" uuid NULL,
                    "NextAttemptAt" timestamp with time zone NOT NULL,
                    "LastFailure" character varying(32) NULL,
                    "Failure" character varying(32) NULL,
                    "IndexedChunks" integer NOT NULL DEFAULT 0,
                    "RejectedChunks" integer NOT NULL DEFAULT 0,
                    "Rechecks" integer NOT NULL DEFAULT 0,
                    "UpdatedAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_KnowledgeIndexStates" PRIMARY KEY ("SpaceKey", "WorkspaceId", "DocumentId"),
                    CONSTRAINT "FK_KnowledgeIndexStates_Spaces" FOREIGN KEY ("SpaceKey")
                        REFERENCES "KnowledgeEmbeddingSpaces" ("Key") ON DELETE CASCADE,
                    CONSTRAINT "FK_KnowledgeIndexStates_Documents" FOREIGN KEY ("WorkspaceId", "DocumentId")
                        REFERENCES "KnowledgeDocuments" ("WorkspaceId", "Id") ON DELETE CASCADE,
                    CONSTRAINT "CK_KnowledgeIndexStates_Status" CHECK ("Status" IN ('PENDING', 'INDEXING', 'RETRY', 'INDEXED', 'FAILED')),
                    CONSTRAINT "CK_KnowledgeIndexStates_LastFailure" CHECK ("LastFailure" IS NULL
                        OR "LastFailure" IN ('UNAVAILABLE', 'INVALID_RESPONSE', 'MISCONFIGURED', 'INTERNAL_ERROR')),
                    CONSTRAINT "CK_KnowledgeIndexStates_Failure" CHECK ("Failure" IS NULL OR "Failure" = 'EMBEDDING_REJECTED'),
                    CONSTRAINT "CK_KnowledgeIndexStates_Counts" CHECK ("Attempts" >= 0 AND "MisconfiguredStrikes" >= 0
                        AND "IndexedChunks" >= 0 AND "RejectedChunks" >= 0 AND "Rechecks" >= 0),
                    CONSTRAINT "CK_KnowledgeIndexStates_Lifecycle" CHECK (
                        ("Status" = 'INDEXING') = ("LeaseToken" IS NOT NULL) AND ("Status" = 'FAILED') = ("Failure" IS NOT NULL))
                );
                CREATE INDEX "IX_KnowledgeIndexStates_Due" ON "KnowledgeIndexStates" ("SpaceKey", "NextAttemptAt")
                    WHERE "Status" IN ('PENDING', 'RETRY', 'INDEXING');
                -- Recent activity per workspace, for round-robin claims.
                CREATE INDEX "IX_KnowledgeIndexStates_Activity" ON "KnowledgeIndexStates" ("SpaceKey", "UpdatedAt", "WorkspaceId")
                    WHERE "Status" <> 'PENDING';
                -- Discovery scans READY documents only.
                CREATE INDEX "IX_KnowledgeDocuments_Ready" ON "KnowledgeDocuments" ("WorkspaceId", "ProcessedAt", "Id")
                    WHERE "Status" = 'READY';
                """;
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeDocuments_WorkspaceId_KnowledgeSourceId_Id",
                table: "KnowledgeDocuments");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_KnowledgeDocuments_WorkspaceId_KnowledgeSourceId_Id",
                table: "KnowledgeDocuments",
                columns: new[] { "WorkspaceId", "KnowledgeSourceId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_KnowledgeDocumentChunks_WorkspaceId_DocumentId_Id",
                table: "KnowledgeDocumentChunks",
                columns: new[] { "WorkspaceId", "DocumentId", "Id" });

            // Vector tables are raw SQL (pgvector types are not mapped by EF). The database user must be allowed to create
            // the extension, or an operator creates it beforehand (Supabase: enable "vector").
            migrationBuilder.Sql(Sql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "KnowledgeIndexStates";
                DROP TABLE IF EXISTS "KnowledgeChunkVectors";
                DROP TABLE IF EXISTS "KnowledgeEmbeddingSpaces";
                DROP INDEX IF EXISTS "IX_KnowledgeDocuments_Ready";
                """);

            migrationBuilder.DropUniqueConstraint(
                name: "AK_KnowledgeDocuments_WorkspaceId_KnowledgeSourceId_Id",
                table: "KnowledgeDocuments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_KnowledgeDocumentChunks_WorkspaceId_DocumentId_Id",
                table: "KnowledgeDocumentChunks");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_WorkspaceId_KnowledgeSourceId_Id",
                table: "KnowledgeDocuments",
                columns: new[] { "WorkspaceId", "KnowledgeSourceId", "Id" });
        }
    }
}
