using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddPinnedRefs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PinnedRefs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoPath = table.Column<string>(type: "TEXT", nullable: false),
                RefName = table.Column<string>(type: "TEXT", nullable: false),
                Order = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PinnedRefs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PinnedRefs_RepoPath_RefName",
            table: "PinnedRefs",
            columns: new[] { "RepoPath", "RefName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PinnedRefs");
    }
}
