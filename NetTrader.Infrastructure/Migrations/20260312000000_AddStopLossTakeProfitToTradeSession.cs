#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;

namespace NetTrader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStopLossTakeProfitToTradeSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "StopLoss",
                table: "TradeSessions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
            migrationBuilder.AddColumn<decimal>(
                name: "TakeProfit",
                table: "TradeSessions",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // FIX #18: Correct argument order — DropColumn(name:, table:)
            // Ранее было DropColumn("TradeSessions", "TakeProfit") — аргументы перепутаны.
            // EF Core signature: DropColumn(string name, string table) — первый аргумент это КОЛОНКА.
            migrationBuilder.DropColumn(
                name: "StopLoss",
                table: "TradeSessions");
            migrationBuilder.DropColumn(
                name: "TakeProfit",
                table: "TradeSessions");
        }
    }
}