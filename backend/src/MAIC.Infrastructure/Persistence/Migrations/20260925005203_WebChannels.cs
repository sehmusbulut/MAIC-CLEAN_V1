using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    PublishableToken = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false, collation: "C"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.CheckConstraint("CK_Channels_Name", "length(btrim(\"Name\")) BETWEEN 1 AND 200 AND \"Name\" !~ '[[:cntrl:]]'");
                    table.CheckConstraint("CK_Channels_PublishableToken", "\"PublishableToken\" ~ '^[A-Za-z0-9_-]{43}$'");
                    table.CheckConstraint("CK_Channels_Type", "\"Type\" = 'WEB'");
                    table.ForeignKey(
                        name: "FK_Channels_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_PublishableToken",
                table: "Channels",
                column: "PublishableToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_WorkspaceId_Id",
                table: "Channels",
                columns: new[] { "WorkspaceId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Channels");
        }
    }
}
