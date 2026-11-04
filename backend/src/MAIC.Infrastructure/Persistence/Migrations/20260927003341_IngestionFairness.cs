using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MAIC.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IngestionFairness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_Running",
                table: "IngestionJobs",
                columns: new[] { "WorkspaceId", "NextAttemptAt" },
                filter: "\"Status\" = 'RUNNING'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IngestionJobs_Running",
                table: "IngestionJobs");
        }
    }
}
