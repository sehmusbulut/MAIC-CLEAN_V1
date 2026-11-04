using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DaribasePhase1And2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeDocuments_OriginShape",
                table: "KnowledgeDocuments");

            migrationBuilder.AddColumn<string>(
                name: "AnswerBoundaries",
                table: "Workspaces",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrandName",
                table: "Workspaces",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessSummary",
                table: "Workspaces",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DirectShareActive",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectShareKey",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EscalationRules",
                table: "Workspaces",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyFaq",
                table: "Workspaces",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "Workspaces",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyMessageCap",
                table: "Workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 5000);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "Workspaces",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "STARTER");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryColor",
                table: "Workspaces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "#7c3aed");

            migrationBuilder.AddColumn<bool>(
                name: "ShowBadge",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageLimitBytes",
                table: "Workspaces",
                type: "bigint",
                nullable: false,
                defaultValue: 104857600L);

            migrationBuilder.AddColumn<int>(
                name: "UsedMessagesThisMonth",
                table: "Workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "UsedStorageBytes",
                table: "Workspaces",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "WidgetTitle",
                table: "Workspaces",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Canlı Destek");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "Users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperAdmin",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlatformRoleId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanDefinitions",
                columns: table => new
                {
                    Plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MonthlyMessageLimit = table.Column<int>(type: "integer", nullable: false),
                    StorageLimitBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxFileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxDocumentCount = table.Column<int>(type: "integer", nullable: false),
                    MaxUrlSourceCount = table.Column<int>(type: "integer", nullable: false),
                    MaxSeats = table.Column<int>(type: "integer", nullable: false),
                    AllowedFeaturesJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanDefinitions", x => x.Plan);
                });

            migrationBuilder.CreateTable(
                name: "PlatformRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PermissionsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceRolePermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsGranted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceRolePermissions", x => x.Id);
                });

            migrationBuilder.Sql("""
                UPDATE "Workspaces"
                SET "DirectShareKey" = replace(gen_random_uuid()::text, '-', '')
                WHERE "DirectShareKey" = '';

                UPDATE "Users"
                SET "Email" = 'user-' || replace("Id"::text, '-', '') || '@local.invalid',
                    "FullName" = CASE WHEN "FullName" = '' THEN 'Kullanıcı' ELSE "FullName" END
                WHERE "Email" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_DirectShareKey",
                table: "Workspaces",
                column: "DirectShareKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PlatformRoleId",
                table: "Users",
                column: "PlatformRoleId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeDocuments_OriginShape",
                table: "KnowledgeDocuments",
                sql: "(\"Origin\" = 'UPLOAD' AND \"FileName\" IS NOT NULL AND \"SourceUrl\" IS NULL AND \"ContentSha256\" IS NOT NULL\n    AND \"ContentType\" IS NOT NULL AND \"ContentType\" IN ('text/plain', 'text/markdown', 'application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document')\n    AND \"SizeBytes\" IS NOT NULL AND \"SizeBytes\" BETWEEN 1 AND 52428800)\nOR (\"Origin\" = 'TEXT' AND \"FileName\" IS NULL AND \"SourceUrl\" IS NULL)\nOR (\"Origin\" = 'WEBSITE' AND \"FileName\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformRoles_Name",
                table: "PlatformRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceRolePermissions_Role_PermissionKey",
                table: "WorkspaceRolePermissions",
                columns: new[] { "Role", "PermissionKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_PlatformRoles_PlatformRoleId",
                table: "Users",
                column: "PlatformRoleId",
                principalTable: "PlatformRoles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_PlatformRoles_PlatformRoleId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "PlanDefinitions");

            migrationBuilder.DropTable(
                name: "PlatformRoles");

            migrationBuilder.DropTable(
                name: "WorkspaceRolePermissions");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_DirectShareKey",
                table: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers");

            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_PlatformRoleId",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeDocuments_OriginShape",
                table: "KnowledgeDocuments");

            migrationBuilder.DropColumn(
                name: "AnswerBoundaries",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "BrandName",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "BusinessSummary",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "DirectShareActive",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "DirectShareKey",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "EscalationRules",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "KeyFaq",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "MonthlyMessageCap",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PrimaryColor",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "ShowBadge",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "StorageLimitBytes",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "UsedMessagesThisMonth",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "UsedStorageBytes",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "WidgetTitle",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSuperAdmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlatformRoleId",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMembers_UserId",
                table: "WorkspaceMembers",
                column: "UserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeDocuments_OriginShape",
                table: "KnowledgeDocuments",
                sql: "(\"Origin\" = 'UPLOAD' AND \"FileName\" IS NOT NULL AND \"SourceUrl\" IS NULL AND \"ContentSha256\" IS NOT NULL\n    AND \"ContentType\" IS NOT NULL AND \"ContentType\" IN ('text/plain', 'text/markdown', 'application/pdf')\n    AND \"SizeBytes\" IS NOT NULL AND \"SizeBytes\" BETWEEN 1 AND 10485760)\nOR (\"Origin\" = 'TEXT' AND \"FileName\" IS NULL AND \"SourceUrl\" IS NULL)\nOR (\"Origin\" = 'WEBSITE' AND \"FileName\" IS NULL)");
        }
    }
}
