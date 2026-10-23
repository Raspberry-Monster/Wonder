using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContextBudgetProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Estimated",
                table: "Usage",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProgressJson",
                table: "Outbox",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Estimated",
                table: "Usage");

            migrationBuilder.DropColumn(
                name: "ProgressJson",
                table: "Outbox");
        }
    }
}
