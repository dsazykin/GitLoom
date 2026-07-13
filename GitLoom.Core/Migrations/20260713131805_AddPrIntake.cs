using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitLoom.Core.Migrations;

/// <inheritdoc />
public partial class AddPrIntake : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Origin",
            table: "MergeQueueRows",
            type: "TEXT",
            nullable: false,
            defaultValue: "Local");

        migrationBuilder.CreateTable(
            name: "PrIntakeHeads",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                PrNumber = table.Column<int>(type: "INTEGER", nullable: false),
                SeenHeadSha = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrIntakeHeads", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PrIntakeSubscriptions",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Host = table.Column<string>(type: "TEXT", nullable: false),
                Owner = table.Column<string>(type: "TEXT", nullable: false),
                Repo = table.Column<string>(type: "TEXT", nullable: false),
                AuthorFilter = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrIntakeSubscriptions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PrIntakeHeads_SourceKey_PrNumber",
            table: "PrIntakeHeads",
            columns: new[] { "SourceKey", "PrNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PrIntakeSubscriptions_Host_Owner_Repo_AuthorFilter",
            table: "PrIntakeSubscriptions",
            columns: new[] { "Host", "Owner", "Repo", "AuthorFilter" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PrIntakeHeads");

        migrationBuilder.DropTable(
            name: "PrIntakeSubscriptions");

        migrationBuilder.DropColumn(
            name: "Origin",
            table: "MergeQueueRows");
    }
}
