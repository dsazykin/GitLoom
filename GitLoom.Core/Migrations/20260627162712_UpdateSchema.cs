using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GitLoom.Core.Migrations;

/// <inheritdoc />
public partial class UpdateSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ParentCategoryId",
            table: "WorkspaceCategories",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CustomIconColor",
            table: "Repositories",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.UpdateData(
            table: "WorkspaceCategories",
            keyColumn: "CategoryId",
            keyValue: 1,
            column: "ParentCategoryId",
            value: null);

        migrationBuilder.UpdateData(
            table: "WorkspaceCategories",
            keyColumn: "CategoryId",
            keyValue: 2,
            column: "ParentCategoryId",
            value: null);

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceCategories_ParentCategoryId",
            table: "WorkspaceCategories",
            column: "ParentCategoryId");

        migrationBuilder.AddForeignKey(
            name: "FK_WorkspaceCategories_WorkspaceCategories_ParentCategoryId",
            table: "WorkspaceCategories",
            column: "ParentCategoryId",
            principalTable: "WorkspaceCategories",
            principalColumn: "CategoryId",
            onDelete: ReferentialAction.Cascade);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_WorkspaceCategories_WorkspaceCategories_ParentCategoryId",
            table: "WorkspaceCategories");

        migrationBuilder.DropIndex(
            name: "IX_WorkspaceCategories_ParentCategoryId",
            table: "WorkspaceCategories");

        migrationBuilder.DropColumn(
            name: "ParentCategoryId",
            table: "WorkspaceCategories");

        migrationBuilder.DropColumn(
            name: "CustomIconColor",
            table: "Repositories");
    }
}
