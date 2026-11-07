# Apolo ERP

[![CI](https://github.com/AndrishAraiza0412/apolo/actions/workflows/ci.yml/badge.svg)](https://github.com/AndrishAraiza0412/apolo/actions/workflows/ci.yml)

Diseño y alcance: [DESIGN.md](DESIGN.md). Stack: ASP.NET Core 10 + EF Core + PostgreSQL, React + Vite.

## Correr en local

```sh
docker compose up -d                     # Postgres 17 en :15432 (apolo/apolo)
cd backend
dotnet tool restore
dotnet run --project Apolo.Api           # http://localhost:5080 — en Development aplica migraciones y siembra datos demo
cd ../frontend && npm install && npm run dev   # http://localhost:5173 (proxy /api → :5080)
```

Usuarios demo (contraseña `apolo1234`): `gerencia@`, `ventas@`, `almacen@`, `finanzas@apolo.test`. Acceso rápido: `admin` / `admin` (Gerencia). Todo esto solo existe en Development.

Si no tienes .NET 10 global: `.dotnet/dotnet` (instalación local, ignorada por git).

## Producción

Imagen única (`Dockerfile`): la API sirve también la interfaz. En `deploy/` hay un compose con Postgres, la API, Caddy (HTTPS automático con Let's Encrypt) y un respaldo diario.

1. En un servidor con Docker, con el DNS del dominio apuntando a él: `cd deploy && cp .env.example .env`.
2. Llena `.env`: `DOMAIN`, `DB_PASSWORD` y `SETUP_TOKEN` (`openssl rand -base64 32`). `ANTHROPIC_API_KEY` es opcional y habilita Apolo IA.
3. Arranca con `docker compose up -d --build`. Las migraciones se aplican al arrancar.
4. Abre `https://DOMAIN`. La primera vez pide el token de instalación y da de alta la empresa y su usuario administrador (rol Gerencia, contraseña de 12+ caracteres con letras y números). Ya con una empresa, el alta queda cerrada.
5. Captura los datos fiscales, la prima de riesgo, los parámetros de nómina y el coeficiente de utilidad en Configuración, RRHH › Parámetros y Contabilidad › SAT y cierre. Después da de alta a los usuarios.

- En producción no hay datos demo ni usuario `admin/admin`; esos solo existen con `ASPNETCORE_ENVIRONMENT=Development`.
- La base no publica puertos. La cookie de sesión es `Secure`, `HttpOnly` y `SameSite=Strict`, y lleva HSTS. Las llaves que la cifran viven en el volumen `keys`.
- **Respaldos.** Diario, en `deploy/backups/apolo-AAAAMMDD-HHMMSS.dump`, conservando 30 días (`BACKUP_KEEP_DAYS`). Copia esa carpeta fuera del servidor (S3, otro disco). Para restaurar:
  `docker compose stop api && docker compose exec -T db pg_restore -U apolo -d apolo --clean --if-exists < backups/ARCHIVO.dump && docker compose start api`.
- **Actualizar:** `git pull && docker compose up -d --build`. Respalda antes: las migraciones son automáticas.
- **Seguridad** (detalle y mapeo ISO 27001 / SOC 2 en [docs/seguridad.md](docs/seguridad.md)):
  - Bloqueo de 15 minutos tras 5 intentos fallidos.
  - Contraseñas de 12+ caracteres con letras y números.
  - Las sesiones se revocan al cambiar la contraseña, el rol o al desactivar al usuario.
  - CSP y encabezados de seguridad.
  - Límite de 600 peticiones por minuto.
  - Bitácora de accesos.
  - `/healthz` para monitoreo.
  - Antes de cada versión corre `scripts/security-check.sh`.
- Para una sola instancia. Con varias, las migraciones y las tareas en segundo plano tienen que correr en un solo proceso.

## Tests

```sh
cd backend && dotnet test
```

## Nueva migración

```sh
cd backend && dotnet ef migrations add <Nombre> --project Apolo.Api
```

## Apolo IA

El chat usa Claude (`claude-opus-5-5`) con herramientas filtradas por el rol del usuario. Para activarlo, arranca la API con la clave en el entorno:

```bash
ANTHROPIC_API_KEY=sk-ant-... dotnet run --project Apolo.Api
```

Sin la clave, el resto del ERP funciona igual y la pantalla de IA avisa que falta configurarla. El agente nunca escribe directo: `crear_factura_borrador` y `crear_oc_borrador` dejan una propuesta en la cola de tareas, y solo una persona con permiso sobre el módulo la convierte en borrador (queda en la bitácora quién aprobó).

## Contabilidad

Cada operación genera su póliza en la misma transacción: factura emitida (clientes / ventas / IVA trasladado / costo de ventas), cobro, cancelación (póliza de reversa), compra recibida, pago a proveedor y nómina pagada. Las pólizas son inmutables; una póliza manual se corrige con su reversa. El catálogo base (101–605) se crea solo la primera vez. Las fechas de póliza son el día local del servidor.

- **Impuestos y cuotas** (Contabilidad › Impuestos y cuotas): saldos por enterar de retenciones (213), cuotas IMSS/RCV/INFONAVIT (214), IVA e ISR provisional. Retenciones y cuotas admiten pagos parciales. El IVA se compensa primero con el IVA que retuvieron los clientes (120) y luego con el acreditable (118). El IVA se toma devengado; la declaración del SAT va sobre flujo de efectivo.
- **ISR provisional (personas morales).** Ingresos acumulados × coeficiente de utilidad × 30%, menos los pagos provisionales del año (117) y el ISR que retuvieron los clientes (119). El coeficiente se captura en Contabilidad › SAT y cierre. Personas físicas y RESICO no se calculan.
- **Contabilidad electrónica.** Desde SAT y cierre se descargan el catálogo y la balanza del mes en XML (Anexo 24, versión 1.3), sin sellar. Cada cuenta con movimientos necesita su código agrupador: las del catálogo base lo traen y las demás se capturan en el catálogo de cuentas.
- **Cierre del ejercicio.** La póliza de cierre lleva ingresos y gastos a 305 Resultado del ejercicio y bloquea el periodo. No se acepta ninguna póliza con fecha igual o anterior al cierre (422). Solo se reabre el último ejercicio cerrado.
- **Proveedores.** Las órdenes recibidas aceptan abonos; la orden pasa a pagada cuando el saldo llega a cero.

## Pronósticos y tareas en segundo plano

- **Demanda** (`/api/forecast/demand`): promedio exponencial (α = 0.1) de las salidas de 90 días. Da días para llegar al mínimo y para agotarse, y una cantidad sugerida que cubre 30 días más el mínimo. Se ve en el detalle de Inventario y alimenta "Sugerir por pronóstico" en Compras.
- **Caja** (`/api/forecast/cash`): saldo de caja y bancos al cierre de los próximos 3 meses. Suma la cobranza al vencimiento, los pagos a proveedores, la nómina promedio y los promedios recientes de gastos y ventas. Los supuestos se muestran en Contabilidad › Pronóstico de caja; el Dashboard dibuja la proyección como línea punteada.
- **Tareas** (`/api/jobs`): la conciliación contable (7 revisiones) y los reportes corren en un worker dentro de la API. El avance queda guardado en `BackgroundJobs` y la interfaz lo consulta cada segundo mientras la tarea corre. Si la API se reinicia, retoma las tareas pendientes. Apolo IA puede lanzarlas con `tarea_en_segundo_plano`.
- Con un solo worker en proceso basta para una instancia. Con varias instancias, conviene pasar a una cola externa.

## Facturación electrónica (CFDI 4.0)

- Con RFC, razón social, régimen y C.P. en Configuración › Empresa, cada factura emitida se timbra como CFDI 4.0 de ingreso. Sin RFC, la factura queda como documento interno.
- El receptor necesita RFC, régimen fiscal y C.P., que se capturan en el cliente. Con el RFC genérico `XAXX010101000` se factura a público en general.
- Las facturas a crédito salen como PPD con forma de pago 99; las de contado, como PUE con forma de pago 03.
- Desde la factura se descarga el XML y se imprime la representación impresa, que lleva el QR de verificación del SAT. La cancelación pide el motivo del SAT (02, 03 o 04).
- **Sustituir** crea un borrador con el mismo contenido. Al emitirlo, el nuevo CFDI se relaciona con el original (tipo 04) y el original se cancela con motivo 01.
- Al registrar el cobro de una factura PPD timbrada se emite su recibo electrónico de pago (CFDI tipo P, complemento Pagos 2.0), serie P.
- Las facturas PPD aceptan **abonos**. Cada abono genera su póliza de ingreso y su recibo de pago, con número de parcialidad, saldo anterior y saldo insoluto. Los impuestos del recibo son proporcionales a lo pagado. La factura pasa a pagada cuando el saldo llega a cero. Una factura con abonos ya no se cancela ni se sustituye. Las PUE se cobran en una sola exhibición.
- La cartera (tablero, antigüedad de saldos, pronóstico de caja y Apolo IA) usa el saldo pendiente. En Bancos, un depósito menor al saldo se sugiere como abono cuando trae el folio en la descripción o en la referencia.
- **Notas de crédito** (CFDI tipo E, relación 01) por devolución o descuento. Reducen el saldo de la factura y registran 402 Devoluciones contra clientes e IVA.
- **Retenciones.** Cuando la empresa es persona física (RFC de 13) en régimen 612/606 (ISR 10%) o 626 RESICO (ISR 1.25%) y factura servicios (partidas sin producto) a una persona moral, el CFDI lleva retención de ISR y de dos terceras partes del IVA. El total ya viene neto y las retenciones se registran en 119/120. Las facturas con retenciones se corrigen sustituyéndolas, no con notas de crédito.
- **Modo actual: pruebas.** El UUID se genera localmente y los sellos dicen `PRUEBAS`, así que los comprobantes no tienen validez ante el SAT. El timbrado real queda fuera del alcance. Para agregarlo harían falta el CSD de la empresa (.cer/.key) y un PAC, y solo cambiaría `Cfdi.Stamp`.

## Nómina

- El ISR se calcula con la tarifa mensual del art. 96 ajustada a los días pagados, y se le resta el subsidio para el empleo. Las cuotas obreras del IMSS se calculan sobre el SBC (factor de integración por antigüedad, con tope de 25 UMA).
- Los parámetros por omisión son los de 2026 (tarifa del Anexo 8 de la RMF 2026, UMA $117.31, subsidio de 15.02% de la UMA mensual hasta $11,492.66). Cada empresa los edita en RRHH › Parámetros: tarifa, UMA, días de aguinaldo, prima vacacional, factores de horas extra, tasas IMSS y tabla de cesantía. No se aceptan valores por debajo de la ley. **Hay que actualizarlos cada enero.**
- **Horas extra.** Las dobles se pagan al 200% y las triples al 300% de la hora (sueldo diario / 8). Exenta la mitad de las dobles, hasta 5 UMA por semana. En salario mínimo ($315.04) las dobles son exentas completas.
- **Aguinaldo.** Nómina de tipo aguinaldo: 15 días proporcionales a los días trabajados en el año de la fecha de pago, exento hasta 30 UMA.
- **PTU.** Nómina de tipo PTU: el monto a repartir se divide 50% por días trabajados y 50% por salarios del año anterior, con tope de tres meses de salario y exento hasta 15 UMA. El ISR de aguinaldo y PTU se calcula sumando la parte gravada al sueldo mensual y tomando la diferencia de la tarifa. No causan cuotas IMSS.
- **Cuotas patronales.** IMSS: cuota fija, excedente, prestaciones en dinero, pensionados, riesgo de trabajo, invalidez y vida, y guarderías. RCV: retiro 2% más cesantía y vejez con la tabla 2026 por nivel salarial. INFONAVIT 5%. La prima de riesgo de trabajo se captura en Configuración › Empresa; el valor por omisión es 0.54355%, la de clase I.
- Al pagar la nómina, las cuotas patronales se registran en 603 Cuotas patronales contra 214 IMSS, RCV e INFONAVIT por pagar. La PTU se registra en 604.
- **Incidencias.** Las faltas y las incapacidades descuentan días de sueldo y de cotización. Las vacaciones pagan prima vacacional (25% por omisión), exenta hasta 15 UMA.
- En salario mínimo, el patrón absorbe la cuota obrera del IMSS.
- La PTU incluye a los empleados dados de baja en el año repartido. El tope es el mayor entre tres meses de salario y el promedio de PTU de los últimos tres años.
- **Finiquito y liquidación.** Incluye salario pendiente, aguinaldo y vacaciones proporcionales y prima vacacional. En despido injustificado suma 3 meses, 20 días por año y prima de antigüedad, que también se paga con 15 años o más. La parte de separación está exenta hasta 90 UMA por año de servicio. Al pagarlo, el empleado queda dado de baja.
- **Cálculo anual** (RRHH › Cálculo anual). Aplica a empleados con el año completo e ingresos de hasta $400,000. Compara la tarifa anual contra el ISR retenido y aplica la diferencia en una nómina en borrador.

## Bancos

- En Contabilidad › Bancos se importa el estado de cuenta en CSV. Las columnas son Fecha, Descripción, Referencia opcional y Monto, o bien Cargo y Abono.
- Cada movimiento sugiere el documento con el mismo importe, priorizando el que tenga su folio en la descripción o la referencia: facturas para depósitos, órdenes de compra y nóminas para retiros.
- Al conciliar, el documento se registra como cobrado o pagado con la fecha del banco y genera su póliza. Los cargos sin documento se pueden registrar como gasto.
