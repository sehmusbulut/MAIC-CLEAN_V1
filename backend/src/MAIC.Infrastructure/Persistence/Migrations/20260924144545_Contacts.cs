using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Contacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.UniqueConstraint("AK_Contacts_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_Contacts_DisplayName", "length(btrim(\"DisplayName\")) BETWEEN 1 AND 200 AND \"DisplayName\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_Contacts_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContactIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactIdentities", x => x.Id);
                    table.CheckConstraint("CK_ContactIdentities_Kind", "\"Kind\" ~ '^[a-z][a-z0-9._:-]{0,63}$'");
                    table.CheckConstraint("CK_ContactIdentities_Value", "length(\"Value\") BETWEEN 1 AND 512 AND \"Value\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_ContactIdentities_Contacts_WorkspaceId_ContactId",
                        columns: x => new { x.WorkspaceId, x.ContactId },
                        principalTable: "Contacts",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactIdentities_WorkspaceId_ContactId",
                table: "ContactIdentities",
                columns: new[] { "WorkspaceId", "ContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactIdentities_WorkspaceId_Kind_Value",
                table: "ContactIdentities",
                columns: new[] { "WorkspaceId", "Kind", "Value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactIdentities");

            migrationBuilder.DropTable(
                name: "Contacts");
        }
    }
}
