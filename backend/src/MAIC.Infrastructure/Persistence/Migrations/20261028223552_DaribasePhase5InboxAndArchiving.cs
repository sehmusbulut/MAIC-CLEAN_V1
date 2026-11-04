using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DaribasePhase5InboxAndArchiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Conversations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8)",
                oldMaxLength: 8);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActivityAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Conversations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "BOT_ACTIVE");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnreadCount",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Contacts",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Contacts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Contacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagsJson",
                table: "Contacts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations",
                sql: "\"Status\" IN ('OPEN', 'RESOLVED', 'ARCHIVED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "UnreadCount",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Contacts");

            migrationBuilder.DropColumn(
                name: "TagsJson",
                table: "Contacts");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Conversations",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Conversations_Status",
                table: "Conversations",
                sql: "\"Status\" IN ('OPEN', 'RESOLVED')");
        }
    }
}
