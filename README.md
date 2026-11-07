# Apolo ERP

[![CI](https://github.com/AndrishAraiza0412/apolo/actions/workflows/ci.yml/badge.svg)](https://github.com/AndrishAraiza0412/apolo/actions/workflows/ci.yml)

Multi-module ERP for Mexican businesses with an integrated AI agent, **Apolo IA**. The interface is in Spanish (Mexico), so screen names below are given in Spanish.

Design and scope: [DESIGN.md](DESIGN.md). Stack: ASP.NET Core 10 + EF Core + PostgreSQL, React + Vite.

## Running locally

```sh
docker compose up -d                     # Postgres 17 on :15432 (apolo/apolo)
cd backend
dotnet tool restore
dotnet run --project Apolo.Api           # http://localhost:5080 — in Development, applies migrations and seeds demo data
cd ../frontend && npm install && npm run dev   # http://localhost:5173 (proxies /api → :5080)
```

Demo users (password `apolo1234`): `gerencia@`, `ventas@`, `almacen@`, `finanzas@apolo.test`. Quick access: `admin` / `admin` (Gerencia, the management role). All of this exists only in Development.

If you don't have .NET 10 installed globally: `.dotnet/dotnet` (local install, ignored by git).

## Production

Single image (`Dockerfile`): the API also serves the interface. `deploy/` has a compose file with Postgres, the API, Caddy (automatic HTTPS with Let's Encrypt) and a daily backup.

1. On a server with Docker, with the domain's DNS pointing to it: `cd deploy && cp .env.example .env`.
2. Fill in `.env`: `DOMAIN`, `DB_PASSWORD` and `SETUP_TOKEN` (`openssl rand -base64 32`). `ANTHROPIC_API_KEY` is optional and enables Apolo IA.
3. Start with `docker compose up -d --build`. Migrations are applied at startup.
4. Open `https://DOMAIN`. The first time, it asks for the setup token and creates the company and its administrator user (Gerencia role, password of 12+ characters with letters and numbers). Once a company exists, sign-up is closed.
5. Enter the tax details, the risk premium, the payroll parameters and the profit coefficient in Configuración, RRHH › Parámetros and Contabilidad › SAT y cierre. Then create the users.

- Production has no demo data and no `admin/admin` user; those exist only with `ASPNETCORE_ENVIRONMENT=Development`.
- The database publishes no ports. The session cookie is `Secure`, `HttpOnly` and `SameSite=Strict`, with HSTS. The keys that encrypt it live in the `keys` volume.
- **Backups.** Daily, in `deploy/backups/apolo-AAAAMMDD-HHMMSS.dump`, kept for 30 days (`BACKUP_KEEP_DAYS`). Copy that folder off the server (S3, another disk). To restore:
  `docker compose stop api && docker compose exec -T db pg_restore -U apolo -d apolo --clean --if-exists < backups/ARCHIVO.dump && docker compose start api`.
- **Updating:** `git pull && docker compose up -d --build`. Back up first: migrations are automatic.
- **Security** (details and ISO 27001 / SOC 2 mapping in [docs/seguridad.md](docs/seguridad.md)):
  - 15-minute lockout after 5 failed attempts.
  - Passwords of 12+ characters with letters and numbers.
  - Sessions are revoked when the password or role changes, or when the user is deactivated.
  - CSP and security headers.
  - Limit of 600 requests per minute.
  - Access log.
  - `/healthz` for monitoring.
  - `scripts/security-check.sh` runs before each release.
- Built for a single instance. With several, migrations and background jobs must run in a single process.

## Tests

```sh
cd backend && dotnet test
```

## New migration

```sh
cd backend && dotnet ef migrations add <Nombre> --project Apolo.Api
```

## Apolo IA

The chat uses Claude (`claude-opus-5-5`) with tools filtered by the user's role. To enable it, start the API with the key in the environment:

```bash
ANTHROPIC_API_KEY=sk-ant-... dotnet run --project Apolo.Api
```

Without the key, the rest of the ERP works the same and the AI screen says it needs to be configured. The agent never writes directly: `crear_factura_borrador` and `crear_oc_borrador` leave a proposal in the task queue, and only a person with permission on the module can turn it into a draft (the audit log records who approved it).

## Accounting

Each operation generates its journal entry (*póliza*) in the same transaction: issued invoice (customers / sales / VAT charged / cost of sales), collection, cancellation (reversing entry), received purchase, supplier payment and paid payroll. Journal entries are immutable; a manual entry is corrected with its reversal. The base chart of accounts (101–605) is created automatically the first time. Entry dates use the server's local day.

- **Taxes and contributions** (Contabilidad › Impuestos y cuotas): balances payable for withholdings (213), IMSS/RCV/INFONAVIT contributions (214), VAT and provisional income tax (ISR). Withholdings and contributions accept partial payments. VAT is offset first against VAT withheld by customers (120) and then against creditable VAT (118). VAT is taken on an accrual basis; the SAT return goes on a cash-flow basis.
- **Provisional ISR (legal entities).** Accumulated income × profit coefficient × 30%, minus the year's provisional payments (117) and ISR withheld by customers (119). The coefficient is entered in Contabilidad › SAT y cierre. Individuals and RESICO are not calculated.
- **Electronic accounting.** SAT y cierre downloads the chart of accounts and the month's trial balance as XML (Annex 24, version 1.3), unsealed. Each account with movements needs its grouping code: base-chart accounts include it and others are entered in the chart of accounts.
- **Fiscal year close.** The closing entry moves income and expenses to 305 Resultado del ejercicio and locks the period. No entry dated on or before the close is accepted (422). Only the last closed year can be reopened.
- **Suppliers.** Received orders accept partial payments; an order becomes paid when its balance reaches zero.

## Forecasting and background jobs

- **Demand** (`/api/forecast/demand`): exponential average (α = 0.1) of 90 days of outflows. Gives days until reaching the minimum and until running out, plus a suggested quantity covering 30 days plus the minimum. Shown in the Inventario detail and feeds "Sugerir por pronóstico" in Compras.
- **Cash** (`/api/forecast/cash`): cash and bank balance at the close of the next 3 months. Adds collections on their due dates, supplier payments, average payroll and recent averages of expenses and sales. Assumptions are shown in Contabilidad › Pronóstico de caja; the Dashboard draws the projection as a dotted line.
- **Jobs** (`/api/jobs`): accounting reconciliation (7 checks) and reports run in a worker inside the API. Progress is saved in `BackgroundJobs` and the interface polls it every second while the job runs. If the API restarts, it resumes pending jobs. Apolo IA can start them with `tarea_en_segundo_plano`.
- A single in-process worker is enough for one instance. With several instances, move to an external queue.

## Electronic invoicing (CFDI 4.0)

- With the RFC (tax ID), legal name, tax regime and postal code in Configuración › Empresa, each issued invoice is stamped as an income CFDI 4.0. Without an RFC, the invoice stays an internal document.
- The recipient needs an RFC, tax regime and postal code, entered on the customer. The generic RFC `XAXX010101000` invoices the general public.
- Credit invoices go out as PPD with payment form 99; cash invoices as PUE with payment form 03.
- The XML can be downloaded from the invoice, and the printed representation carries the SAT verification QR. Cancellation asks for the SAT reason (02, 03 or 04).
- **Replace** creates a draft with the same content. When issued, the new CFDI is linked to the original (type 04) and the original is canceled with reason 01.
- Registering the collection of a stamped PPD invoice issues its electronic payment receipt (CFDI type P, Pagos 2.0 complement), series P.
- PPD invoices accept **partial payments**. Each one generates its income journal entry and payment receipt, with installment number, previous balance and outstanding balance. Taxes on the receipt are proportional to the amount paid. The invoice becomes paid when its balance reaches zero. An invoice with partial payments can no longer be canceled or replaced. PUE invoices are collected in a single payment.
- Receivables (dashboard, aging, cash forecast and Apolo IA) use the outstanding balance. In Bancos, a deposit smaller than the balance is suggested as a partial payment when its description or reference contains the invoice number.
- **Credit notes** (CFDI type E, relation 01) for returns or discounts. They reduce the invoice balance and record 402 Devoluciones against customers and VAT.
- **Withholdings.** When the company is an individual (13-character RFC) under regime 612/606 (ISR 10%) or 626 RESICO (ISR 1.25%) and invoices services (lines without a product) to a legal entity, the CFDI carries withholding of ISR and two thirds of the VAT. The total is already net and withholdings are recorded in 119/120. Invoices with withholdings are corrected by replacing them, not with credit notes.
- **Current mode: testing.** The UUID is generated locally and the seals read `PRUEBAS`, so the documents have no validity before the SAT. Real stamping is out of scope. Adding it would require the company's CSD (.cer/.key) and a PAC, and would change only `Cfdi.Stamp`.

## Payroll

- ISR is calculated with the monthly rate table of art. 96 adjusted to the days paid, minus the employment subsidy. Employee IMSS contributions are calculated on the SBC (integration factor by seniority, capped at 25 UMA).
- Default parameters are those of 2026 (rate table from Annex 8 of the RMF 2026, UMA $117.31, subsidy of 15.02% of the monthly UMA up to $11,492.66). Each company edits them in RRHH › Parámetros: rate table, UMA, Christmas bonus days, vacation premium, overtime factors, IMSS rates and the severance-age table. Values below the legal minimum are not accepted. **They must be updated every January.**
- **Overtime.** Double hours are paid at 200% and triple hours at 300% of the hourly rate (daily salary / 8). Half of double hours are exempt, up to 5 UMA per week. At minimum wage ($315.04), double hours are fully exempt.
- **Christmas bonus (*aguinaldo*).** Payroll of type aguinaldo: 15 days prorated to days worked in the year of the payment date, exempt up to 30 UMA.
- **Profit sharing (PTU).** Payroll of type PTU: the amount to distribute is split 50% by days worked and 50% by the previous year's wages, capped at three months' salary and exempt up to 15 UMA. ISR on aguinaldo and PTU is calculated by adding the taxable part to the monthly salary and taking the difference in the rate table. They do not incur IMSS contributions.
- **Employer contributions.** IMSS: fixed fee, surplus, cash benefits, pensioners, occupational risk, disability and life, and childcare. RCV: 2% retirement plus severance and old age with the 2026 table by wage level. INFONAVIT 5%. The occupational risk premium is entered in Configuración › Empresa; the default is 0.54355%, that of class I.
- When payroll is paid, employer contributions are recorded in 603 Cuotas patronales against 214 IMSS, RCV and INFONAVIT payable. PTU is recorded in 604.
- **Incidents.** Absences and sick leave deduct days of salary and contributions. Vacations pay a vacation premium (25% by default), exempt up to 15 UMA.
- At minimum wage, the employer absorbs the employee's IMSS contribution.
- PTU includes employees who left during the distributed year. The cap is the greater of three months' salary and the average PTU of the last three years.
- **Final settlement and severance (*finiquito y liquidación*).** Includes pending salary, prorated aguinaldo and vacations, and vacation premium. On unjustified dismissal it adds 3 months, 20 days per year and seniority premium, which is also paid at 15 or more years. The separation portion is exempt up to 90 UMA per year of service. Once paid, the employee is deactivated.
- **Annual calculation** (RRHH › Cálculo anual). Applies to employees with the full year and income up to $400,000. Compares the annual rate table against ISR withheld and applies the difference in a draft payroll.

## Banking

- Contabilidad › Bancos imports the bank statement as CSV. The columns are Fecha, Descripción, optional Referencia and Monto, or Cargo and Abono.
- Each movement suggests the document with the same amount, prioritizing the one with its number in the description or reference: invoices for deposits, purchase orders and payrolls for withdrawals.
- On reconciliation, the document is recorded as collected or paid on the bank's date and generates its journal entry. Charges without a document can be recorded as an expense.
