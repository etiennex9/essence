using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "imports",
                type: "text",
                nullable: false,
                // Every import before this migration came from the live feed.
                defaultValue: "feed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source",
                table: "imports");
        }
    }
}
