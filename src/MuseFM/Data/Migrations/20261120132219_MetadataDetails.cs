using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuseFM.Data.Migrations
{
    /// <inheritdoc />
    public partial class MetadataDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "ArtistMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "ArtistMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "AlbumMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseYear",
                table: "AlbumMetadata",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "AlbumMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Genres",
                table: "ArtistMetadata");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "ArtistMetadata");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "AlbumMetadata");

            migrationBuilder.DropColumn(
                name: "ReleaseYear",
                table: "AlbumMetadata");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AlbumMetadata");
        }
    }
}
