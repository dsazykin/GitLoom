using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddGitProfiles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GitProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", nullable: false),
                UserEmail = table.Column<string>(type: "TEXT", nullable: false),
                SignCommits = table.Column<bool>(type: "INTEGER", nullable: false),
                GpgFormat = table.Column<string>(type: "TEXT", nullable: false),
                SigningKey = table.Column<string>(type: "TEXT", nullable: false),
                GpgProgram = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GitProfiles", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "GitProfiles");
    }
}
