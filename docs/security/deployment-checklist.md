# Checklist de despliegue seguro

**Revisión:** 18 de octubre de 2026.

**Uso:** completar por ambiente y por versión. Cada casilla requiere responsable, fecha/hora, resultado y enlace o identificador de evidencia sanitizada. No copie secretos, hosts privados ni cadenas de conexión en este documento.

La evidencia local de referencia está en la [matriz de controles](control-matrix.md). Ninguna prueba del repositorio sustituye estas puertas del entorno objetivo.

## Bloqueadores conocidos

- El PostgreSQL inspeccionado no ofrece TLS; Production debe permanecer bloqueado hasta demostrar `VerifyFull`.
- No existe todavía un reporte aprobado de integración completa contra PostgreSQL real.
- El blob heredado del navegador debe conciliarse o eliminarse con aprobación en cada perfil.
- La CSP completa sigue en Report-Only.
- Faltan decisiones operativas sobre MFA, auditoría de rechazos de autorización, keyring compartido, SIEM y retención.
- El escaneo estándar `d0331dac-f35b-4f37-9ae2-89c13a9be54d` terminó con cobertura completa; su único hallazgo medio fue corregido localmente. La liberación debe adjuntar el reporte y la evidencia de la corrección.

## Registro de la ejecución

- Ambiente: `__________`
- Versión/commit/artefacto: `__________`
- Ventana en UTC: `__________`
- Responsable de aplicación: `__________`
- Responsable de plataforma: `__________`
- Responsable de datos: `__________`
- Aprobación de seguridad: `__________`
- Plan y responsable de reversión: `__________`

## Artefacto y revisión

- [ ] Instalar desde el lockfile revisado con `npm ci`; ejecutar auditoría completa y de producción, 48 pruebas o su sucesora y build de producción.
- [ ] Compilar la API sin advertencias y ejecutar la suite backend completa; adjuntar salida sanitizada y versiones de SDK/runtime.
- [ ] Completar el escaneo estándar de seguridad y registrar el tratamiento de cada hallazgo crítico/alto.
- [ ] Confirmar que `.env`, llaves, dumps, reportes con credenciales y artefactos temporales no forman parte del paquete.
- [ ] Confirmar que no se habilitan proveedores JSON/InMemory ni autenticación por headers fuera de Testing.

## PostgreSQL y datos

- [ ] Habilitar TLS y reglas `hostssl`; impedir sesiones en claro para roles y redes de aplicación/despliegue.
- [ ] Validar CA, vigencia y SAN con el nombre usado por el backend; demostrar Npgsql y `psql` con verificación completa y `pg_stat_ssl.ssl=true`.
- [ ] Ejecutar [apply.sql](../../backend/Database/apply.sql) con la cuenta de despliegue en una base aislada limpia y aplicarlo una segunda vez sin errores ni pérdida de datos.
- [ ] Verificar privilegios mínimos de los roles runtime y despliegue; runtime no debe modificar identidad administrativa ni eventos auditables existentes.
- [ ] Ejecutar backup y restore; registrar checksum, duración, lectura posterior, RPO/RTO y responsable de custodia.
- [ ] Completar importación, cuarentena y conciliación; no debe quedar un lote pendiente, rechazado o con conciliación requerida antes del corte.
- [ ] Ejecutar el flujo autenticado de las cuatro fases, aislamiento entre cuentas, roles, catálogos, concurrencia y rollback por fallo inyectado; adjuntar el reporte de integración real.

## Identidad, sesiones y secretos

- [ ] Inyectar cadenas, credenciales y claves desde el gestor del ambiente; comprobar rotación y acceso por la identidad mínima del servicio.
- [ ] Configurar `DataProtection__KeyRingPath` en almacenamiento persistente y protegido; si hay varias réplicas, verificar que comparten y pueden rotar el mismo keyring.
- [ ] Aprovisionar cuentas por el comando administrativo documentado, sin cuentas predeterminadas ni contraseñas en argumentos, historial o logs.
- [ ] Definir el nivel ASVS objetivo y cerrar la decisión de MFA; para un nivel que exija segundo factor, no liberar hasta implementarlo y probarlo.
- [ ] Verificar login válido/inválido, bloqueo, 429, expiración absoluta, baja de cuenta, cambio de rol, logout y revocación entre réplicas.
- [ ] Verificar CSRF en login, logout y todas las mutaciones; confirmar cookie host-only, `HttpOnly`, `SameSite` y `Secure` en la URL pública.

## Ingress, navegador y frontend

- [ ] Servir solo por HTTPS; confirmar que HTTP se rechaza o redirige según la política y que HSTS aparece únicamente en la respuesta HTTPS esperada.
- [ ] Configurar solo IP/CIDR de proxies controlados; el ingress debe reemplazar `X-Forwarded-For` y `X-Forwarded-Proto`, y el backend debe procesar un único salto simétrico.
- [ ] Alinear el límite de cuerpo del ingress con la API; comprobar 413 global y el límite de 4 KiB de login sin registrar el cuerpo.
- [ ] Verificar en 401, 403, 413 y 5xx los encabezados defensivos y la ausencia de detalles internos.
- [ ] Recopilar violaciones CSP con login, cuatro fases, mapa, tiles, workers y telemetría; retirar fuentes no usadas y aplicar la política final en el host del SPA.
- [ ] Restringir el token público de Mapbox por orígenes y scopes desde la cuenta del proveedor.
- [ ] En cada perfil autorizado, exportar y conciliar el blob `gds-detection-cases-v1` o aprobar y verificar su eliminación; cerrar pestañas antiguas, purgar caché y comprobar redirecciones.

## Auditoría, monitoreo y operación

- [ ] Confirmar escritura de login exitoso/rechazado, logout y mutaciones; verificar que no aparecen contraseñas, cookies, tokens, cadenas de conexión ni IP en claro.
- [ ] Definir y probar auditoría acotada de rechazos de autorización y otros eventos de seguridad requeridos.
- [ ] Configurar reloj UTC, retención, control de acceso, respaldo y exportación a un sistema separado; probar correlación y alertas sin modificar eventos existentes.
- [ ] Alertar sobre bloqueos, picos de 401/403/429, fallos de auditoría, fallos TLS, 5xx y cambios administrativos relevantes.
- [ ] Comprobar dashboards, contactos, escalamiento, runbook de incidente y capacidad de operar durante una caída de dependencias.

## Decisión de liberación

- [ ] Todas las puertas anteriores tienen evidencia vigente, responsable y resultado aprobado.
- [ ] No quedan hallazgos críticos/altos validados sin corrección o aceptación formal con fecha y propietario.
- [ ] El smoke test posterior al despliegue valida salud, login, catálogos, ownership y una incidencia sintética completa.
- [ ] Se verificó la reversión conforme al [runbook de corte](../runbooks/postgresql-cutover.md), incluida la conciliación de cualquier escritura posterior.
- [ ] La aprobación final registra limitaciones aceptadas; no describe el resultado como certificación OWASP.
