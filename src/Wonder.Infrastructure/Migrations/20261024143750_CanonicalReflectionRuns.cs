using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalReflectionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentReflectionQuotas",
                columns: table => new
                {
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    UsedNormal = table.Column<int>(type: "integer", nullable: false),
                    UsedImportant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReflectionQuotas", x => new { x.BotId, x.Date });
                    table.CheckConstraint("CK_Reflection_Quota", "\"UsedNormal\" BETWEEN 0 AND 2 AND \"UsedImportant\" BETWEEN 0 AND 1");
                });

            migrationBuilder.CreateTable(
                name: "AgentReflectionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawBoundary = table.Column<long>(type: "bigint", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentReflectionRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReflectionGenerations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    RawBoundary = table.Column<long>(type: "bigint", nullable: false),
                    SourceSignature = table.Column<string>(type: "text", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReflectionGenerations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReflectionRequests_BotId_UserId_CreatedAt",
                table: "AgentReflectionRequests",
                columns: new[] { "BotId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentReflectionRequests_OperationId",
                table: "AgentReflectionRequests",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReflectionGenerations_BotId_SessionId_SubjectId_SourceSigna~",
                table: "ReflectionGenerations",
                columns: new[] { "BotId", "SessionId", "SubjectId", "SourceSignature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReflectionGenerations_JobId",
                table: "ReflectionGenerations",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentReflectionQuotas");

            migrationBuilder.DropTable(
                name: "AgentReflectionRequests");

            migrationBuilder.DropTable(
                name: "ReflectionGenerations");
        }
    }
}
