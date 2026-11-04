using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DaribasePhase3SettingsPlayground : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Workspaces",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiScheduleMode",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "ALWAYS_AI");

            migrationBuilder.AddColumn<string>(
                name: "AllowedDomains",
                table: "Workspaces",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssistantRole",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "info_desk");

            migrationBuilder.AddColumn<string>(
                name: "InitialKnowledgeMethod",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "form");

            migrationBuilder.AddColumn<int>(
                name: "PlaygroundMonthlyCap",
                table: "Workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryChannel",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "website");

            migrationBuilder.AddColumn<bool>(
                name: "SoundNotificationEnabled",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "SoundTone",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "classic");

            migrationBuilder.AddColumn<string>(
                name: "UseCase",
                table: "Workspaces",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "ecommerce");

            migrationBuilder.AddColumn<int>(
                name: "UsedPlaygroundMessagesThisMonth",
                table: "Workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WelcomeMessage",
                table: "Workspaces",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "Merhaba, sağlıklı günler diliyorum.");

            migrationBuilder.AddColumn<string>(
                name: "WidgetSubtitle",
                table: "Workspaces",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "Genel destek • Online");

            migrationBuilder.AddColumn<int>(
                name: "PlaygroundMonthlyLimit",
                table: "PlanDefinitions",
                type: "integer",
                nullable: false,
                defaultValue: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiScheduleMode",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AllowedDomains",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AssistantRole",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "InitialKnowledgeMethod",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PlaygroundMonthlyCap",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PrimaryChannel",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SoundNotificationEnabled",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SoundTone",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "UseCase",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "UsedPlaygroundMessagesThisMonth",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "WelcomeMessage",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "WidgetSubtitle",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PlaygroundMonthlyLimit",
                table: "PlanDefinitions");

            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Workspaces",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
