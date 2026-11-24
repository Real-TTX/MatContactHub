using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatContactHub.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourcesDiscoveredFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscoveredFields",
                table: "DataSource",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveredFields",
                table: "DataSource");
        }
    }
}
