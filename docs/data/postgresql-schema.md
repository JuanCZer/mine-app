# Esquema PostgreSQL de incidencias

## Alcance

El esquema relacional conserva el contrato HTTP actual y separa datos operativos, identidad, auditoría e importación. Los identificadores internos son `uuid`; `incidents.public_id`, `phase_evidence.client_id` e `incident_events.client_id` conservan identificadores legados sin exigir que sean UUID. Las fechas de sistema son `timestamptz`. Una fecha antigua sin zona se conserva además en `*_original` y no se convierte a un instante hasta disponer de una zona explícita.

Los catálogos de Objetivo 2 siguen siendo la fuente canónica. Las FK compuestas `(catalog_key, code)` impiden que un código de un catálogo se use como si perteneciera a otro. Desactivar una opción no rompe la lectura histórica; la API comprueba `is_active` con `FOR SHARE` dentro de la misma transacción de cada escritura. El snapshot inmutable de una validación conserva código, etiqueta y versión.

## Archivo único y orden

`backend/Database/apply.sql` es el único SQL entregable y la fuente de verdad del esquema. Es SQL autocontenido, no contiene comandos especiales de `psql` ni depende de rutas del repositorio, por lo que puede copiarse directamente al servidor y ejecutarse también desde pgAdmin. Una sola transacción instala o reaplica el contrato actual y revierte todo el lote si una comprobación detecta una estructura preexistente incompatible. No se usan migraciones EF.

Sus secciones están incluidas físicamente en este orden:

1. tablas convencionales de ASP.NET Core Identity con PK UUID, sin cuentas ni contraseñas;
2. DDL canónico de catálogos;
3. incidencias, cuatro fases tipadas, revisión y selecciones múltiples;
4. evidencias, firmas de validación, eventos de dominio y auditoría;
5. lotes, originales y correspondencias de importación;
6. inicialización de fases, orden de validación, inmutabilidad y transiciones;
7. semillas canónicas repetibles;
8. roles SQL `NOLOGIN` y privilegios, sin crear usuarios ni secretos;
9. comprobaciones de integridad estructural;
10. reporte de conciliación de importación.

Desde el repositorio:

```sh
psql "$POSTGRES_DEPLOY_URL" --set ON_ERROR_STOP=on -f backend/Database/apply.sql
```

Si el archivo ya fue copiado al servidor, ejecute `psql "$POSTGRES_DEPLOY_URL" --set ON_ERROR_STOP=on -f apply.sql`. Todo cambio de estructura, restricción, índice, función, trigger, semilla o comprobación se edita directamente en este archivo; la aplicación no modifica el esquema durante el arranque.

La cadena se proporciona al proceso, nunca se escribe en el repositorio. La cuenta de despliegue requiere capacidad para crear extensión, esquema, tablas, funciones, triggers y roles, y recibe temporalmente `gds_db_deployer`. La cuenta de ejecución recibe `gds_app_runtime`; no puede modificar validaciones/eventos ya emitidos, borrar incidencias ni leer las cargas originales de importación. El acceso de Identity en `public` se termina de restringir al implementar el Objetivo 4.

`npm run db:apply` acepta únicamente una URL PostgreSQL con un solo
`sslmode=verify-full` y falla antes de iniciar `psql` ante un modo ausente, débil o
ambiguo. La ejecución directa de `psql` mostrada arriba sigue siendo responsabilidad
del operador y debe usar la misma política.

La importación heredada usa el ejecutable separado `backend-import/LegacyImport.Tool.csproj` mediante `npm run legacy -- analyze|import`. Solo recibe `POSTGRES_DEPLOY_URL`, exige `VerifyFull`, una conexión miembro de `gds_db_deployer` y un aprobador activo con rol `administrador`. La API no carga esa credencial y nunca inicia importaciones durante su arranque.

## Diccionario operativo

| Área        | Tabla                                                                | Garantía principal                                                                                   |
| ----------- | -------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| Incidencia  | `app.incidents`                                                      | `public_id` único, revisión no negativa, coordenadas válidas, borrado lógico                         |
| Fases       | `app.incident_phases`                                                | Una fila por incidencia y etapa; cuatro filas creadas automáticamente                                |
| Revisión    | `app.phase_reviews`                                                  | Hallazgo y entorno con catálogo tipado; el nombre declarado no autentica al revisor                  |
| Fase 1      | `app.phase_indications`                                              | Plataforma, métricas LiDAR, análisis diferencial, alerta, hora, fuente y profundidad                 |
| Fase 1 N:N  | `app.phase_indication_signatures`                                    | Firmas del terreno sin duplicados                                                                    |
| Fase 2      | `app.phase_reconnaissance`                                           | Magnetometría, GPR, RTK, localización, riesgo y fusión                                               |
| Fase 3      | `app.phase_surveillance`                                             | Periodo, despliegue, canales, dispositivos, actividad y decisión                                     |
| Fase 3 N:N  | `app.phase_radio_technologies`, `app.phase_surveillance_methods`     | Tecnologías y métodos sin duplicados                                                                 |
| Fase 4      | `app.phase_mitigation`                                               | Protección, medios, interrogación, artefacto, EOD, resultado y TECHINT                               |
| Evidencia   | `app.phase_evidence`                                                 | ID del cliente único dentro de la fase y sensor de catálogo                                          |
| Validación  | `app.phase_validations`                                              | Una firma inmutable por fase, actor real y snapshot JSON con datos y etiquetas/versiones de catálogo |
| Historia    | `app.incident_events`                                                | Línea de tiempo append-only y revisión asociada                                                      |
| Auditoría   | `app.audit_events`                                                   | Acción, resultado, entidad, correlación y cambios; las opciones registran `before`/`after` y rol SQL |
| Importación | `app.import_batches`, `app.legacy_records`, `app.legacy_batch_records`, `app.legacy_id_mappings` | Huella SHA-256, original exacto, membresía por ocurrencia e importación idempotente                   |

Los campos numéricos admiten `NULL` durante el borrador y solo imponen rangos físicos básicos. Los requisitos de completitud, mínimos operativos y sensores obligatorios se evalúan al validar; así no se bloquea un borrador parcial. Las tablas de detalle llevan una FK compuesta `(phase_id, stage_code)`, lo que impide colocar datos de una fase en otra.

## Guardas de flujo

Al crear una incidencia, `app.initialize_incident_phases` crea las cuatro fases, sus revisiones y su detalle tipado. Un trigger diferido comprueba al confirmar la transacción que no falte ninguna fila. La validación bloquea la incidencia con `FOR UPDATE`, exige todas las fases anteriores y rechaza la segunda firma de una misma fase mediante `UNIQUE(phase_id)`.

Después de validar una fase, sus datos, selecciones y evidencias quedan inmutables. La validación de mitigación cierra la incidencia. Una validación de reconocimiento cuyo dictamen sea `false-positive` la descarta. Una incidencia cerrada, descartada o eliminada no puede reabrirse por una actualización directa. `phase_validations`, `incident_events` y `audit_events` son append-only mediante privilegios y triggers.

Las validaciones nuevas de origen `authenticated` exigen una FK a un usuario real. Una validación `legacy` solo puede insertarla una conexión miembro de `gds_db_deployer`; `gds_app_runtime` no puede usar ese origen para evadir la identidad del actor.

Estas guardas complementan la autorización y las validaciones de negocio del backend; no sustituyen comprobaciones de acceso, vigencia de catálogo ni el control de revisión optimista en el repositorio.

## Importación y conciliación

Cada archivo se registra por `(source, source_sha256)`. Cada original se deduplica por `(source, source_record_id, source_hash)`. `app.legacy_batch_records` vincula cada posición `(batch_id, source_index)` con ese original, incluso cuando otro lote ya lo había archivado, y conserva la disposición y el motivo propios de esa ocurrencia. Así un original importado puede quedar en conciliación dentro de otro archivo que lo duplique, sin duplicar la evidencia protegida. `original_payload_json` guarda los bytes de texto normalizados por el importador como evidencia de contenido; `parsed_payload` permite análisis sin sustituir el original. Un registro importado debe tener una correspondencia de incidente.

El cierre calcula los totales acumulados desde esos vínculos. Solo marca `completed` o `completed_with_errors` cuando el número de posiciones vinculadas coincide con `record_count` y no queda ningún original `pending`; en otro caso conserva `running` y el comando falla. Una reejecución informa sus registros como ya procesados, pero conserva el total acumulado de cuarentenas y mantiene el código de salida `2` mientras requiera conciliación.

La última sección de `apply.sql` muestra conteos por lote, importados sin correspondencia, correspondencias sin destino y pendientes. Después de importar, vuelva a ejecutar el mismo archivo completo; es idempotente y vuelve a validar la estructura antes de emitir la conciliación. Antes del corte, los tres últimos resultados deben estar vacíos y los conteos deben coincidir con el informe aprobado. El archivo original se respalda fuera de la base con acceso restringido y su SHA-256 debe coincidir con `import_batches.source_sha256`.

## Validación y límites actuales

La penúltima sección de `apply.sql` comprueba tablas, etapas, triggers y cardinalidad. La segunda ejecución del archivo no duplica catálogos ni roles. Para generar la huella revisable de esta entrega:

```sh
shasum -a 256 backend/Database/apply.sql
```

No había una instancia PostgreSQL ni `psql` disponible al preparar este archivo, por lo que aún falta ejecutar instalación limpia, segunda ejecución, fallos de constraints y conciliación contra una base aislada. `backend/Database/apply.sql` es la fuente final de verdad, incluidas las tablas compatibles con Identity. La activación de usuarios, roles de aplicación y permisos HTTP pertenece al Objetivo 4.
