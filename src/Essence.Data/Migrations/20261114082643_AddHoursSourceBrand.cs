using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHoursSourceBrand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_brand",
                table: "station_hours",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_brand",
                table: "station_hours");
        }
    }
}
