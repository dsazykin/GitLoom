using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddGatewayPerDayBudget : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "TokenCapPerDay",
            table: "GatewayBudgets",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "UsdMicrosCapPerDay",
            table: "GatewayBudgets",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TokenCapPerDay",
            table: "GatewayBudgets");

        migrationBuilder.DropColumn(
            name: "UsdMicrosCapPerDay",
            table: "GatewayBudgets");
    }
}
