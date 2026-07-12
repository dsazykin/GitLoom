using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitLoom.Core.Migrations;

/// <inheritdoc />
public partial class AddTosAcknowledgment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TosAcknowledgments",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Provider = table.Column<string>(type: "TEXT", nullable: false),
                AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TosAcknowledgments", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TosAcknowledgments_Provider",
            table: "TosAcknowledgments",
            column: "Provider");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TosAcknowledgments");
    }
}
