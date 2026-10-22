using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wonder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SchedulingTokenizersAndEventV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_BotId",
                table: "Outbox");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservationExpiresAt",
                table: "Usage",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ScheduledDate",
                table: "Outbox",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageTokenBudget",
                table: "Models",
                type: "integer",
                nullable: false,
                defaultValue: 4096);

            migrationBuilder.AddColumn<int>(
                name: "MessageTokenOverhead",
                table: "Models",
                type: "integer",
                nullable: false,
                defaultValue: 16);

            migrationBuilder.AddColumn<string>(
                name: "TokenizerId",
                table: "Models",
                type: "text",
                nullable: false,
                defaultValue: "estimate");

            // Old daily jobs used UTC dates; preserve their meaning during this one-time conversion.
            migrationBuilder.Sql("""
                UPDATE "Outbox" SET "Payload" = json_build_object('date', ("CreatedAt" AT TIME ZONE 'UTC')::date)::text
                WHERE "Type" IN ('maintenance', 'narrative') AND "SchemaVersion" = 1;
                WITH first_jobs AS (
                    SELECT "Id", ("CreatedAt" AT TIME ZONE 'UTC')::date AS day,
                        row_number() OVER (PARTITION BY "BotId", "Type", ("CreatedAt" AT TIME ZONE 'UTC')::date ORDER BY "Sequence") AS ordinal
                    FROM "Outbox" WHERE "Type" IN ('maintenance', 'narrative')
                ) UPDATE "Outbox" j SET "ScheduledDate" = f.day FROM first_jobs f WHERE f."Id" = j."Id" AND f.ordinal = 1;
                UPDATE "Outbox" SET "SchemaVersion" = 2 WHERE "SchemaVersion" = 1;
                """);
            using (var script = typeof(SchedulingTokenizersAndEventV2).Assembly.GetManifestResourceStream("Wonder.Infrastructure.Persistence.quartz-postgres.sql")!)
            using (var reader = new System.IO.StreamReader(script)) migrationBuilder.Sql(reader.ReadToEnd());

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_BotId_Type_ScheduledDate",
                table: "Outbox",
                columns: new[] { "BotId", "Type", "ScheduledDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Outbox" SET "Payload" = json_build_object('id', "BotId")::text
                WHERE "Type" IN ('maintenance', 'narrative') AND "SchemaVersion" = 2;
                UPDATE "Outbox" SET "SchemaVersion" = 1 WHERE "SchemaVersion" = 2;
                DELETE FROM "Usage" WHERE "ReservationExpiresAt" IS NOT NULL;
                DROP TABLE qrtz_fired_triggers, qrtz_paused_trigger_grps, qrtz_scheduler_state, qrtz_locks,
                    qrtz_simprop_triggers, qrtz_simple_triggers, qrtz_cron_triggers, qrtz_blob_triggers,
                    qrtz_triggers, qrtz_job_details, qrtz_calendars;
                """);
            migrationBuilder.DropIndex(
                name: "IX_Outbox_BotId_Type_ScheduledDate",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "ReservationExpiresAt",
                table: "Usage");

            migrationBuilder.DropColumn(
                name: "ScheduledDate",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "ImageTokenBudget",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "MessageTokenOverhead",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "TokenizerId",
                table: "Models");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_BotId",
                table: "Outbox",
                column: "BotId");
        }
    }
}
