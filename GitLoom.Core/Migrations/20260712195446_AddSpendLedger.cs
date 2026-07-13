using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitLoom.Core.Migrations;

/// <inheritdoc />
public partial class AddSpendLedger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ExpectedAgents",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RepoHash = table.Column<string>(type: "TEXT", nullable: false),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                Disposition = table.Column<string>(type: "TEXT", nullable: false),
                DisposalReason = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExpectedAgents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "GatewayBudgets",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UsdMicrosCap = table.Column<long>(type: "INTEGER", nullable: false),
                TokenCap = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GatewayBudgets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SpendRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AgentId = table.Column<string>(type: "TEXT", nullable: false),
                Model = table.Column<string>(type: "TEXT", nullable: false),
                Tokens = table.Column<long>(type: "INTEGER", nullable: false),
                UsdMicros = table.Column<long>(type: "INTEGER", nullable: false),
                WhenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SpendRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ExpectedAgents_RepoHash_AgentId",
            table: "ExpectedAgents",
            columns: new[] { "RepoHash", "AgentId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SpendRecords_AgentId",
            table: "SpendRecords",
            column: "AgentId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ExpectedAgents");

        migrationBuilder.DropTable(
            name: "GatewayBudgets");

        migrationBuilder.DropTable(
            name: "SpendRecords");
    }
}
