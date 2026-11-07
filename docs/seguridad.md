# Information security — Apolo ERP

This document is a basis for operating Apolo with controls aligned to **ISO/IEC 27001:2022** (27000 family) and the **SOC 2 Trust Services Criteria** (security, availability and confidentiality). It includes:

- the statement of applicability (SoA);
- the SOC 2 mapping;
- the minimum operating policies.

> The software is not "certified". ISO 27001 certification and the SOC 2 report are granted to the **organization that operates the system**, after an external audit. Apolo provides the technical controls. The company operating it provides the organizational controls marked here as **Org**: owners, approved policies, periodic evidence.

Status of each control:

- **System**: implemented in Apolo and verifiable in the code or the deployment.
- **Org**: met by the organization with its own process (a template is given here).
- **Partial**: technical support exists, but a complement, stated in each case, is missing.

## 1. Scope and assets

- **Scope:** the Apolo application (.NET API and React SPA), its PostgreSQL database, the Caddy proxy, backups and session keys, deployed with `deploy/docker-compose.yml`.
- **Information handled:**
  - tax data: RFC, CFDI and accounting;
  - employees' personal data: salary, RFC/CURP if entered, payroll and final settlements;
  - customer and supplier data.
  - These are subject to the **LFPDPPP** (Mexican data protection law) and the **CFF** (Federal Tax Code; accounting records kept for 5 years, art. 30).
- **Critical assets:**
  - `pgdata` volume (database);
  - `deploy/backups/`;
  - `keys` volume (keys that encrypt the session cookie);
  - `deploy/.env` (database password, setup token, Anthropic key).

## 2. Statement of applicability (ISO 27001:2022, Annex A)

### A.5 Organizational controls

| Control | Status | How it is met / evidence |
|---|---|---|
| 5.1 Security policies | Org | Adopt section 4 of this document, approved by management and reviewed annually. |
| 5.2 Roles and responsibilities | Org | Appoint a security owner and an Apolo administrator (Gerencia role). |
| 5.3 Segregation of duties | System | Roles by module: Gerencia, Ventas, Almacén and Finanzas. Authorization policies are enforced on the server (`Modules.cs`, `Program.cs`), not just in the menu. Automatic journal entries are not reversed by hand: the source document is canceled. |
| 5.9 Asset inventory | Org | Section 1, plus the organization's inventory of servers and accounts. |
| 5.12 Information classification | Org | Payroll and personal data: confidential. Accounting: confidential and subject to legal retention. |
| 5.15 Access control | System | Mandatory authentication. Each module requires its permission and each company sees only its own data (tenant filter in `AppDb`, plus a check on save that prevents writing to another company). |
| 5.16 Identity management | System | One user per person, identified by a unique email. No shared accounts in production: `admin/admin` exists only in development. |
| 5.17 Authentication information | System | Passwords hashed with PBKDF2 (ASP.NET Identity) and automatic rehash. Policy of 12 or more characters with letters and numbers. Password change verifies the current one (`PUT /api/me/password`). |
| 5.18 Access rights | System + Org | Creation, deactivation, role change and unlock with `PUT /api/users/{id}`. A deactivation, role change, change to a role's modules or password change instantly invalidates open sessions (session fingerprint checked on each request). **Org:** quarterly review of active users and deactivation on the day an employee leaves. |
| 5.23 Cloud services | Org | If Apolo IA is used, query data travels to Anthropic. Evaluate the data processing agreement or leave `ANTHROPIC_API_KEY` empty. |
| 5.24–5.28 Incident management | Org | Procedure in §4.6. Evidence: audit log and JSON logs. |
| 5.29–5.30 Continuity / ICT for continuity | Partial | Daily backup and restore documented (README › Production). **Missing (Org):** off-server copy and a recorded quarterly restore test. |
| 5.31 Legal requirements | Org | CFF art. 30 (keep for 5 years; do not purge annual backups). LFPDPPP (privacy notice to employees and customers). |
| 5.33 Protection of records | System | Journal entries are immutable: they are corrected with a reversal. The fiscal year close locks the period. CFDIs keep their XML. |
| 5.34 Privacy and personal data | Partial | Access to payroll and HR only with the `rrhh` permission. **Org:** privacy notice and handling of ARCO rights (access, rectification, cancellation, objection). |

### A.6 People controls

| Control | Status | How it is met |
|---|---|---|
| 6.1–6.6 Screening, terms, awareness, confidentiality, termination | Org | Confidentiality clause in the contract. Annual security induction. Termination checklist: deactivate the user in Apolo, which closes their sessions. |
| 6.8 Event reporting | Org | A single channel (the security owner's email) for reporting incidents. |

### A.7 Physical controls

| Control | Status | How it is met |
|---|---|---|
| 7.x | Org | Inherited from the cloud or data center provider. Keep their SOC 2 or ISO 27001 report as evidence. |

### A.8 Technological controls

| Control | Status | How it is met / evidence |
|---|---|---|
| 8.2 Privileged access | System + Org | Only Gerencia has the Configuración module, where users are managed. Nobody can remove their own role or deactivate themselves. **Org:** at least two administrators and SSH access to the server by key only. |
| 8.3 Access restriction | System | Per-module authorization on each endpoint and isolation by company. |
| 8.5 Secure authentication | System | Error messages that do not reveal whether the email exists, with constant time (decoy hash). 15-minute lockout after 5 failed attempts, plus a limit of 10 attempts per minute per IP. `HttpOnly`, `Secure` and `SameSite=Strict` cookie, with 8-hour expiration and renewal on use. **Pending:** second factor (TOTP) if the organization requires it. |
| 8.6 Capacity management | System | General limit of 600 requests per minute per user or IP. Heavy tasks run in the background. |
| 8.7 Protection against malware | Org | Antivirus/EDR on workstations. Server with updated official images. |
| 8.8 Technical vulnerabilities | System | `scripts/security-check.sh` runs the tests, looks for vulnerable NuGet packages (including transitive ones), runs a production `npm audit` and searches for secrets. Run it before each release and monthly. Current status: 0 vulnerabilities in production. Moderate npm alerts come from development tools (eslint). |
| 8.9 Configuration | System | Configuration through environment variables: no secrets in the repository, other than the local development database. The database publishes no ports. The container runs as an unprivileged user. |
| 8.10 Information deletion | Org | Backup retention (`BACKUP_KEEP_DAYS`). To comply with the CFF, also keep one closing backup per fiscal year for 5 years. |
| 8.12 Data leakage prevention | Partial | `/api` responses carry `Cache-Control: no-store`. CSV/XML exports require the module's permission. |
| 8.13 Backup | System | Daily compressed `pg_dump` with rotation (`deploy/backup.sh`). Restore tested. **Org:** encrypted off-server copy. |
| 8.15 Logging | System | The audit log records each creation, change or deletion with user, date and fields (passwords are masked). It also records logins, failed logins, lockouts and password changes. It is viewed in Configuración › Auditoría. Application logs go out as JSON on the console. |
| 8.16 Monitoring | Partial | `/healthz` serves an external availability monitor. **Org:** alerts for repeated failed logins (query the audit log or the log collector). |
| 8.20–8.21 Network security | System | Only 80 and 443 are exposed (Caddy). The API and database stay on the internal Docker network. |
| 8.23 Web filtering / browser | System | Strict CSP: own scripts plus the hash of the theme script, no `unsafe-eval`, `frame-ancestors 'none'` and `object-src 'none'`. Also `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` and `Permissions-Policy`. |
| 8.24 Cryptography | System | TLS 1.2+ with automatic certificates (Caddy and Let's Encrypt) and HSTS. Passwords with PBKDF2. Cookie encrypted with ASP.NET Data Protection, whose keys persist in the `keys` volume. **Org:** disk encryption for the server and backups. |
| 8.25–8.29 Secure development and testing | System | Automated tests of tax, accounting and security calculations (`Apolo.Tests`). Input validation on each endpoint. Parameterized queries: EF Core and interpolated SQL with parameters. CSRF protection through `SameSite=Strict` plus JSON bodies. |
| 8.31 Separation of environments | System | Development (`Development`: demo data, `admin/admin`) and production (`Production`: no demo, sign-up with token) are separated by configuration. |
| 8.32 Change management | Org + System | Versioned migrations in `Migrations/`. Procedure in §4.5. |
| 8.34 Protection during audits | Org | Audits use a read-only user created with a limited role. |

## 3. SOC 2 mapping (Trust Services Criteria 2017, rev. 2022)

| Criterion | Apolo controls |
|---|---|
| CC1 Control environment | Org: §4.1, roles and owners (A.5.2) |
| CC2 Communication and information | Org: published policies, privacy notice. System: queryable audit log. |
| CC3 Risk assessment | §5, risk register, reviewed annually. |
| CC4 Monitoring of controls | Monthly `scripts/security-check.sh`. Quarterly review of users and restores. |
| CC5 Control activities | Policies in §4. Segregation by roles (A.5.3). |
| CC6.1 Logical access | Authentication, roles by module, isolation by company, secure cookie. |
| CC6.2–6.3 Access provisioning, removal and changes | `/api/users`, immediate session revocation, quarterly review. |
| CC6.6 External threats | TLS, HSTS, CSP, request limits, account lockout. |
| CC6.7 Transmission | TLS end to end up to the proxy. Internal Docker network. |
| CC6.8 Malicious software | Official images, `security-check.sh`, EDR on workstations (Org). |
| CC7.1 Vulnerability detection | `security-check.sh` (NuGet and npm). |
| CC7.2–7.3 Anomaly monitoring | Audit log (failed logins, lockouts), JSON logs, `/healthz`. |
| CC7.4–7.5 Response and recovery | §4.6 incidents. Restore from backup. |
| CC8.1 Change management | §4.5, versioned migrations, tests before deploying. |
| CC9 Risk and vendor mitigation | §5. Vendor assessment: cloud, Anthropic, future PAC. |
| A1 Availability | Automatic container restart, daily backup, `/healthz`, RPO of 24 h and RTO of 4 h (§4.4). |
| C1 Confidentiality | Permissions by module, encryption in transit, retention and deletion (A.8.10). |
| PI1 Processing integrity | Balanced journal entries by construction and automatic 7-check reconciliation (Contabilidad › Conciliación). Immutability and period close. Consecutive numbers assigned on the server with a row lock. |

## 4. Operating policies (templates for approval)

### 4.1 Governance

- Management approves this document.
- The security owner reviews it every year or after an incident or major change.
- Exceptions are documented with an expiration date.

### 4.2 Access control

- Least privilege: each person gets the role with the modules they need.
- Gerencia is limited to management and 1 or 2 administrators.
- **Creation:** request approved by the area's manager.
- **Deactivation:** on the day of departure, by deactivating the user.
- **Review:** each quarter, the user list is exported and its validity signed off.
- **Passwords:** 12 or more characters with letters and numbers, never shared. Changed on suspicion of compromise. An administrator unlocks accounts after verifying identity.

### 4.3 Logging and monitoring

- The audit log is not deleted. It is kept for at least 5 years along with the accounting records.
- `acceso fallido` (failed login) and `bloqueo` (lockout) events are reviewed each week.
- `/healthz` is monitored every 5 minutes with an alert to the owner.

### 4.4 Backup and continuity

- **RPO:** 24 hours, from the daily backup. **RTO:** 4 hours.
- Daily copy of `deploy/backups/` to encrypted external storage.
- Quarterly restore test on a separate server, recorded with date, file, duration and result.
- The `keys` volume and `deploy/.env` are backed up separately, in a secrets vault.

### 4.5 Change management

1. The change is developed and tested in development.
2. `scripts/security-check.sh` passes without errors.
3. Production is backed up.
4. `docker compose up -d --build` is run.
5. `/healthz` is checked and the basics are walked through: log in, open the dashboard, issue a draft.

- Each release leaves a record: date, who, what changed and the result.
- Database migrations are never edited after being published.

### 4.6 Incident response

1. **Detect and report** to the security owner.
2. **Contain:**
   - deactivate compromised users, which closes their sessions;
   - rotate `DB_PASSWORD`, `ANTHROPIC_API_KEY` and, if needed, delete the `keys` volume to close all sessions.
3. **Analyze** with the audit log and logs.
4. **Recover** from backup if there was tampering.
5. **Notify:** if personal data was affected, inform the data subjects under the LFPDPPP.
6. **Lessons learned** in a signed record within the following 10 business days.

### 4.7 Vulnerability management

- `scripts/security-check.sh` runs each month and before each release.
- Fix deadlines: critical in 7 days, high in 30, moderate in 90.
- Alerts from development tools that do not reach production are accepted and documented.

## 5. Risk register (summary)

| Risk | Impact | Treatment | Residual |
|---|---|---|---|
| Credential theft | High | Lockout, limits, password policy and session revocation. Pending: 2FA. | Medium |
| Database loss | High | Daily backup, external copy (Org) and restore test. | Low |
| Unauthorized payroll access | High | `rrhh` permission and quarterly review. | Low |
| Data leak to a third party (AI) | Medium | Optional AI, vendor assessment and empty key if not approved. | Low |
| Tax error in calculations | Medium | Automated tests, editable parameters and accountant review. | Medium |
| Tampering with accounting records | High | Immutability, period close and audit log. | Low |
| Vulnerable dependency | Medium | Monthly `security-check.sh`. | Low |
| Compromised server | High | Unprivileged container, database with no exposed ports, SSH by key (Org) and operating system patches (Org). | Medium |

## 6. Recommended follow-ups (outside current scope)

- Second authentication factor (TOTP) for Gerencia.
- Encryption of the Data Protection keys with a certificate or a KMS.
- Log shipping to a SIEM with automatic alerts for failed logins.
- Backups encrypted with `age` or `gpg` before leaving the server.
- External penetration test before the first production customer.
