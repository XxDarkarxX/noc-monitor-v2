using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NocMonitor.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncLogEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLogEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogEntries_IsAcknowledged",
                table: "SyncLogEntries",
                column: "IsAcknowledged");

            migrationBuilder.CreateIndex(
                name: "IX_SyncLogEntries_Timestamp",
                table: "SyncLogEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncLogEntries");
        }
    }
}
