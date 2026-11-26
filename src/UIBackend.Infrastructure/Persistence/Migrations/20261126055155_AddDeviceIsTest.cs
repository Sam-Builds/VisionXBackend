using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UIBackend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceIsTest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                table: "MonitoredDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTest",
                table: "MonitoredDevices");
        }
    }
}
