using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NocMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostLastCheckSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckAt",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckError",
                table: "Hosts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastCheckLatencyMs",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastCheckSuccess",
                table: "Hosts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckAt",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "LastCheckError",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "LastCheckLatencyMs",
                table: "Hosts");

            migrationBuilder.DropColumn(
                name: "LastCheckSuccess",
                table: "Hosts");
        }
    }
}
