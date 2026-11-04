using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    TextContent = table.Column<string>(type: "character varying(64000)", maxLength: 64000, nullable: true, collation: "C"),
                    WebsiteUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeSources", x => x.Id);
                    table.UniqueConstraint("AK_KnowledgeSources_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_KnowledgeSources_Kind", "\"Kind\" IN ('FILE', 'TEXT', 'WEBSITE')");
                    table.CheckConstraint("CK_KnowledgeSources_Name", "length(\"Name\") + length(regexp_replace(\"Name\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) BETWEEN 1 AND 200\nAND \"Name\" !~ U&'[\\0001-\\001F\\007F-\\009F]'\nAND \"Name\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'");
                    table.CheckConstraint("CK_KnowledgeSources_Payload", "(\"Kind\" = 'FILE' AND \"TextContent\" IS NULL AND \"WebsiteUrl\" IS NULL)\nOR (\"Kind\" = 'TEXT' AND \"TextContent\" IS NOT NULL AND \"WebsiteUrl\" IS NULL)\nOR (\"Kind\" = 'WEBSITE' AND \"TextContent\" IS NULL AND \"WebsiteUrl\" IS NOT NULL)");
                    table.CheckConstraint("CK_KnowledgeSources_TextContent", "\"TextContent\" IS NULL OR (\n    length(\"TextContent\") + length(regexp_replace(\"TextContent\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) <= 64000\n    AND \"TextContent\" !~ U&'[\\0001-\\0008\\000B\\000C\\000E-\\001F\\007F-\\009F]'\n    AND \"TextContent\" ~ U&'[^\\0009-\\000D\\0020\\0085\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]')");
                    table.CheckConstraint("CK_KnowledgeSources_WebsiteUrl", "\"WebsiteUrl\" IS NULL OR (\n    length(\"WebsiteUrl\") + length(regexp_replace(\"WebsiteUrl\", U&'[^\\+010000-\\+10FFFF]', '', 'g')) <= 2048\n    AND \"WebsiteUrl\" ~ '^[Hh][Tt][Tt][Pp][Ss]?://[^/?#@]+([/?]|$)'\n    AND \"WebsiteUrl\" !~ U&'[\\0001-\\0020\\007F-\\00A0\\1680\\2000-\\200A\\2028\\2029\\202F\\205F\\3000]'\n    AND strpos(\"WebsiteUrl\", '#') = 0\n    AND strpos(\"WebsiteUrl\", chr(92)) = 0)");
                    table.ForeignKey(
                        name: "FK_KnowledgeSources_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeSources");
        }
    }
}
