using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class Fiscal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "IsrWithheld",
                table: "Invoices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "IvaWithheld",
                table: "Invoices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ClosedThrough",
                table: "Companies",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProfitCoefficient",
                table: "Companies",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "SatCode",
                table: "Accounts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsrWithheld",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IvaWithheld",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ClosedThrough",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ProfitCoefficient",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "SatCode",
                table: "Accounts");
        }
    }
}
