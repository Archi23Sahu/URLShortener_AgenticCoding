using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UrlShortener.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShortUrls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(12)", nullable: false, collation: "SQL_Latin1_General_CP1_CS_AS"),
                    OriginalUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    OriginalUrlHash = table.Column<string>(type: "char(64)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortUrls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClickEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShortUrlId = table.Column<long>(type: "bigint", nullable: false),
                    ClickedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VisitorHash = table.Column<string>(type: "char(64)", nullable: false),
                    Country = table.Column<string>(type: "char(2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClickEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClickEvents_ShortUrls_ShortUrlId",
                        column: x => x.ShortUrlId,
                        principalTable: "ShortUrls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClickEvents_ShortUrlId_ClickedAt",
                table: "ClickEvents",
                columns: new[] { "ShortUrlId", "ClickedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShortUrls_ExpiresAt",
                table: "ShortUrls",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ShortUrls_OriginalUrlHash",
                table: "ShortUrls",
                column: "OriginalUrlHash");

            migrationBuilder.CreateIndex(
                name: "UX_ShortUrls_Code",
                table: "ShortUrls",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClickEvents");

            migrationBuilder.DropTable(
                name: "ShortUrls");
        }
    }
}
