using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mainguard.Git.Migrations;

/// <inheritdoc />
public partial class AddCustomIconColor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "CustomIconColor",
            table: "Repositories",
            type: "TEXT",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "CustomIconColor",
            table: "Repositories",
            type: "TEXT",
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldNullable: true);
    }
}
