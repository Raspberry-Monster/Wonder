using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModelThinkingOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "Models",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ThinkingEnabled",
                table: "Models",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "ThinkingEnabled",
                table: "Models");
        }
    }
}
