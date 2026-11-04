using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiAgentHiddenCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_AiAgents_HiddenCharacters",
                table: "AiAgents",
                sql: "\"Name\" !~ U&'[\\202A-\\202E\\2066-\\2069\\200B\\2060\\FEFF\\+0E0000-\\+0E007F]'\nAND (\"Instructions\" IS NULL OR \"Instructions\" !~ U&'[\\202A-\\202E\\2066-\\2069\\200B\\2060\\FEFF\\+0E0000-\\+0E007F]')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AiAgents_HiddenCharacters",
                table: "AiAgents");
        }
    }
}
