using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class NominaCompleta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EmployerCost",
                table: "PayrollRuns",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "PayrollRuns",
                type: "text",
                nullable: false,
                defaultValue: "ordinaria");

            migrationBuilder.AddColumn<decimal>(
                name: "DoubleHours",
                table: "PayrollLines",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Employer",
                table: "PayrollLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Exempt",
                table: "PayrollLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Overtime",
                table: "PayrollLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TripleHours",
                table: "PayrollLines",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "RiskPremium",
                table: "Companies",
                type: "numeric(9,7)",
                precision: 9,
                scale: 7,
                nullable: false,
                defaultValue: 0.0054355m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmployerCost",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "DoubleHours",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Employer",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Exempt",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "Overtime",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "TripleHours",
                table: "PayrollLines");

            migrationBuilder.DropColumn(
                name: "RiskPremium",
                table: "Companies");
        }
    }
}
