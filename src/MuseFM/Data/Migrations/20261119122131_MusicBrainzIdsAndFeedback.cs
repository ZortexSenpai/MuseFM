using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuseFM.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicBrainzIdsAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Tracks",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Artists",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Albums",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrackFeedback",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    TrackId = table.Column<int>(type: "INTEGER", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackFeedback", x => new { x.UserId, x.TrackId });
                    table.ForeignKey(
                        name: "FK_TrackFeedback_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrackFeedback_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tracks_MusicBrainzId",
                table: "Tracks",
                column: "MusicBrainzId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackFeedback_TrackId",
                table: "TrackFeedback",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackFeedback_UserId_Score_CreatedAt",
                table: "TrackFeedback",
                columns: new[] { "UserId", "Score", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackFeedback");

            migrationBuilder.DropIndex(
                name: "IX_Tracks_MusicBrainzId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Albums");
        }
    }
}
