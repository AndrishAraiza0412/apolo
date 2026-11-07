using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class Abonos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Xml",
                table: "PaymentReceipts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<Guid>(
                name: "Uuid",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "Folio",
                table: "PaymentReceipts",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<decimal>(
                name: "BalanceBefore",
                table: "PaymentReceipts",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "Installment",
                table: "PaymentReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Paid",
                table: "Invoices",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Las facturas ya pagadas quedan saldadas y los recibos existentes son su única parcialidad.
            migrationBuilder.Sql("UPDATE \"Invoices\" SET \"Paid\" = \"Total\" WHERE \"Status\" = 'pagada';");
            migrationBuilder.Sql("UPDATE \"PaymentReceipts\" SET \"Installment\" = 1, \"BalanceBefore\" = \"Amount\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceBefore",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "Installment",
                table: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "Paid",
                table: "Invoices");

            migrationBuilder.AlterColumn<string>(
                name: "Xml",
                table: "PaymentReceipts",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "Uuid",
                table: "PaymentReceipts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Folio",
                table: "PaymentReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
