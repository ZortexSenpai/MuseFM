using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuseFM.Data.Migrations
{
    /// <inheritdoc />
    public partial class ViewerPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Accent",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "violet");

            migrationBuilder.AddColumn<bool>(
                name: "Clock12",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DefaultRange",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HideArtwork",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<bool>(
                name: "WeekStartsSunday",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Accent",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Clock12",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DefaultRange",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "HideArtwork",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "WeekStartsSunday",
                table: "AspNetUsers");
        }
    }
}
