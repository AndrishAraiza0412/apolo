using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class Cfdi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SatCode",
                table: "Products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SatUnit",
                table: "Products",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CancelReason",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentForm",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StampedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Uuid",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Xml",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CfdiUse",
                table: "Customers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TaxRegime",
                table: "Customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZipCode",
                table: "Customers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Rfc",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxRegime",
                table: "Companies",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZipCode",
                table: "Companies",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SatCode",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SatUnit",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CancelReason",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentForm",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "StampedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Uuid",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Xml",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CfdiUse",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ZipCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "LegalName",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Rfc",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TaxRegime",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ZipCode",
                table: "Companies");
        }
    }
}
