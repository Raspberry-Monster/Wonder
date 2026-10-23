using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedModelProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Models");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Models",
                type: "text",
                nullable: false,
                defaultValue: "openai-compatible");
        }
    }
}
