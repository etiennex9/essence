using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:cube", ",,")
                .Annotation("Npgsql:PostgresExtension:earthdistance", ",,");

            migrationBuilder.CreateTable(
                name: "fuel_types",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fuel_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "imports",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    etag = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    archive_path = table.Column<string>(type: "text", nullable: true),
                    delay_seconds = table.Column<double>(type: "double precision", nullable: true),
                    stations_total = table.Column<int>(type: "integer", nullable: false),
                    stations_added = table.Column<int>(type: "integer", nullable: false),
                    stations_updated = table.Column<int>(type: "integer", nullable: false),
                    stations_deactivated = table.Column<int>(type: "integer", nullable: false),
                    prices_changed = table.Column<int>(type: "integer", nullable: false),
                    warnings = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    natural_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    brand = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    postal_code = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station_id = table.Column<long>(type: "bigint", nullable: false),
                    fuel_type_id = table.Column<short>(type: "smallint", nullable: false),
                    price_cents = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    import_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_history_fuel_types_fuel_type_id",
                        column: x => x.fuel_type_id,
                        principalTable: "fuel_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_history_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_price_history_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "station_changes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    station_id = table.Column<long>(type: "bigint", nullable: false),
                    import_id = table.Column<long>(type: "bigint", nullable: false),
                    field = table.Column<string>(type: "text", nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_station_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_station_changes_imports_import_id",
                        column: x => x.import_id,
                        principalTable: "imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_station_changes_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "current_prices",
                columns: table => new
                {
                    station_id = table.Column<long>(type: "bigint", nullable: false),
                    fuel_type_id = table.Column<short>(type: "smallint", nullable: false),
                    price_cents = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    price_history_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_current_prices", x => new { x.station_id, x.fuel_type_id });
                    table.ForeignKey(
                        name: "fk_current_prices_fuel_types_fuel_type_id",
                        column: x => x.fuel_type_id,
                        principalTable: "fuel_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_current_prices_price_history_price_history_id",
                        column: x => x.price_history_id,
                        principalTable: "price_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_current_prices_stations_station_id",
                        column: x => x.station_id,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_current_prices_fuel_type_id_price_cents",
                table: "current_prices",
                columns: new[] { "fuel_type_id", "price_cents" });

            migrationBuilder.CreateIndex(
                name: "ix_current_prices_price_history_id",
                table: "current_prices",
                column: "price_history_id");

            migrationBuilder.CreateIndex(
                name: "ix_fuel_types_code",
                table: "fuel_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_imports_generated_at",
                table: "imports",
                column: "generated_at",
                unique: true,
                filter: "status = 'succeeded'");

            migrationBuilder.CreateIndex(
                name: "ix_imports_sha256",
                table: "imports",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_price_history_fuel_type_id",
                table: "price_history",
                column: "fuel_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_history_import_id",
                table: "price_history",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_history_station_id_fuel_type_id_valid_from",
                table: "price_history",
                columns: new[] { "station_id", "fuel_type_id", "valid_from" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_price_history_open",
                table: "price_history",
                columns: new[] { "station_id", "fuel_type_id" },
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_station_changes_import_id",
                table: "station_changes",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_station_changes_station_id_changed_at",
                table: "station_changes",
                columns: new[] { "station_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stations_brand",
                table: "stations",
                column: "brand");

            migrationBuilder.CreateIndex(
                name: "ix_stations_natural_key",
                table: "stations",
                column: "natural_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stations_region",
                table: "stations",
                column: "region");

            // EF can't model expression indexes. Nearby queries must repeat both IS NOT NULL conditions to use it.
            migrationBuilder.Sql($"""
                CREATE INDEX {EssenceDbContext.EarthIndexName} ON stations
                USING gist (ll_to_earth(latitude, longitude))
                WHERE latitude IS NOT NULL AND longitude IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"DROP INDEX IF EXISTS {EssenceDbContext.EarthIndexName};");

            migrationBuilder.DropTable(
                name: "current_prices");

            migrationBuilder.DropTable(
                name: "station_changes");

            migrationBuilder.DropTable(
                name: "price_history");

            migrationBuilder.DropTable(
                name: "fuel_types");

            migrationBuilder.DropTable(
                name: "imports");

            migrationBuilder.DropTable(
                name: "stations");
        }
    }
}
