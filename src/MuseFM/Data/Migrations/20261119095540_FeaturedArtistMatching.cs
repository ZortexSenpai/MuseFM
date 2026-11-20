using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuseFM.Data.Migrations
{
    /// <inheritdoc />
    public partial class FeaturedArtistMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tracks_Key",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Albums_Key",
                table: "Albums");

            migrationBuilder.CreateTable(
                name: "AppState",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppState", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Key",
                table: "Tracks",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_Key",
                table: "Albums",
                column: "Key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppState");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_Key",
                table: "Tracks");

            migrationBuilder.DropIndex(
                name: "IX_Albums_Key",
                table: "Albums");

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_Key",
                table: "Tracks",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Albums_Key",
                table: "Albums",
                column: "Key",
                unique: true);
        }
    }
}
