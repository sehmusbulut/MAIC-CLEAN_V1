using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeIndexBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One token bucket per embedding space, shared by every Worker: the indexing share of the provider quota.
            migrationBuilder.Sql("""
                CREATE TABLE "KnowledgeIndexBudgets" (
                    "SpaceKey" character varying(64) COLLATE "C" NOT NULL,
                    "Tokens" double precision NOT NULL,
                    "RefilledAt" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_KnowledgeIndexBudgets" PRIMARY KEY ("SpaceKey"),
                    CONSTRAINT "FK_KnowledgeIndexBudgets_Spaces" FOREIGN KEY ("SpaceKey")
                        REFERENCES "KnowledgeEmbeddingSpaces" ("Key") ON DELETE CASCADE
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "KnowledgeIndexBudgets";""");
        }
    }
}
