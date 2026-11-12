using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStationHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hours_imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_version = table.Column<string>(type: "text", nullable: true),
                    records = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    changed = table.Column<int>(type: "integer", nullable: false),
                    warnings = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hours_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "station_hours",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    opening_hours = table.Column<string>(type: "text", nullable: true),
                    source_ref = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    matched_by = table.Column<string>(type: "text", nullable: false),
                    match_distance_m = table.Column<int>(type: "integer", nullable: true),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    temporarily_closed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    source_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    hours_import_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_station_hours", x => x.id);
                    table.ForeignKey(
                        name: "fk_station_hours_hours_imports_hours_import_id",
                        column: x => x.hours_import_id,
                        principalTable: "hours_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_station_hours_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hours_imports_source_finished_at",
                table: "hours_imports",
                columns: new[] { "source", "finished_at" });

            migrationBuilder.CreateIndex(
                name: "ix_station_hours_hours_import_id",
                table: "station_hours",
                column: "hours_import_id");

            migrationBuilder.CreateIndex(
                name: "ix_station_hours_source",
                table: "station_hours",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "ix_station_hours_station_id_source",
                table: "station_hours",
                columns: new[] { "station_id", "source" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "station_hours");

            migrationBuilder.DropTable(
                name: "hours_imports");
        }
    }
}
