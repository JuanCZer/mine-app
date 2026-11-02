# Objetivo 5: seguridad integral del backend — estado y plan de cierre

> **Revisado el 19 de octubre de 2026:** **23 de 23 actividades de implementación completadas; 13 puertas de liberación externas abiertas**. El modelo de amenazas ya está persistido y el escaneo estándar `d0331dac-f35b-4f37-9ae2-89c13a9be54d` terminó con cobertura completa. Reportó un hallazgo medio en el wrapper de despliegue PostgreSQL, corregido localmente al exigir `sslmode=verify-full` antes de iniciar `psql`. Swagger/OpenAPI se sirve solo en Development. Production permanece bloqueado hasta cerrar las puertas de infraestructura, integración real y datos heredados. Este documento usa OWASP como guía de verificación; no afirma certificación ni ausencia absoluta de vulnerabilidades.

**Objetivo:** reducir accesos indebidos y exposición de datos mediante controles verificables en autenticación, autorización, persistencia, transporte, navegador y borde HTTP.

**Arquitectura actual:** ASP.NET Core 10 con Identity persistido en PostgreSQL, sesión por cookie, antiforgery, CORS explícito por ambiente, ownership por usuario, autorización de validación por rol, SQL parametrizado y una única frontera para conexiones Npgsql. Angular consume rutas relativas del mismo origen.

**Referencias de trabajo:** [OWASP ASVS 5.0.0](https://github.com/OWASP/ASVS/tree/v5.0.0), [OWASP Top 10:2025](https://top10.owasp.org/2025/en/) y [OWASP API Security Top 10:2023](https://api-security.owasp.org/editions/2023/en/0x11-t10/). La selección aplicable y su evidencia están en la [matriz de controles](../../security/control-matrix.md); las puertas operativas están en el [checklist de despliegue](../../security/deployment-checklist.md).

## Estado real

La identidad se carga desde PostgreSQL. Las rutas privadas requieren una cuenta activa; las incidencias se filtran por propietario y la validación de fases exige un rol autorizado. Login y logout requieren CSRF, la cookie de sesión es `HttpOnly`, host-only y `SameSite=Lax`, y fuera de Development/Testing exige transporte seguro. El cierre de sesión rota el sello de seguridad para invalidar la sesión utilizada.

Los éxitos y rechazos de autenticación y los cierres de sesión se escriben de forma acotada en `app.audit_events`. No se almacenan contraseñas, cookies, tokens ni nombres de usuario rechazados. Los éxitos de mutaciones de incidencias y fases se escriben dentro de sus transacciones PostgreSQL.

La API aplica límites de cuerpo, rate limiting de login, respuestas sanitizadas y encabezados defensivos. `frame-ancestors 'none'` está aplicado; la política completa compatible con Angular y Mapbox sigue en Report-Only. Forwarded headers permanecen cerrados si no hay proxies o redes configurados explícitamente. CORS permite `http://localhost:4200` únicamente en Development y, fuera de ese ambiente, solo los orígenes exactos configurados en `Cors:AllowedOrigins`; las pruebas confirman que Production no hereda el origen local.

Production y Staging exigen `Ssl Mode=VerifyFull` para las conexiones PostgreSQL principal y de catálogos y rechazan `Trust Server Certificate=true`. El servidor PostgreSQL inspeccionado aún no ofrece TLS, por lo que la política bloquea correctamente un despliegue productivo pero el canal real todavía no está listo.

El bundle nuevo retiró el acceso al almacén compartido `gds-detection-cases-v1` y redirige las rutas anteriores al flujo autenticado de incidencias. El blob existente se conserva para recuperación: cada perfil de navegador debe exportarlo y conciliarlo o eliminarlo mediante aprobación antes de permitir cambio de cuenta.

## Evidencia disponible

- [Autenticación y sesiones](../../../backend/Auth/README.md).
- [Esquema, pools y política PostgreSQL](../../../backend/Database/README.md).
- [Runbook de corte PostgreSQL y retiro del blob local](../../runbooks/postgresql-cutover.md).
- [Verificación independiente de auditoría de autenticación](../../../.superpowers/sdd/2026-09-27-05-backend-security/auth-audit-verification.md).
- [Verificación independiente del borde HTTP y TLS](../../../.superpowers/sdd/2026-09-27-05-backend-security/platform-hardening-verification.md).
- [Corrección del almacén local heredado](../../../.superpowers/sdd/2026-09-27-05-backend-security/localstorage-fix-report.md).
- [Revisión de dependencias](../../../.superpowers/sdd/2026-09-27-05-backend-security/dependency-fix-report.md).
- [Modelo de amenazas revisable](../../security/threat-model.md).
- [Escaneo estándar completo](</Users/juancarloszerongarcia/.codex/state/plugins/codex-security/scans/mine-app/a5de6f37f8ec1ea3fc9ad9ce83684568d1b09547_20261019T040349Z_x604ggnv/report.md>).
- [Corrección TLS del despliegue](../../../.superpowers/sdd/2026-09-27-05-backend-security/deployment-tls-fix-report.md).

La regresión local actual incluye 136/136 pruebas backend, 52/52 pruebas Angular, 32/32 pruebas de entorno, builds .NET sin advertencias y build Angular de producción, además de `git diff --check`. Las pruebas cubren la política CORS por ambiente, persistencia/reanudación del importador y el rechazo previo a `spawn` de URLs PostgreSQL sin un único `sslmode=verify-full`; la integración de despliegue contra PostgreSQL continúa siendo una puerta separada.

## Trabajo comprobado

### Reconocimiento y validación

- [x] Inventariar endpoints, sesiones, roles, ownership, catálogos, incidencias, auditoría, conexiones PostgreSQL, almacenamiento del navegador y dependencias.
- [x] Crear y persistir un threat model revisable sobre identidad, datos por usuario, fases, importación, proxy, base remota y artefactos cliente. El documento fuente está en `docs/security/threat-model.md` y el escaneo conserva su proyección sellada en `threatmodel.md`.
- [x] Validar de forma independiente los candidatos de almacenamiento compartido del navegador, auditoría de autenticación y transporte PostgreSQL no estricto.
- [x] Revisar paquetes npm de producción/desarrollo y paquetes .NET; actualizar Angular dentro de la versión mayor vigente y dejar ambas auditorías npm en cero.
- [x] Revisar secretos sin imprimir valores: los archivos `.env` están ignorados y la credencial pública de Mapbox se trata como configuración cliente que requiere restricciones de cuenta/origen.
- [x] Completar un escaneo estándar del repositorio con artefacto final. El escaneo `d0331dac-f35b-4f37-9ae2-89c13a9be54d` quedó `complete` y sellado, cubrió 10 superficies y reportó un hallazgo medio, sin hallazgos altos o críticos. El hallazgo fue corregido en `scripts/apply-database.mjs` y cuenta con prueba de regresión; el reporte sellado conserva el estado previo a la corrección.

### Controles de aplicación

- [x] Usar Identity con hashes versionados, longitud máxima previa al hash, bloqueo de cuenta y trabajo equivalente para usuario desconocido y contraseña incorrecta.
- [x] Aplicar cookie de sesión, CSRF en mutaciones, revocación por sello, expiración absoluta y respuestas 401/403 sin redirección HTML.
- [x] Exigir identidad en servidor, ownership en todas las operaciones de incidencias y rol `validador`/`administrador` para validar fases.
- [x] Mantener revisión optimista, orden de las cuatro fases, reglas de completitud y transacciones para datos, eventos y auditoría.
- [x] Parametrizar SQL y centralizar todos los consumidores PostgreSQL en `PostgresDataSources`.
- [x] Auditar login exitoso, login rechazado y logout sin material de credenciales; compensar una cookie pendiente si falla la auditoría después de firmar.
- [x] Aplicar límite global de cuerpo de 1 MiB, límite de login de 4 KiB y rate limiting de 10 intentos por IP/minuto; conservar el bloqueo de 5 fallos por cuenta durante 15 minutos.
- [x] Aplicar `nosniff`, denegación de framing, Referrer-Policy, Permissions-Policy, HSTS condicionado a HTTPS, forwarded headers con confianza explícita, simetría y un salto, y CORS por ambiente sin origen local implícito en Production.
- [x] Hacer fail-fast en Production/Staging para PostgreSQL sin `VerifyFull`, incluido el override de catálogos y `provision-user`.
- [x] Hacer fail-fast antes de `psql` si `POSTGRES_DEPLOY_URL` no es una URL PostgreSQL con exactamente un `sslmode=verify-full`; rechazar duplicados, controles y formatos ambiguos sin exponer la cadena.
- [x] Retirar del bundle actual las rutas y el servicio que leían o escribían `gds-detection-cases-v1`, sin borrar silenciosamente el blob heredado.
- [x] Integrar Swashbuckle 10.3.0 y publicar Swagger UI/OpenAPI solo en Development. El documento describe `GDS.Session` y protege las operaciones que no tienen `IAllowAnonymous`; la UI conserva cookies del mismo origen y limita el encabezado XSRF a mutaciones `/api`. Evidencia y comandos: [reporte de Swagger](../../../.superpowers/sdd/2026-09-27-05-backend-security/swagger-task-report.md). El RED previo a la implementación no quedó documentado en los artefactos disponibles.

### Verificación independiente

- [x] Repetir rutas normales y fallos de auditoría/cookie; confirmar ausencia de éxito falso y de sesión emitida tras un fallo.
- [x] Revisar 401, 413, encabezados, HSTS, proxies confiables/no confiables, cadenas reenviadas y rutas alternativas a PostgreSQL.
- [x] Ejecutar suites backend y frontend, pruebas de entorno, builds de producción y `git diff --check` después de integrar los controles. La regresión consolidada del 18 de octubre aprobó backend 136/136, Angular 52/52 y entorno 32/32.
- [x] Registrar controles, evidencia, riesgo residual y puertas operativas en la matriz y el checklist de seguridad.
- [x] Completar la matriz PDF → UI → DTO → SQL para las cuatro fases y resolver cada discrepancia documental. La trazabilidad está en `docs/data/phase-field-mapping.md` y cuenta con una prueba de cobertura de DTO/campos SQL.

## Puertas de liberación externas

Estas puertas no representan componentes de aplicación pendientes. Validan el ambiente donde se desplegará el sistema y solo pueden cerrarse con evidencia de ese ambiente. El repositorio ya contiene las políticas, runners y runbooks necesarios; este host no dispone de Docker, Podman, `psql`, `pg_dump` ni `pg_restore`, y no tiene acceso administrativo al servidor PostgreSQL, al proxy o a la cuenta Mapbox.

| ID | Puerta de liberación | Estado actual | Evidencia necesaria para cerrarla |
| --- | --- | --- | --- |
| DEP-01 | TLS PostgreSQL con `hostssl`, CA y SAN válido | Bloqueada por infraestructura: el servidor inspeccionado rechazó `SSLRequest`. | Configuración del servidor y `pg_stat_ssl.ssl=true` con Npgsql y `psql`. |
| DEP-02 | Rotación de cadenas runtime/deploy con verificación completa | Depende de DEP-01 y del gestor de secretos del ambiente. | Rotación registrada y conexiones `VerifyFull`/`verify-full` exitosas, sin revelar valores. |
| DEP-03 | Proxy, CORS, HTTPS/HSTS y límites reales | La aplicación falla cerrado y tiene pruebas locales; faltan valores y URL pública. | Pruebas por la ruta pública con CIDR/orígenes exactos, headers reemplazados y respuestas 401/403/413/5xx. |
| DEP-04 | Keyring persistente y compartido | Soporte de configuración implementado; almacenamiento no provisionado. | Permisos, respaldo, rotación y revocación entre dos réplicas. |
| DEP-05 | Conciliación del blob heredado del navegador | El bundle nuevo ya no lo usa ni lo borra silenciosamente. | Exportación/conciliación o eliminación aprobada en cada perfil, con pestañas y caché antiguas cerradas. |
| DEP-06 | CSP final | Cabeceras defensivas activas y política completa en Report-Only. | Captura de violaciones en el host real para login, fases y Mapbox, seguida de política aplicada sin regresiones. |
| DEP-07 | Restricción del token público Mapbox | El token está separado de código y sigue siendo público por diseño. | Restricciones de origen y scopes comprobadas en la cuenta propietaria. |
| DEP-08 | Instalación limpia y reaplicación de `apply.sql` | Runner preparado; no hay motor/contenedor PostgreSQL local. | Aplicación doble en base desechable de la versión objetivo, sin pérdida ni cambios inesperados. |
| DEP-09 | Backup/restore y RPO/RTO | Runbook disponible; no hay `pg_dump`, `pg_restore` ni base desechable. | Respaldo restaurado, checksum, duración, lectura posterior, RPO/RTO y custodio. |
| DEP-10 | Flujo autenticado completo de cuatro fases | Runner implementado y protegido; ejecución bloqueada por ausencia de Docker/PostgreSQL aislado. | Reporte del recorrido con cuatro validaciones y persistencia tras reinicio. |
| DEP-11 | Validaciones concurrentes entre instancias | Caso preparado en el runner. | Una respuesta exitosa, una `409`, una revisión y un único evento. |
| DEP-12 | Rollback por fallo antes de auditoría | Control transaccional implementado; falta inyección contra PostgreSQL aislado. | Cero cambio parcial en incidencia, fase, validación, evento y auditoría. |
| DEP-13 | Integración del importador legado | Sink, reanudación, cuarentena y conciliación implementados y probados por contrato. | Ejecución aislada con reaplicación, reintento, concurrencia, rollback y cero pendientes. |

## Criterio de cierre

La implementación del Objetivo 5 está completa. La liberación a Production se autoriza únicamente cuando las trece puertas anteriores tienen evidencia del entorno objetivo, no quedan hallazgos críticos/altos validados sin decisión registrada y el checklist de despliegue tiene responsable, fecha y resultado para cada puerta. La evidencia demuestra controles concretos dentro de su alcance; no equivale a una certificación OWASP ni sustituye pruebas de infraestructura, monitoreo o respuesta a incidentes.
