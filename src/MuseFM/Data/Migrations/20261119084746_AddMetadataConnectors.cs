using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MuseFM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumMetadata",
                columns: table => new
                {
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageFile = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ImageSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RefreshAfter = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumMetadata", x => x.AlbumId);
                    table.ForeignKey(
                        name: "FK_AlbumMetadata_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtistMetadata",
                columns: table => new
                {
                    ArtistId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageFile = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ImageSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Bio = table.Column<string>(type: "TEXT", nullable: true),
                    BioSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BioUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    MusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RefreshAfter = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistMetadata", x => x.ArtistId);
                    table.ForeignKey(
                        name: "FK_ArtistMetadata_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetadataConnectors",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataConnectors", x => x.Kind);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumMetadata");

            migrationBuilder.DropTable(
                name: "ArtistMetadata");

            migrationBuilder.DropTable(
                name: "MetadataConnectors");
        }
    }
}
