# Apolo ERP — Documento de diseño

Fuente visual: `ERP v2.dc.html` (prototipo). Este documento es la referencia para empezar a programar.

## 1. Producto

- **Nombre:** Apolo. Sistema ERP multi-módulo con agente de IA integrado, **Apolo IA**.
- **Usuario principal:** equipos con distintos roles (gerencia, ventas, almacén, finanzas).
- **Idioma:** español (México). Moneda en MXN salvo que se configure otra.

## 2. Módulos

| Módulo | Grupo de menú | Roles con acceso (prototipo) |
|---|---|---|
| Dashboard | General | Todos |
| Apolo IA (copiloto) | General | Todos |
| Ventas / CRM | Operación | Gerencia, Ventas |
| Facturación | Operación | Gerencia, Ventas, Finanzas |
| Inventario | Operación | Gerencia, Almacén |
| Compras | Operación | Gerencia, Almacén, Finanzas |
| Proyectos | Operación | Gerencia, Ventas |
| Contabilidad | Finanzas | Gerencia, Finanzas |
| RR.HH. / Nómina | Finanzas | Gerencia, Finanzas |
| Reportes | Sistema | Gerencia, Ventas, Almacén, Finanzas |
| Configuración | Sistema | Gerencia |

Los permisos se aplican en backend. El menú solo los refleja.

## 3. Roles

- **Gerencia:** acceso total.
- **Ventas**, **Almacén**, **Finanzas:** ven solo sus módulos.
- En el prototipo, el selector "Ver como rol" de la barra lateral cambia de rol para pruebas. En producción se elimina.

## 4. Apolo IA

**Dónde vive**
- Panel lateral (drawer) desde cualquier módulo, con botón flotante "Preguntar a la IA".
- Pantalla completa "Apolo IA" con dos pestañas: *Conversación* y *Cola de tareas*.
- Briefing diario en el Dashboard: pendientes con botón de acción.
- Sugerencias dentro de cada módulo (por ejemplo, pronóstico al seleccionar una fila de inventario).

**Qué puede hacer**
- Responder preguntas sobre datos del rol del usuario.
- Preparar borradores: facturas, órdenes de compra, recordatorios de cobro, reportes.
- Explicar pronósticos (el cálculo lo hace un servicio estadístico, no el modelo de lenguaje).
- Ejecutar tareas en segundo plano (reportes, conciliaciones) con avance visible.

**Reglas**
1. El agente nunca escribe directo. Todo borrador requiere aprobación humana.
2. Las tareas que mueven dinero (pagos, nómina, facturas, órdenes de compra) siempre esperan aprobación.
3. El agente solo ve las herramientas permitidas por el rol del usuario.
4. Cada acción del agente queda registrada en la bitácora de auditoría con el usuario que la aprobó.
5. Si la consulta no tiene datos o el rol no tiene permiso, el agente lo dice; no inventa cifras.

**Herramientas previstas (tool use)**
`consultar_ventas`, `consultar_cartera`, `consultar_stock`, `crear_factura_borrador`, `crear_oc_borrador`, `generar_reporte`, `pronostico_demanda`, `pronostico_caja`.

## 5. Identidad visual

**Color**
- Principal (guinda): `#8a1538` (claro) / `#b8325c` (oscuro). Hover y acento fuerte: `#b02a55` / `#d4567e`. Presionado: `#6b0f2b` / `#f29bb5`.
- Neutros cálidos: texto `#201e1d`, secundario `#605d5d`, bordes `#d7d3d3`, fondo `#f3f2f2`, tarjeta `#ffffff`.
- Estados: éxito = negro sólido (`#201e1d`); error y vencido = guinda; pendiente = gris.
- Todas las variables están definidas en `:root` y en el tema oscuro (ver sección 7).

**Tipografía**
- Familia: DM Sans (prototipo). Pesos 400, 500, 600, 700.
- Títulos de sección: 13px, mayúsculas, guinda, espaciado .03em.
- Título de pantalla: 24px, peso 600.
- Indicadores (KPI): 32px, peso 500.
- Texto de tabla: 14px. Texto secundario: 12px en gris medio.
- Tamaño mínimo de texto: 12px.

**Forma**
- Tarjetas: fondo blanco, radio 6px, sombra suave (`0 1px 2px` + `0 6px 18px`).
- Botones: radio 6px. Pastillas (999px) para etiquetas de estado y chips.
- Divisores de 1px.

**Espaciado**
- Múltiplos de 4px. Gap entre tarjetas: 20px. Padding de tarjeta: 16–18px.

## 6. Componentes y animaciones

**Botón de acción** (`ActionButton`)
- Estados: `idle` → `loading` → `done`.
- `idle`: fondo guinda (o borde guinda si es secundario), texto blanco.
- Hover: se eleva 1px y la sombra crece.
- Presionado: escala a 0.95.
- `loading`: barra de progreso de 3px que se llena de izquierda a derecha en 1.3 s; el texto cambia (ej. "Creando OC…").
- `done`: texto con ✓ que aparece con rebote (0.45 s); el color pasa a negro.
- Un clic repetido en `done` no hace nada.

**Botón de navegación**
- Fondo que se llena de guinda con barrido horizontal (0.35 s) al pasar el cursor.

**Botón flotante del copiloto**
- Pulso de anillo 3 veces al cargar.
- Barrido de fondo al hover.

**Tarjeta KPI**
- Entra con desplazamiento vertical de 10px y opacidad; cada una con retraso de 70 ms.
- Hover: se eleva 3px.

**Gráficas**
- Barras de ventas: crecen desde la base con rebote suave (0.8 s, escalonadas 40 ms).
- Dona de cobranza: gira al cargar (0.9 s).
- Línea de flujo de caja: se dibuja al cargar (1.6 s). Pronóstico con línea punteada.
- Pronóstico en popover: barras de 14 días crecen escalonadas; línea roja punteada marca el mínimo.

**Tarjetas de tareas del agente**
- Aparecen con rebote suave. La barra de avance tiene 0.3 s de transición lineal.

**Notificaciones**
- Toast negro en la esquina inferior izquierda, entra desde abajo (0.35 s), se oculta a los 3.2 s.

**Tabla**
- Fila: al pasar el cursor, fondo gris muy claro.
- Fila seleccionada: franja izquierda de 3px guinda y fondo guinda tenue.
- Filas de RR.HH. y reportes entran escalonadas (0.35 s).

**Accesibilidad**
- Respetar `prefers-reduced-motion`: desactivar rebotes, pulsos y barridos; mantener cambios de color y texto.
- Foco visible: contorno de 2px guinda.
- Contraste de texto mínimo 4.5:1. En modo oscuro, el guinda se aclara para cumplirlo.

## 7. Modo oscuro

- Se activa desde el ícono de luna/sol en la barra superior.
- Se guarda la preferencia por usuario (en producción, en el perfil; en el prototipo, en `localStorage`).
- Se aplican variables CSS en `document.documentElement`; el valor es el del tema oscuro de la sección 5.

## 8. Diseño de pantallas

**Layout**
- Barra superior de 60px: logo, título de la pantalla, íconos de acceso rápido, usuario.
- Menú lateral de 250px con grupos (General, Operación, Finanzas, Sistema).
- Área principal con padding 20–24px y scroll propio.

**Patrones de pantalla**
- *Lista/tabla:* encabezado con título, subtítulo, filtro de búsqueda y botón principal con IA a la derecha; tabla dentro de tarjeta.
- *Dashboard:* briefing + fila de KPIs + gráficas + cola del agente.
- *Pantalla del copiloto:* conversación (izquierda) y datos consultados y permisos (derecha).

**Plataforma**
- Exclusivo de escritorio (mínimo 1280px). No hay versión móvil.

## 9. Datos (modelo inicial)

Entidades núcleo: Empresa, Usuario, Rol, Permiso, Cliente, Producto (SKU), Movimiento de inventario, Factura, Partida de factura, Orden de compra, Proveedor, Oportunidad, Cuenta contable, Póliza, Empleado, Periodo de nómina, Tarea del agente, Acción del agente (bitácora de auditoría), Reporte.

Reglas de datos:
- Todas las tablas con `created_at`, `updated_at`, `created_by`.
- Los folios de factura y OC se generan en servidor con secuencia por empresa.
- Los saldos de inventario se calculan desde movimientos, no se guardan como número editable.

## 10. Stack propuesto

Pendiente de decisión: **Next.js + TypeScript** o **.NET (ASP.NET Core + Blazor o React)**.
Base de datos: PostgreSQL. Cola de tareas: Redis + BullMQ (Node) o Hangfire (.NET).
Agente: API de Claude con tool use.

## 11. Fases

1. Autenticación, roles, modelo de datos base y bitácora.
2. Inventario, Ventas y Facturación con borradores de IA (solo lectura primero).
3. Compras y aprobación de acciones del agente.
4. Contabilidad, RR.HH. y Reportes.
5. Pronósticos y tareas en segundo plano.

## 12. Fuera de alcance por ahora

- Timbrado real de CFDI con PAC y CSD (hoy: CFDI 4.0 en modo pruebas).
- Nómina: percepciones exentas, horas extra, aguinaldo/PTU, cuotas patronales y ajuste anual (hoy: ISR, subsidio e IMSS obrero).
- Conexión directa con bancos (hoy: importación de estado de cuenta en CSV y conciliación).

## 13. Preguntas abiertas

- ¿Stack final: Next.js o .NET?
- ¿Facturación electrónica con qué país y proveedor?
- ¿Multiempresa desde el inicio o una sola empresa?
- ¿Qué roles y permisos reales tiene tu operación?
