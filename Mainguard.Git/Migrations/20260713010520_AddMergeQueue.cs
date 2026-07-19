using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddMergeQueue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MergeLeaseRows",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoHash = table.Column<string>(type: "TEXT", nullable: false),
                LeaseId = table.Column<string>(type: "TEXT", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                ExpectedMainSha = table.Column<string>(type: "TEXT", nullable: false),
                MainBranch = table.Column<string>(type: "TEXT", nullable: false),
                Confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                PostMergeSha = table.Column<string>(type: "TEXT", nullable: true),
                BeginUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MergeLeaseRows", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MergeQueueRows",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoHash = table.Column<string>(type: "TEXT", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<string>(type: "TEXT", nullable: false),
                LastVerificationId = table.Column<long>(type: "INTEGER", nullable: true),
                UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                VerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MergeQueueRows", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "VerificationRows",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoHash = table.Column<string>(type: "TEXT", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                MainSha = table.Column<string>(type: "TEXT", nullable: false),
                Passed = table.Column<bool>(type: "INTEGER", nullable: false),
                LogArtifactPath = table.Column<string>(type: "TEXT", nullable: false),
                ResolvedCommand = table.Column<string>(type: "TEXT", nullable: false),
                ConfigHash = table.Column<string>(type: "TEXT", nullable: false),
                WhenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VerificationRows", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MergeLeaseRows_RepoHash",
            table: "MergeLeaseRows",
            column: "RepoHash");

        migrationBuilder.CreateIndex(
            name: "IX_MergeQueueRows_RepoHash_AgentId",
            table: "MergeQueueRows",
            columns: new[] { "RepoHash", "AgentId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_VerificationRows_RepoHash_AgentId",
            table: "VerificationRows",
            columns: new[] { "RepoHash", "AgentId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MergeLeaseRows");

        migrationBuilder.DropTable(
            name: "MergeQueueRows");

        migrationBuilder.DropTable(
            name: "VerificationRows");
    }
}
