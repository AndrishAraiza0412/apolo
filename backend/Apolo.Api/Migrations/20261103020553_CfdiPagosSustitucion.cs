using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Apolo.Api.Migrations
{
    /// <inheritdoc />
    public partial class CfdiPagosSustitucion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplacesId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubstitutedBy",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Folio = table.Column<int>(type: "integer", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentForm = table.Column<string>(type: "text", nullable: false),
                    Uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Xml = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentReceipts_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_CompanyId_Folio",
                table: "PaymentReceipts",
                columns: new[] { "CompanyId", "Folio" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentReceipts_InvoiceId",
                table: "PaymentReceipts",
                column: "InvoiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentReceipts");

            migrationBuilder.DropColumn(
                name: "ReplacesId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SubstitutedBy",
                table: "Invoices");
        }
    }
}
