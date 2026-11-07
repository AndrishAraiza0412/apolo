# Seguridad de la información — Apolo ERP

Este documento sirve de base para operar Apolo con controles alineados a **ISO/IEC 27001:2022** (familia 27000) y a los **Trust Services Criteria de SOC 2** (seguridad, disponibilidad y confidencialidad). Incluye:

- la declaración de aplicabilidad (SoA);
- el mapeo SOC 2;
- las políticas operativas mínimas.

> El software no "está certificado". La certificación ISO 27001 y el informe SOC 2 se otorgan a la **organización que opera el sistema**, después de una auditoría externa. Apolo aporta los controles técnicos. La empresa que lo opera aporta los controles organizacionales que aquí se marcan como **Org**: responsables, políticas aprobadas, evidencia periódica.

Estado de cada control:

- **Sistema**: implementado en Apolo y verificable en el código o en el despliegue.
- **Org**: lo cumple la organización con un proceso propio (aquí se da la plantilla).
- **Parcial**: hay soporte técnico, pero falta un complemento que se indica.

## 1. Alcance y activos

- **Alcance:** la aplicación Apolo (API .NET y SPA React), su base PostgreSQL, el proxy Caddy, los respaldos y las llaves de sesión, desplegados con `deploy/docker-compose.yml`.
- **Información tratada:**
  - datos fiscales: RFC, CFDI y contabilidad;
  - datos personales de empleados: salario, RFC/CURP si se capturan, nómina y finiquitos;
  - datos de clientes y proveedores.
  - Están sujetos a la **LFPDPPP** y al **CFF** (conservación de la contabilidad por 5 años, art. 30).
- **Activos críticos:**
  - volumen `pgdata` (base);
  - `deploy/backups/`;
  - volumen `keys` (llaves que cifran la cookie de sesión);
  - `deploy/.env` (contraseña de la base, token de instalación, llave de Anthropic).

## 2. Declaración de aplicabilidad (ISO 27001:2022, Anexo A)

### A.5 Controles organizacionales

| Control | Estado | Cómo se cumple / evidencia |
|---|---|---|
| 5.1 Políticas de seguridad | Org | Adoptar la sección 4 de este documento, aprobada por la dirección y revisada anualmente. |
| 5.2 Roles y responsabilidades | Org | Nombrar un responsable de seguridad y un administrador de Apolo (rol Gerencia). |
| 5.3 Segregación de funciones | Sistema | Roles por módulo: Gerencia, Ventas, Almacén y Finanzas. Las políticas de autorización se aplican en el servidor (`Modules.cs`, `Program.cs`), no solo en el menú. Las pólizas automáticas no se revierten a mano: se cancela el documento de origen. |
| 5.9 Inventario de activos | Org | Sección 1, más el inventario de servidores y cuentas de la organización. |
| 5.12 Clasificación de la información | Org | Nómina y datos personales: confidencial. Contabilidad: confidencial y de conservación legal. |
| 5.15 Control de acceso | Sistema | Autenticación obligatoria. Cada módulo exige su permiso y cada empresa solo ve sus datos (filtro de inquilino en `AppDb`, además de una verificación al guardar que impide escribir en otra empresa). |
| 5.16 Gestión de identidades | Sistema | Un usuario por persona, identificado por correo único. No hay cuentas compartidas en producción: `admin/admin` solo existe en desarrollo. |
| 5.17 Información de autenticación | Sistema | Contraseñas con hash PBKDF2 (ASP.NET Identity) y rehash automático. Política de 12 caracteres o más con letras y números. Cambio de contraseña con verificación de la actual (`PUT /api/me/password`). |
| 5.18 Derechos de acceso | Sistema + Org | Alta, baja, cambio de rol y desbloqueo con `PUT /api/users/{id}`. Una baja, un cambio de rol, un cambio de módulos del rol o un cambio de contraseña invalidan al instante las sesiones abiertas (huella de sesión verificada en cada petición). **Org:** revisión trimestral de usuarios activos y baja el mismo día de la salida de un empleado. |
| 5.23 Servicios en la nube | Org | Si se usa Apolo IA, los datos de la consulta viajan a Anthropic. Evaluar el acuerdo de tratamiento de datos o dejar `ANTHROPIC_API_KEY` vacío. |
| 5.24–5.28 Gestión de incidentes | Org | Procedimiento en §4.6. Evidencia: bitácora de auditoría y logs JSON. |
| 5.29–5.30 Continuidad / TIC para continuidad | Parcial | Respaldo diario y restauración documentados (README › Producción). **Falta (Org):** copia fuera del servidor y una prueba de restauración trimestral registrada. |
| 5.31 Requisitos legales | Org | CFF art. 30 (conservar 5 años; no purgar respaldos anuales). LFPDPPP (aviso de privacidad a empleados y clientes). |
| 5.33 Protección de registros | Sistema | Las pólizas son inmutables: se corrigen con reversa. El cierre del ejercicio bloquea el periodo. Los CFDI conservan su XML. |
| 5.34 Privacidad y datos personales | Parcial | Acceso a nómina y RRHH solo con el permiso `rrhh`. **Org:** aviso de privacidad y atención de derechos ARCO. |

### A.6 Controles de personas

| Control | Estado | Cómo se cumple |
|---|---|---|
| 6.1–6.6 Selección, términos, concientización, confidencialidad, baja | Org | Cláusula de confidencialidad en el contrato. Inducción de seguridad anual. Checklist de baja: desactivar al usuario en Apolo, lo que cierra sus sesiones. |
| 6.8 Reporte de eventos | Org | Canal único (correo del responsable de seguridad) para reportar incidentes. |

### A.7 Controles físicos

| Control | Estado | Cómo se cumple |
|---|---|---|
| 7.x | Org | Los hereda el proveedor de nube o centro de datos. Conservar su informe SOC 2 o ISO 27001 como evidencia. |

### A.8 Controles tecnológicos

| Control | Estado | Cómo se cumple / evidencia |
|---|---|---|
| 8.2 Accesos privilegiados | Sistema + Org | Solo Gerencia tiene el módulo Configuración, desde donde se administran usuarios. Nadie puede quitarse a sí mismo el rol ni desactivarse. **Org:** mínimo dos administradores y acceso SSH al servidor solo con llave. |
| 8.3 Restricción de acceso | Sistema | Autorización por módulo en cada endpoint y aislamiento por empresa. |
| 8.5 Autenticación segura | Sistema | Mensajes de error que no revelan si el correo existe, con tiempo constante (hash señuelo). Bloqueo de 15 minutos tras 5 intentos fallidos, más límite de 10 intentos por minuto por IP. Cookie `HttpOnly`, `Secure` y `SameSite=Strict`, con expiración de 8 horas y renovación por uso. **Pendiente:** segundo factor (TOTP) si la organización lo exige. |
| 8.6 Gestión de capacidad | Sistema | Límite general de 600 peticiones por minuto por usuario o IP. Las tareas pesadas corren en segundo plano. |
| 8.7 Protección contra malware | Org | Antivirus/EDR en estaciones. Servidor con imágenes oficiales actualizadas. |
| 8.8 Vulnerabilidades técnicas | Sistema | `scripts/security-check.sh` corre las pruebas, busca paquetes NuGet vulnerables (incluidos transitivos), hace `npm audit` de producción y busca secretos. Correrlo antes de cada versión y mensualmente. Estado actual: 0 vulnerabilidades en producción. Las alertas moderadas de npm son de herramientas de desarrollo (eslint). |
| 8.9 Configuración | Sistema | Configuración por variables de entorno: no hay secretos en el repositorio, salvo la base local de desarrollo. La base no publica puertos. El contenedor corre con un usuario sin privilegios. |
| 8.10 Eliminación de información | Org | Retención de respaldos (`BACKUP_KEEP_DAYS`). Para cumplir el CFF, conservar además un respaldo de cierre por ejercicio durante 5 años. |
| 8.12 Prevención de fuga de datos | Parcial | Las respuestas de `/api` llevan `Cache-Control: no-store`. Las exportaciones CSV/XML requieren el permiso del módulo. |
| 8.13 Respaldo | Sistema | `pg_dump` diario, comprimido, con rotación (`deploy/backup.sh`). Restauración probada. **Org:** copia cifrada fuera del servidor. |
| 8.15 Registro (logging) | Sistema | La bitácora de auditoría registra cada alta, cambio o baja con usuario, fecha y campos (las contraseñas se enmascaran). También registra accesos, accesos fallidos, bloqueos y cambios de contraseña. Se consulta en Configuración › Auditoría. Los logs de la aplicación salen en JSON por consola. |
| 8.16 Monitoreo | Parcial | `/healthz` sirve para un monitor externo de disponibilidad. **Org:** alertas de accesos fallidos repetidos (consultar la bitácora o el colector de logs). |
| 8.20–8.21 Seguridad de redes | Sistema | Solo se exponen 80 y 443 (Caddy). La API y la base quedan en la red interna de Docker. |
| 8.23 Filtrado web / navegador | Sistema | CSP estricta: scripts propios más el hash del script de tema, sin `unsafe-eval`, `frame-ancestors 'none'` y `object-src 'none'`. Además `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` y `Permissions-Policy`. |
| 8.24 Criptografía | Sistema | TLS 1.2+ con certificados automáticos (Caddy y Let's Encrypt) y HSTS. Contraseñas con PBKDF2. Cookie cifrada con ASP.NET Data Protection, cuyas llaves persisten en el volumen `keys`. **Org:** cifrado de disco del servidor y de los respaldos. |
| 8.25–8.29 Desarrollo seguro y pruebas | Sistema | Pruebas automatizadas de cálculo fiscal, contable y de seguridad (`Apolo.Tests`). Validación de entradas en cada endpoint. Consultas parametrizadas: EF Core y SQL interpolado con parámetros. Protección CSRF por `SameSite=Strict` más cuerpos JSON. |
| 8.31 Separación de ambientes | Sistema | Desarrollo (`Development`: datos demo, `admin/admin`) y producción (`Production`: sin demo, alta con token) quedan separados por configuración. |
| 8.32 Gestión de cambios | Org + Sistema | Migraciones versionadas en `Migrations/`. Procedimiento en §4.5. |
| 8.34 Protección durante auditorías | Org | Las auditorías usan un usuario de solo lectura creado con un rol limitado. |

## 3. Mapeo SOC 2 (Trust Services Criteria 2017, rev. 2022)

| Criterio | Controles de Apolo |
|---|---|
| CC1 Entorno de control | Org: §4.1, roles y responsables (A.5.2) |
| CC2 Comunicación e información | Org: políticas publicadas, aviso de privacidad. Sistema: bitácora consultable. |
| CC3 Evaluación de riesgos | §5, registro de riesgos, revisado anualmente. |
| CC4 Monitoreo de controles | `scripts/security-check.sh` mensual. Revisión trimestral de usuarios y restauraciones. |
| CC5 Actividades de control | Políticas §4. Segregación por roles (A.5.3). |
| CC6.1 Acceso lógico | Autenticación, roles por módulo, aislamiento por empresa, cookie segura. |
| CC6.2–6.3 Alta, baja y cambios de acceso | `/api/users`, revocación inmediata de sesiones, revisión trimestral. |
| CC6.6 Amenazas externas | TLS, HSTS, CSP, límites de peticiones, bloqueo de cuenta. |
| CC6.7 Transmisión | TLS de extremo a extremo hasta el proxy. Red interna Docker. |
| CC6.8 Software malicioso | Imágenes oficiales, `security-check.sh`, EDR en estaciones (Org). |
| CC7.1 Detección de vulnerabilidades | `security-check.sh` (NuGet y npm). |
| CC7.2–7.3 Monitoreo de anomalías | Bitácora (accesos fallidos, bloqueos), logs JSON, `/healthz`. |
| CC7.4–7.5 Respuesta y recuperación | §4.6 incidentes. Restauración desde respaldo. |
| CC8.1 Gestión de cambios | §4.5, migraciones versionadas, pruebas antes de desplegar. |
| CC9 Mitigación de riesgos y proveedores | §5. Evaluación de proveedores: nube, Anthropic, PAC futuro. |
| A1 Disponibilidad | Reinicio automático de contenedores, respaldo diario, `/healthz`, RPO de 24 h y RTO de 4 h (§4.4). |
| C1 Confidencialidad | Permisos por módulo, cifrado en tránsito, retención y eliminación (A.8.10). |
| PI1 Integridad del procesamiento | Pólizas cuadradas por construcción y conciliación automática de 7 revisiones (Contabilidad › Conciliación). Inmutabilidad y cierre de periodos. Folios consecutivos asignados en servidor con bloqueo de fila. |

## 4. Políticas operativas (plantillas para aprobar)

### 4.1 Gobierno

- La dirección aprueba este documento.
- El responsable de seguridad lo revisa cada año o tras un incidente o cambio mayor.
- Las excepciones se documentan con fecha de vencimiento.

### 4.2 Control de acceso

- Mínimo privilegio: cada persona recibe el rol con los módulos que necesita.
- Gerencia se limita a la dirección y a 1 o 2 administradores.
- **Alta:** solicitud aprobada por el jefe del área.
- **Baja:** el mismo día de la salida, desactivando al usuario.
- **Revisión:** cada trimestre se exporta la lista de usuarios y se firma su vigencia.
- **Contraseñas:** 12 o más caracteres con letras y números, nunca compartidas. Se cambian ante sospecha de compromiso. Un administrador desbloquea cuentas después de verificar la identidad.

### 4.3 Registro y monitoreo

- La bitácora de auditoría no se borra. Se conserva al menos 5 años junto con la contabilidad.
- Cada semana se revisan los eventos `acceso fallido` y `bloqueo`.
- `/healthz` se monitorea cada 5 minutos con alerta al responsable.

### 4.4 Respaldo y continuidad

- **RPO:** 24 horas, por el respaldo diario. **RTO:** 4 horas.
- Copia diaria de `deploy/backups/` a un almacenamiento externo cifrado.
- Prueba de restauración trimestral en un servidor aparte, registrada con fecha, archivo, duración y resultado.
- El volumen `keys` y `deploy/.env` se respaldan aparte, en una bóveda de secretos.

### 4.5 Gestión de cambios

1. El cambio se desarrolla y prueba en desarrollo.
2. `scripts/security-check.sh` pasa sin errores.
3. Se respalda la producción.
4. Se ejecuta `docker compose up -d --build`.
5. Se verifica `/healthz` y se recorre lo básico: entrar, abrir el tablero, emitir un borrador.

- Cada versión deja registro: fecha, quién, qué cambió y el resultado.
- Las migraciones de base nunca se editan después de publicadas.

### 4.6 Respuesta a incidentes

1. **Detectar y reportar** al responsable de seguridad.
2. **Contener:**
   - desactivar los usuarios comprometidos, lo que cierra sus sesiones;
   - rotar `DB_PASSWORD`, `ANTHROPIC_API_KEY` y, si hace falta, borrar el volumen `keys` para cerrar todas las sesiones.
3. **Analizar** con la bitácora de auditoría y los logs.
4. **Recuperar** desde respaldo si hubo alteración.
5. **Notificar:** si se afectaron datos personales, avisar a los titulares según la LFPDPPP.
6. **Lecciones aprendidas** en un registro firmado dentro de los 10 días hábiles siguientes.

### 4.7 Gestión de vulnerabilidades

- `scripts/security-check.sh` corre cada mes y antes de cada versión.
- Plazos de corrección: crítica en 7 días, alta en 30, moderada en 90.
- Las alertas de herramientas de desarrollo que no llegan a producción se aceptan documentándolas.

## 5. Registro de riesgos (resumen)

| Riesgo | Impacto | Tratamiento | Residual |
|---|---|---|---|
| Robo de credenciales | Alto | Bloqueo, límites, política de contraseñas y revocación de sesiones. Pendiente: 2FA. | Medio |
| Pérdida de la base | Alto | Respaldo diario, copia externa (Org) y prueba de restauración. | Bajo |
| Acceso indebido a nómina | Alto | Permiso `rrhh` y revisión trimestral. | Bajo |
| Fuga de datos a un tercero (IA) | Medio | IA opcional, evaluación del proveedor y llave vacía si no se aprueba. | Bajo |
| Error fiscal en cálculos | Medio | Pruebas automatizadas, parámetros editables y revisión del contador. | Medio |
| Alteración de registros contables | Alto | Inmutabilidad, cierre de periodo y bitácora. | Bajo |
| Dependencia vulnerable | Medio | `security-check.sh` mensual. | Bajo |
| Servidor comprometido | Alto | Contenedor sin privilegios, base sin puertos expuestos, SSH con llave (Org) y parches del sistema operativo (Org). | Medio |

## 6. Pendientes recomendados (fuera del alcance actual)

- Segundo factor de autenticación (TOTP) para Gerencia.
- Cifrado de las llaves de Data Protection con un certificado o un KMS.
- Envío de logs a un SIEM con alertas automáticas de accesos fallidos.
- Respaldos cifrados con `age` o `gpg` antes de sacarlos del servidor.
- Prueba de penetración externa antes del primer cliente en producción.
