# Corte de incidencias JSON a PostgreSQL

Este procedimiento importa el almacén principal `notes.json` a las tablas relacionales de PostgreSQL. Los expedientes de `gds-detection-cases-v1` se archivan o importan solo mediante una decisión separada; su etapa local nunca se convierte en una validación autenticada.

El flujo de bootstrap y la secuencia de rollout deben documentarse junto al corte para que el arranque del despliegue, el bootstrap del administrador y la aprobación del cambio queden conectados entre sí sin depender de una ejecución manual no registrada.

## Estado de esta entrega

El SQL autocontenido, las comprobaciones estáticas, el analizador, el comando aislado y el adaptador PostgreSQL de `ILegacyImportSink` están incluidos en el repositorio. El importador no se ejecuta al iniciar la API y utiliza exclusivamente `POSTGRES_DEPLOY_URL`. Debe probarse primero en una base aislada antes de importar un archivo legado del entorno de trabajo. El bundle nuevo retira `/deteccion` y deja de leer la clave compartida, pero el riesgo residual del archivo local no termina hasta exportarlo y conciliarlo o eliminarlo de forma aprobada en cada perfil. Una pestaña con el bundle anterior mantiene la exposición mientras siga operativa.

No abrir escrituras en producción mientras una comprobación de este documento siga pendiente.

## Barrera obligatoria para retirar el bundle local

Una pestaña que cargó la versión anterior conserva su JavaScript en memoria aunque se publiquen archivos nuevos. Ese bundle no puede parchearse de forma remota y todavía puede leer o escribir la clave compartida. Antes de permitir que una estación cambie de cuenta:

1. Bloquear temporalmente nuevos logins y cambios de cuenta en el proxy o detener la API. Anunciar que no se debe reutilizar ninguna estación hasta completar la barrera.
2. En cada perfil autorizado, exportar y registrar el archivo local conforme a la sección 2. No cerrar el último perfil con datos sin obtener antes el archivo y su checksum.
3. Cerrar todas las pestañas del origen. En estaciones administradas, terminar el proceso completo del navegador o reiniciar la estación; verificar que no quede ningún proceso o pestaña con el bundle anterior. En un equipo no administrado que no pueda verificarse, mantener bloqueado el cambio de cuenta.
4. Desplegar la aplicación, purgar cachés de proxy/CDN y retirar los HTML anteriores. Los nombres hash de los chunks no sustituyen el cierre de pestañas que ya estaban abiertas.
5. Abrir una ventana nueva, recargar sin caché y comprobar que el menú no contiene «Centro de detección», que `/deteccion` y `/deteccion/<id>` redirigen a `/incidencias`, y que la clave local conserva el mismo checksum.
6. Revocar las sesiones anteriores conforme al procedimiento de autenticación del entorno. Habilitar logins y cambios de cuenta solo después de registrar quién verificó cada estación, hora, cierre o inutilización de todas las pestañas anteriores, recarga del bundle nuevo, checksum y resultado de las dos redirecciones.

La aceptación falla si una pestaña anterior sigue operativa, si no puede demostrarse el cierre o inutilización de todas ellas y la recarga posterior, si cambió el archivo local o si una ruta retirada todavía presenta expedientes. En ese caso, mantener la estación fuera de servicio multiusuario y repetir la barrera. Superar esta barrera impide que el bundle anterior cruce cuentas, pero el blob sigue siendo material sensible recuperable hasta completar y verificar su exportación y conciliación o su eliminación aprobada en el navegador real.

## Responsables y cuentas

- La cuenta de despliegue es propietaria controlada de los objetos o una cuenta equivalente autorizada para DDL. Recibe temporalmente el rol SQL `gds_db_deployer` para la importación heredada.
- La cuenta de la API recibe `gds_app_runtime`; no recibe permisos para modificar o borrar validaciones, eventos o auditoría.
- La aprobación del lote corresponde a un usuario autenticado con permiso de importación. `demo-user`, nombres escritos en revisiones y propietarios locales no crean usuarios ni credenciales.
- La API recibe `ConnectionStrings__Postgres`. El comando aislado recibe `POSTGRES_DEPLOY_URL`; su wrapper elimina del proceso las cadenas runtime. Ninguna se escribe en scripts, reportes, comandos versionados o registros.
- Production y Staging no arrancan si `ConnectionStrings__Postgres` (o el override `ConnectionStrings__Catalogs`) no usa `Ssl Mode=VerifyFull`; `Trust Server Certificate=true` siempre se rechaza. `POSTGRES_DEPLOY_URL` debe usar el equivalente de libpq `sslmode=verify-full` y, cuando proceda, `sslrootcert`.

## 1. Preparar el entorno aislado

1. Seleccionar y documentar la versión de PostgreSQL de destino, ventana de mantenimiento, responsables, RPO y RTO.
2. Crear una base de ensayo vacía con la misma configuración y extensiones del destino.
3. Crear logins propios del entorno y concederles los roles de grupo. Los scripts solo crean roles `NOLOGIN`.
4. Confirmar que `psql`, `pg_dump` y `pg_restore` proceden de una versión compatible con el servidor.
5. Guardar fuera del repositorio la conexión, los respaldos y los archivos JSON originales con acceso restringido.
6. Antes del ensayo remoto, exigir `ssl=on`, reglas `hostssl` y denegación efectiva de `hostnossl`; comprobar que el SAN del certificado coincide con el DNS o IP usados y que el cliente confía en la CA. La aceptación requiere `pg_stat_ssl.ssl=true`, apertura Npgsql con `VerifyFull` y `psql` con `sslmode=verify-full`.

El endpoint inspeccionado el 4 de octubre de 2026 respondió `N` al `SSLRequest` y
la sesión confirmó `ssl=false`. Mientras siga así, solo Development/Testing pueden
usar la excepción temporal; el corte a Production permanece bloqueado. No cambie
la cadena a `VerifyFull` para aparentar el control: primero habilite TLS en el
servidor y repita la validación descrita en el reporte de política TLS.

En la primera instalación, el rol de grupo todavía no existe. Un administrador copia el único archivo `backend/Database/apply.sql` al servidor, lo ejecuta una vez, concede la membresía al login de despliegue y comprueba que puede activarla. `POSTGRES_DEPLOY_LOGIN` contiene solo el nombre del rol PostgreSQL, no una cadena de conexión:

```bash
psql "$POSTGRES_ADMIN_URL" --set ON_ERROR_STOP=on --file backend/Database/apply.sql
psql "$POSTGRES_ADMIN_URL" --set ON_ERROR_STOP=on --set deploy_login="$POSTGRES_DEPLOY_LOGIN" <<'SQL'
GRANT gds_db_deployer TO :"deploy_login";
BEGIN;
SET LOCAL ROLE gds_db_deployer;
SELECT current_role;
ROLLBACK;
SQL
```

La membresía permite que las transacciones aprobadas inserten validaciones con origen `legacy`. El login usado por `POSTGRES_DEPLOY_URL` también debe ser propietario de los objetos que modifica; pertenecer a `gds_db_deployer` por sí solo no concede propiedad sobre tablas en `public`.

## 2. Respaldar y registrar las fuentes

Con las escrituras aún habilitadas se puede hacer un ensayo preliminar. Para el corte final, pausar primero todas las escrituras y repetir este paso.

```bash
install -m 0700 -d "$IMPORT_BACKUP_DIR"
cp --preserve=all backend/Data/notes.json "$IMPORT_BACKUP_DIR/notes.json"
shasum -a 256 "$IMPORT_BACKUP_DIR/notes.json" > "$IMPORT_BACKUP_DIR/notes.json.sha256"
pg_dump --format=custom --file="$IMPORT_BACKUP_DIR/pre-cutover.dump" "$POSTGRES_ADMIN_URL"
```

No reutilizar nombres reservados del shell para variables. El directorio debe estar cifrado o en almacenamiento protegido. Exportar manualmente `gds-detection-cases-v1` solo desde navegadores autorizados, sin cookies, tokens, contraseñas ni contenido de otros sitios. La ruta local ya no está disponible: exportar antes del despliegue o usar el procedimiento de recuperación de [Retiro seguro del panel local](../detection-module.md). La aplicación y el logout dejan la clave intacta hasta que un custodio apruebe su eliminación.

Registrar por fuente:

- nombre estable y checksum SHA-256;
- tamaño y número de registros;
- suma de evidencias, eventos, revisiones y fases validadas;
- revisiones mínima/máxima;
- responsable de custodia y ruta del respaldo.

## 3. Instalar y comprobar el esquema

Copie `backend/Database/apply.sql` al servidor. Desde el directorio que contiene esa copia, ejecute:

```bash
psql "$POSTGRES_DEPLOY_URL" --set ON_ERROR_STOP=on --file apply.sql
```

Repetir el mismo `apply.sql` y comprobar que no aparecen tablas, opciones o roles duplicados:

```bash
psql "$POSTGRES_DEPLOY_URL" --set ON_ERROR_STOP=on --file apply.sql
```

El archivo usa una sola transacción y termina con integridad estructural y conciliación. Al ejecutarlo con `psql`, mantenga `--set ON_ERROR_STOP=on` para que el cliente devuelva error inmediatamente; en pgAdmin, la transacción evita confirmar un lote incompleto. En una instalación sin importaciones, el resumen de lotes estará vacío. Guardar salida, versión del commit y checksum de `apply.sql`. Una compilación de C# o las pruebas estáticas no sustituyen esta ejecución.

## 4. Analizar sin escribir

Preparar dos objetos JSON protegidos:

- `owners.json`: cada `legacy userId → UUID` de una cuenta custodio existente;
- `timezones.json`: cada `sourceId → zona IANA`, o `"*"` solo cuando una única zona esté demostrada para toda la fuente.

El análisis se ejecuta con el comando siguiente. `POSTGRES_DEPLOY_URL` se toma del entorno o de `backend/.env`, debe usar `sslmode=verify-full` y su login debe poder leer los catálogos. Para `--kind detection` acepta el sobre JSON del exportador (`schemaVersion: 1`), la copia exacta del almacén (`version: 1`) o el arreglo heredado.

```bash
npm run legacy -- analyze \
  --source "$IMPORT_BACKUP_DIR/notes.json" \
  --name notes-json \
  --kind notes \
  --owner-map "$IMPORT_BACKUP_DIR/owners.json" \
  --timezone-map "$IMPORT_BACKUP_DIR/timezones.json"
```

El comando escribe únicamente `ToSafeReport()`. Códigos de salida:

- `0`: todos los registros pueden importarse;
- `2`: existe al menos un registro que requiere conciliación o cuarentena;
- `1`: la fuente o los argumentos no son válidos.

El reporte contiene IDs, hashes, conteos y códigos de problema. No contiene títulos, descripciones, evidencias ni el JSON original. Revisar al menos:

- IDs ausentes o no UUID; los no UUID conservan `public_id` y reciben un UUID interno determinista;
- propietarios sin correspondencia, en especial `demo-user`;
- coordenadas fuera de rango;
- fechas sin offset y zonas inválidas;
- códigos de catálogo ausentes o retirados;
- datos de un objeto de captura guardados bajo otra fase;
- validaciones heredadas, que conservarán nombre/fecha y `validated_by_user_id = NULL`;
- notas sin workflow, que entrarán con cuatro fases pendientes;
- expedientes locales, cuya etapa queda solo como dato de origen.

El archivo original se conserva como texto exacto en `legacy_records.original_payload_json`, además de su hash. No corregirlo ni normalizarlo en el respaldo.

## 5. Aprobar e importar

1. Resolver las correspondencias de dueño y zona que sí estén demostradas.
2. Aprobar explícitamente el análisis con el actor autenticado. La aprobación no borra problemas: los registros con problemas bloqueantes se archivan en cuarentena.
3. Crear/reanudar el lote por su UUID y checksum.
4. Importar cada registro en una transacción que incluya original, correspondencia, incidencia normalizada, fases, selecciones, evidencia, validaciones legado y eventos.
5. El adaptador de `ILegacyImportSink` debe imponer unicidad por `(source, source_record_id, source_hash)`. El mismo ID con un hash distinto es un conflicto de conciliación; nunca sobrescribe la incidencia anterior.
6. Cada posición del archivo queda vinculada por `(batch_id, source_index)` en `app.legacy_batch_records`, junto con su disposición y motivo de conciliación. Si otro lote ya archivó el mismo original, se reutiliza la fila protegida y el contexto del lote actual decide si esa ocurrencia se importa o queda en cuarentena.
7. Repetir exactamente el mismo lote. El segundo resultado debe informar todos los registros como ya procesados y cero inserciones nuevas; sus totales acumulados y el código de salida deben seguir mostrando cualquier cuarentena previa.

Una interrupción se reanuda con el mismo UUID de lote. No crear otro lote para ocultar una ejecución parcial.

El UUID de `--approve-by` debe corresponder a un usuario activo con rol `administrador`. Copiar la huella mostrada por el análisis y generar una sola vez el UUID del lote:

```bash
IMPORT_BATCH_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
npm run legacy -- import \
  --source "$IMPORT_BACKUP_DIR/notes.json" \
  --name notes-json \
  --kind notes \
  --owner-map "$IMPORT_BACKUP_DIR/owners.json" \
  --timezone-map "$IMPORT_BACKUP_DIR/timezones.json" \
  --approve-by "$IMPORT_APPROVER_USER_ID" \
  --batch-id "$IMPORT_BATCH_ID" \
  --confirm-source-sha256 "$ANALYZED_SOURCE_SHA256"
```

El comando vuelve a analizar el archivo y compara la huella antes de abrir el lote. Opciones desconocidas o duplicadas fallan. Un resultado `2` indica que se archivaron registros para conciliación; no habilite escrituras hasta dejar ese conteo en cero. Para reanudar, use exactamente los mismos archivos, correspondencias, aprobador, huella y `IMPORT_BATCH_ID`.

## 6. Conciliar antes de abrir escrituras

```bash
psql "$POSTGRES_DEPLOY_URL" --set ON_ERROR_STOP=on --file apply.sql
```

La reaplicación es idempotente y sus dos secciones finales vuelven a comprobar el esquema y presentan la conciliación sin requerir otro archivo SQL.

Al actualizar una instalación existente, detener temporalmente las importaciones, respaldar la base, ejecutar primero `apply.sql` y desplegar después `LegacyImport.Tool`. El SQL crea y rellena de forma idempotente la membresía por lote; el binario nuevo requiere esa tabla. Revisar cualquier lote reabierto como `running`, pues indica que el esquema anterior lo cerró con menos posiciones que `record_count` o con registros pendientes.

Comparar contra el inventario de la sección 2:

- registros declarados = posiciones vinculadas;
- importados + pendientes + rechazados + por conciliar = posiciones vinculadas;
- ninguna incidencia importada carece de correspondencia;
- ninguna correspondencia apunta a una incidencia inexistente;
- IDs públicos, revisión, coordenadas, fechas originales, evidencias y eventos coinciden campo por campo;
- cada incidencia tiene las cuatro fases y un único detalle tipado por fase;
- no hay pendiente, rechazado o `reconciliation_required` antes del corte.

La cuarentena permite investigar sin perder el original, pero no cuenta como importación terminada. Resolver cada discrepancia o retirar formalmente el lote del corte.

## 7. Ensayo funcional y de concurrencia

Con la API apuntando a la base aislada y la cuenta runtime:

1. Comprobar autenticación, catálogo, lista, mapa, historial y lectura de un registro legado.
2. Crear una incidencia y guardar borradores de las cuatro fases.
3. Verificar `409` por revisión obsoleta o salto de fase, `422` por fase incompleta, `401` sin identidad y `403` sin permiso.
4. Validar el flujo completo, el cierre por mitigación y el descarte por falso positivo.
5. Ejecutar dos instancias contra la misma base; dos validaciones simultáneas deben producir un éxito, un `409`, un solo incremento de revisión y un solo evento.
6. Inyectar un fallo antes de auditoría y verificar que incidencia, fase, validación, evento y auditoría hacen rollback juntos.
7. Intentar `UPDATE`/`DELETE` directo de una fase validada con la cuenta runtime y verificar rechazo.
8. Reiniciar ambas instancias y repetir lecturas.

Ejecutar las suites del repositorio y las pruebas PostgreSQL reales. Conservar resultados y versión probada.

## 8. Corte

1. Anunciar la ventana y pausar escrituras en la versión JSON.
2. Confirmar que no hay procesos API con capacidad de escribir el archivo.
3. Repetir respaldo, checksum, análisis, aprobación, importación y conciliación con la fuente final.
4. Desplegar la API configurada para PostgreSQL con la cuenta runtime.
5. Ejecutar un smoke test de lectura y una incidencia sintética completa.
6. Habilitar escrituras solo si los checks, conteos, concurrencia y reinicio son correctos.
7. Mantener `notes.json` y los reportes originales de solo lectura según la política de retención. No iniciar dual-write.

## Reversión

Si todavía no hubo escrituras nuevas en PostgreSQL, detener la API nueva, restaurar la versión anterior y habilitar la fuente JSON intacta después de comprobar su checksum.

Si ya hubo escrituras nuevas, no volver directamente al JSON: detener escrituras, exportar el delta PostgreSQL, reconciliarlo con el respaldo y decidir entre corrección hacia adelante o restauración controlada. No ejecutar `DROP`, scripts descendentes destructivos ni copiar el JSON sobre una fuente que recibió cambios.

Ensayar restauración antes del corte:

```bash
createdb "$POSTGRES_RESTORE_DATABASE"
pg_restore --dbname="$POSTGRES_RESTORE_DATABASE" "$IMPORT_BACKUP_DIR/pre-cutover.dump"
psql "$POSTGRES_RESTORE_URL" --set ON_ERROR_STOP=on --file backend/Database/apply.sql
```

Registrar tiempo de restauración, checksum del respaldo, conteos y resultado de lectura de una incidencia conocida.

Al terminar la importación y verificar la conciliación, revocar la capacidad temporal:

```bash
psql "$POSTGRES_ADMIN_URL" --set ON_ERROR_STOP=on --set deploy_login="$POSTGRES_DEPLOY_LOGIN" <<'SQL'
REVOKE gds_db_deployer FROM :"deploy_login";
SQL
```
