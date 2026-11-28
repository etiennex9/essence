using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Essence.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_android",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    install = table.Column<Guid>(type: "uuid", nullable: false),
                    version_code = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_android", x => new { x.day, x.install, x.version_code });
                });

            migrationBuilder.CreateTable(
                name: "usage_salts",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    salt = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_salts", x => x.day);
                });

            migrationBuilder.CreateTable(
                name: "usage_web",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    visitor = table.Column<Guid>(type: "uuid", nullable: false),
                    os = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_web", x => new { x.day, x.visitor });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_android");

            migrationBuilder.DropTable(
                name: "usage_salts");

            migrationBuilder.DropTable(
                name: "usage_web");
        }
    }
}
