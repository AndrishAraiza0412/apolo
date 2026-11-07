# Apolo ERP — Design document

> **Note:** this is the original design document, written before implementation. Where it differs from the code, such as the undecided stack in section 10 or the scope in section 12, the [README](README.md) describes the current state.

Visual source: `ERP v2.dc.html` (prototype). This document is the reference for starting development.

## 1. Product

- **Name:** Apolo. A multi-module ERP system with an integrated AI agent, **Apolo IA**.
- **Primary user:** teams with different roles (management, sales, warehouse, finance).
- **Language:** Spanish (Mexico). Currency in MXN unless another is configured.

## 2. Modules

| Module | Menu group | Roles with access (prototype) |
|---|---|---|
| Dashboard | General | All |
| Apolo IA (copilot) | General | All |
| Sales / CRM | Operations | Management, Sales |
| Invoicing | Operations | Management, Sales, Finance |
| Inventory | Operations | Management, Warehouse |
| Purchasing | Operations | Management, Warehouse, Finance |
| Projects | Operations | Management, Sales |
| Accounting | Finance | Management, Finance |
| HR / Payroll | Finance | Management, Finance |
| Reports | System | Management, Sales, Warehouse, Finance |
| Settings | System | Management |

Permissions are enforced in the backend. The menu only reflects them.

## 3. Roles

- **Management (Gerencia):** full access.
- **Sales (Ventas)**, **Warehouse (Almacén)**, **Finance (Finanzas):** see only their modules.
- In the prototype, the "View as role" selector in the sidebar switches roles for testing. It is removed in production.

## 4. Apolo IA

**Where it lives**
- Side panel (drawer) from any module, with a floating "Ask the AI" button.
- Full "Apolo IA" screen with two tabs: *Conversation* and *Task queue*.
- Daily briefing on the Dashboard: pending items with an action button.
- Suggestions inside each module (for example, a forecast when selecting an inventory row).

**What it can do**
- Answer questions about data available to the user's role.
- Prepare drafts: invoices, purchase orders, collection reminders, reports.
- Explain forecasts (a statistical service does the calculation, not the language model).
- Run background tasks (reports, reconciliations) with visible progress.

**Rules**
1. The agent never writes directly. Every draft requires human approval.
2. Tasks that move money (payments, payroll, invoices, purchase orders) always wait for approval.
3. The agent sees only the tools allowed by the user's role.
4. Each agent action is recorded in the audit log with the user who approved it.
5. If a query has no data or the role lacks permission, the agent says so; it does not invent figures.

**Planned tools (tool use)**
`consultar_ventas`, `consultar_cartera`, `consultar_stock`, `crear_factura_borrador`, `crear_oc_borrador`, `generar_reporte`, `pronostico_demanda`, `pronostico_caja`.

## 5. Visual identity

**Color**
- Primary (burgundy): `#8a1538` (light) / `#b8325c` (dark). Hover and strong accent: `#b02a55` / `#d4567e`. Pressed: `#6b0f2b` / `#f29bb5`.
- Warm neutrals: text `#201e1d`, secondary `#605d5d`, borders `#d7d3d3`, background `#f3f2f2`, card `#ffffff`.
- States: success = solid black (`#201e1d`); error and overdue = burgundy; pending = gray.
- All variables are defined in `:root` and in the dark theme (see section 7).

**Typography**
- Family: DM Sans (prototype). Weights 400, 500, 600, 700.
- Section titles: 13px, uppercase, burgundy, .03em spacing.
- Screen title: 24px, weight 600.
- Indicators (KPI): 32px, weight 500.
- Table text: 14px. Secondary text: 12px in medium gray.
- Minimum text size: 12px.

**Shape**
- Cards: white background, 6px radius, soft shadow (`0 1px 2px` + `0 6px 18px`).
- Buttons: 6px radius. Pills (999px) for status labels and chips.
- 1px dividers.

**Spacing**
- Multiples of 4px. Gap between cards: 20px. Card padding: 16–18px.

## 6. Components and animations

**Action button** (`ActionButton`)
- States: `idle` → `loading` → `done`.
- `idle`: burgundy background (or burgundy border if secondary), white text.
- Hover: rises 1px and the shadow grows.
- Pressed: scales to 0.95.
- `loading`: 3px progress bar filling left to right in 1.3 s; the text changes (e.g. "Creando OC…").
- `done`: text with a ✓ that appears with a bounce (0.45 s); the color turns black.
- A repeated click on `done` does nothing.

**Navigation button**
- Background fills with burgundy in a horizontal sweep (0.35 s) on hover.

**Copilot floating button**
- Ring pulses 3 times on load.
- Background sweep on hover.

**KPI card**
- Enters with a 10px vertical offset and opacity; each with a 70 ms delay.
- Hover: rises 3px.

**Charts**
- Sales bars: grow from the base with a soft bounce (0.8 s, staggered 40 ms).
- Collections donut: rotates on load (0.9 s).
- Cash flow line: draws on load (1.6 s). Forecast as a dotted line.
- Forecast popover: 14-day bars grow staggered; a dotted red line marks the minimum.

**Agent task cards**
- Appear with a soft bounce. The progress bar has a 0.3 s linear transition.

**Notifications**
- Black toast in the bottom-left corner, enters from below (0.35 s), hides after 3.2 s.

**Table**
- Row: very light gray background on hover.
- Selected row: 3px burgundy left stripe and faint burgundy background.
- HR and report rows enter staggered (0.35 s).

**Accessibility**
- Respect `prefers-reduced-motion`: turn off bounces, pulses and sweeps; keep color and text changes.
- Visible focus: 2px burgundy outline.
- Minimum text contrast 4.5:1. In dark mode, the burgundy lightens to meet it.

## 7. Dark mode

- Turned on from the moon/sun icon in the top bar.
- The preference is saved per user (in production, in the profile; in the prototype, in `localStorage`).
- CSS variables are applied on `document.documentElement`; the value is that of the dark theme in section 5.

## 8. Screen design

**Layout**
- 60px top bar: logo, screen title, quick-access icons, user.
- 250px side menu with groups (General, Operations, Finance, System).
- Main area with 20–24px padding and its own scroll.

**Screen patterns**
- *List/table:* header with title, subtitle, search filter and primary AI button on the right; table inside a card.
- *Dashboard:* briefing + KPI row + charts + agent queue.
- *Copilot screen:* conversation (left) and queried data and permissions (right).

**Platform**
- Desktop only (minimum 1280px). There is no mobile version.

## 9. Data (initial model)

Core entities: Company, User, Role, Permission, Customer, Product (SKU), Inventory movement, Invoice, Invoice line, Purchase order, Supplier, Opportunity, Ledger account, Journal entry, Employee, Payroll period, Agent task, Agent action (audit log), Report.

Data rules:
- All tables have `created_at`, `updated_at`, `created_by`.
- Invoice and purchase order numbers are generated on the server with a per-company sequence.
- Inventory balances are calculated from movements, not stored as an editable number.

## 10. Proposed stack

Pending decision: **Next.js + TypeScript** or **.NET (ASP.NET Core + Blazor or React)**.
Database: PostgreSQL. Task queue: Redis + BullMQ (Node) or Hangfire (.NET).
Agent: Claude API with tool use.

## 11. Phases

1. Authentication, roles, base data model and audit log.
2. Inventory, Sales and Invoicing with AI drafts (read-only first).
3. Purchasing and approval of agent actions.
4. Accounting, HR and Reports.
5. Forecasting and background tasks.

## 12. Out of scope for now

- Real CFDI stamping with a PAC and CSD (today: CFDI 4.0 in test mode).
- Payroll: exempt earnings, overtime, Christmas bonus/profit sharing, employer contributions and annual adjustment (today: income tax, subsidy and employee IMSS).
- Direct bank connection (today: CSV statement import and reconciliation).

## 13. Open questions

- Final stack: Next.js or .NET?
- Electronic invoicing for which country and provider?
- Multi-company from the start or a single company?
- What real roles and permissions does your operation have?
