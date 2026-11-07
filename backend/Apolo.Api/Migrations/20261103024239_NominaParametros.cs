using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class NominaParametros : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cause",
                table: "PayrollRuns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Absences",
                table: "PayrollLines",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Concepts",
                table: "PayrollLines",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "IsrAdjustment",
                table: "PayrollLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SickDays",
                table: "PayrollLines",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VacationDays",
                table: "PayrollLines",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "VacationPremium",
                table: "PayrollLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TerminationDate",
                table: "Employees",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayrollJson",
                table: "Companies",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cause",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "Absences",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Concepts",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "IsrAdjustment",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "SickDays",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "VacationDays",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "VacationPremium",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "TerminationDate",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "PayrollJson",
                table: "Companies");
        }
    }
}
