using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitLoom.Core.Migrations;

/// <inheritdoc />
public partial class AddJournalEntries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JournalEntries",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoPath = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: false),
                WhenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                PreStateJson = table.Column<string>(type: "TEXT", nullable: false),
                PostStateJson = table.Column<string>(type: "TEXT", nullable: false),
                IsUndoable = table.Column<bool>(type: "INTEGER", nullable: false),
                UndoBlockedReason = table.Column<string>(type: "TEXT", nullable: true),
                IsUndone = table.Column<bool>(type: "INTEGER", nullable: false),
                IsTruncated = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JournalEntries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_JournalEntries_RepoPath",
            table: "JournalEntries",
            column: "RepoPath");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "JournalEntries");
    }
}
