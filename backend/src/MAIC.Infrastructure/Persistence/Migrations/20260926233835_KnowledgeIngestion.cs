using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    KnowledgeSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ContentSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true, collation: "C"),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true, collation: "C"),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocuments", x => x.Id);
                    table.UniqueConstraint("AK_KnowledgeDocuments_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_KnowledgeDocuments_Content", "(\"ContentSha256\" IS NULL OR \"ContentSha256\" ~ '^[0-9a-f]{64}$')\nAND (\"SizeBytes\" IS NULL OR \"SizeBytes\" >= 0)\nAND (\"ContentType\" IS NULL OR \"ContentType\" ~ '^[a-z0-9.+-]+/[a-z0-9.+-]+$')\nAND (\"SourceUrl\" IS NULL OR \"SourceUrl\" ~ '^[Hh][Tt][Tt][Pp][Ss]?://[^/?#@]+([/?]|$)')");
                    table.CheckConstraint("CK_KnowledgeDocuments_Counts", "\"ChunkCount\" >= 0 AND \"CharacterCount\" >= 0");
                    table.CheckConstraint("CK_KnowledgeDocuments_FailureReason", "\"FailureReason\" IS NULL OR \"FailureReason\" IN ('SOURCE_DISABLED', 'UNSUPPORTED_CONTENT', 'INVALID_CONTENT',\n    'EMPTY_CONTENT', 'CONTENT_TOO_LARGE', 'FETCH_BLOCKED', 'FETCH_FAILED', 'STORAGE_MISSING',\n    'STORAGE_UNAVAILABLE', 'LEASE_EXPIRED', 'INTERNAL_ERROR')");
                    table.CheckConstraint("CK_KnowledgeDocuments_FileName", "\"FileName\" IS NULL OR (\n    length(\"FileName\") + length(regexp_replace(\"FileName\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) BETWEEN 1 AND 255\n    AND \"FileName\" !~ U&'[\\0001-\\001F\\007F-\\009F/]'\n    AND strpos(\"FileName\", chr(92)) = 0\n    AND \"FileName\" !~ U&'^[\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'\n    AND \"FileName\" !~ U&'[\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]$'\n    AND \"FileName\" NOT IN ('.', '..'))");
                    table.CheckConstraint("CK_KnowledgeDocuments_Lifecycle", "(\"Status\" = 'PENDING' AND \"FailureReason\" IS NULL AND \"ProcessedAt\" IS NULL\n    AND \"ChunkCount\" = 0 AND \"CharacterCount\" = 0)\nOR (\"Status\" = 'READY' AND \"FailureReason\" IS NULL AND \"ProcessedAt\" IS NOT NULL\n    AND \"ChunkCount\" > 0 AND \"CharacterCount\" > 0 AND \"ContentType\" IS NOT NULL\n    AND \"SizeBytes\" IS NOT NULL AND \"ContentSha256\" IS NOT NULL)\nOR (\"Status\" = 'FAILED' AND \"FailureReason\" IS NOT NULL AND \"ProcessedAt\" IS NOT NULL\n    AND \"ChunkCount\" = 0 AND \"CharacterCount\" = 0)\nOR (\"Status\" = 'SUPERSEDED' AND \"FailureReason\" IS NULL AND \"ProcessedAt\" IS NOT NULL AND \"Origin\" <> 'UPLOAD')");
                    table.CheckConstraint("CK_KnowledgeDocuments_Origin", "\"Origin\" IN ('UPLOAD', 'TEXT', 'WEBSITE')");
                    table.CheckConstraint("CK_KnowledgeDocuments_OriginShape", "(\"Origin\" = 'UPLOAD' AND \"FileName\" IS NOT NULL AND \"SourceUrl\" IS NULL AND \"ContentSha256\" IS NOT NULL\n    AND \"ContentType\" IS NOT NULL AND \"ContentType\" IN ('text/plain', 'text/markdown', 'application/pdf')\n    AND \"SizeBytes\" IS NOT NULL AND \"SizeBytes\" BETWEEN 1 AND 10485760)\nOR (\"Origin\" = 'TEXT' AND \"FileName\" IS NULL AND \"SourceUrl\" IS NULL)\nOR (\"Origin\" = 'WEBSITE' AND \"FileName\" IS NULL)");
                    table.CheckConstraint("CK_KnowledgeDocuments_RequestKey", "\"ClientRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_KnowledgeDocuments_Status", "\"Status\" IN ('PENDING', 'READY', 'FAILED', 'SUPERSEDED')");
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_KnowledgeSources_WorkspaceId_KnowledgeSo~",
                        columns: x => new { x.WorkspaceId, x.KnowledgeSourceId },
                        principalTable: "KnowledgeSources",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KnowledgeDocuments_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IngestionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionJobs", x => x.Id);
                    table.CheckConstraint("CK_IngestionJobs_Attempts", "\"MaxAttempts\" BETWEEN 1 AND 20 AND \"Attempts\" BETWEEN 0 AND \"MaxAttempts\"");
                    table.CheckConstraint("CK_IngestionJobs_LastError", "\"LastError\" IS NULL OR \"LastError\" IN ('SOURCE_DISABLED', 'UNSUPPORTED_CONTENT', 'INVALID_CONTENT',\n    'EMPTY_CONTENT', 'CONTENT_TOO_LARGE', 'FETCH_BLOCKED', 'FETCH_FAILED', 'STORAGE_MISSING',\n    'STORAGE_UNAVAILABLE', 'LEASE_EXPIRED', 'INTERNAL_ERROR')");
                    table.CheckConstraint("CK_IngestionJobs_Lifecycle", "(\"Status\" = 'QUEUED' AND \"LeaseToken\" IS NULL AND \"CompletedAt\" IS NULL AND \"Attempts\" < \"MaxAttempts\")\nOR (\"Status\" = 'RUNNING' AND \"LeaseToken\" IS NOT NULL AND \"CompletedAt\" IS NULL AND \"Attempts\" > 0)\nOR (\"Status\" = 'SUCCEEDED' AND \"LeaseToken\" IS NULL AND \"CompletedAt\" IS NOT NULL AND \"LastError\" IS NULL AND \"Attempts\" > 0)\nOR (\"Status\" = 'FAILED' AND \"LeaseToken\" IS NULL AND \"CompletedAt\" IS NOT NULL AND \"LastError\" IS NOT NULL AND \"Attempts\" > 0)\nOR (\"Status\" = 'CANCELLED' AND \"LeaseToken\" IS NULL AND \"CompletedAt\" IS NOT NULL AND \"LastError\" IS NOT NULL)");
                    table.CheckConstraint("CK_IngestionJobs_Status", "\"Status\" IN ('QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED')");
                    table.ForeignKey(
                        name: "FK_IngestionJobs_KnowledgeDocuments_WorkspaceId_DocumentId",
                        columns: x => new { x.WorkspaceId, x.DocumentId },
                        principalTable: "KnowledgeDocuments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeDocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    StartOffset = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeDocumentChunks", x => x.Id);
                    table.CheckConstraint("CK_KnowledgeDocumentChunks_Content", "length(\"Content\") + length(regexp_replace(\"Content\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) BETWEEN 1 AND 2000\nAND \"Content\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'");
                    table.CheckConstraint("CK_KnowledgeDocumentChunks_Position", "\"Sequence\" > 0 AND \"StartOffset\" >= 0");
                    table.ForeignKey(
                        name: "FK_KnowledgeDocumentChunks_KnowledgeDocuments_WorkspaceId_Docu~",
                        columns: x => new { x.WorkspaceId, x.DocumentId },
                        principalTable: "KnowledgeDocuments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_ActiveDocument",
                table: "IngestionJobs",
                column: "DocumentId",
                unique: true,
                filter: "\"Status\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_Due",
                table: "IngestionJobs",
                columns: new[] { "NextAttemptAt", "Id" },
                filter: "\"Status\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_WorkspaceId_DocumentId_Id",
                table: "IngestionJobs",
                columns: new[] { "WorkspaceId", "DocumentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocumentChunks_WorkspaceId_DocumentId_Sequence",
                table: "KnowledgeDocumentChunks",
                columns: new[] { "WorkspaceId", "DocumentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_CreatedByUserId",
                table: "KnowledgeDocuments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_PendingSnapshot",
                table: "KnowledgeDocuments",
                column: "KnowledgeSourceId",
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"Origin\" <> 'UPLOAD'");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_WorkspaceId_CreatedByUserId_ClientReques~",
                table: "KnowledgeDocuments",
                columns: new[] { "WorkspaceId", "CreatedByUserId", "ClientRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeDocuments_WorkspaceId_KnowledgeSourceId_Id",
                table: "KnowledgeDocuments",
                columns: new[] { "WorkspaceId", "KnowledgeSourceId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestionJobs");

            migrationBuilder.DropTable(
                name: "KnowledgeDocumentChunks");

            migrationBuilder.DropTable(
                name: "KnowledgeDocuments");
        }
    }
}
