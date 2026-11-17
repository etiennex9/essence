using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "position_candidates",
                columns: table => new
                {
                    station_id = table.Column<long>(type: "bigint", nullable: false),
                    natural_key = table.Column<string>(type: "text", nullable: false),
                    feed_latitude = table.Column<double>(type: "double precision", nullable: false),
                    feed_longitude = table.Column<double>(type: "double precision", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false),
                    distance_m = table.Column<int>(type: "integer", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    found_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_position_candidates", x => x.station_id);
                    table.ForeignKey(
                        name: "fk_position_candidates_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_candidates");
        }
    }
}
