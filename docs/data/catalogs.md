# Catálogos canónicos

## Contrato

PostgreSQL es la única fuente de opciones de negocio fuera del entorno de pruebas. Por defecto usa `ConnectionStrings__Postgres`; `ConnectionStrings__Catalogs` solo se configura cuando el despliegue requiere una conexión de lectura separada. `GET /api/catalogs?keys=soils,seasons&stage=indications&includeInactive=false` devuelve códigos estables y etiquetas ordenadas:

```json
{
  "version": 1,
  "catalogs": {
    "soils": [
      { "id": "…", "code": "unknown", "label": "Sin determinar", "sortOrder": 1, "active": true }
    ]
  }
}
```

`keys` acepta únicamente claves de la lista cerrada de la API. `stage` acepta `indications`, `reconnaissance`, `surveillance` o `mitigation`. La API ordena por `sortOrder, code`. `includeInactive=true` sirve para reconstruir registros históricos; una opción inactiva no puede introducirse en una incidencia nueva.

El código es identidad y no se renombra. La etiqueta puede cambiar sin romper registros. Una opción utilizada se retira con `is_active=false`, nunca con `DELETE`. `version` aumenta al editar una fila. Cada validación congela código, etiqueta y versión en `phase_validations.snapshot`; la API devuelve ese `catalogSnapshot` y la interfaz lo usa para presentar el significado histórico aunque la etiqueta vigente cambie después.

## Matriz

| Clave API                 | Tabla PostgreSQL                   | Códigos iniciales                                                                                                   | Fase / consumidor                                                             | Semántica protegida                                          |
| ------------------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- | ------------------------------------------------------------ |
| `mexicoStates`            | `app.cat_mexico_states`            | 32 entidades federativas con código ASCII                                                                           | Panel local                                                                   | No                                                           |
| `soils`                   | `app.cat_soils`                    | `unknown`, `vertisol`, `andosol`, `leptosol`                                                                        | Todas                                                                         | No                                                           |
| `seasons`                 | `app.cat_seasons`                  | `unknown`, `dry`, `rainy`                                                                                           | Todas                                                                         | No                                                           |
| `riskLevels`              | `app.cat_risk_levels`              | `low`, `medium`, `high`                                                                                             | Global: prioridad del módulo local y probabilidad/prioridad de reconocimiento | Sí, la fase 2 exige clasificar probabilidad y prioridad      |
| `sensors`                 | `app.cat_sensors`                  | `LiDAR`, `RGB`, `IR`, `GPR`, `Magnetometría`, `EO/IR`, `IMSI/IMEI/TMSI`, `EOD`, `TECHINT`, `Observación`, `Informe` | Evidencia, con alcance por fase                                               | Sí, las evidencias mínimas siguen en código                  |
| `platforms`               | `app.cat_platforms`                | `if1200a`, `husky-route-clearance`                                                                                  | Fases 1 y 4                                                                   | Sí                                                           |
| `indicationSources`       | `app.cat_indication_sources`       | `if1200a-lidar`, `if1200a-lidar-rgb-ir`                                                                             | Fase 1                                                                        | Sí                                                           |
| `indicationSignatures`    | `app.cat_indication_signatures`    | `surface-removal`, `irregular-edge`, `vegetation-change`, `soil-density-change`                                     | Fase 1                                                                        | `surface-removal` activa la regla de profundidad             |
| `radioTechnologies`       | `app.cat_radio_technologies`       | `2G`, `3G`, `4G`, `5G`                                                                                              | Fase 3                                                                        | No                                                           |
| `surveillanceMethods`     | `app.cat_surveillance_methods`     | 14 procesos de las páginas 27, 30 y 31 del PDF                                                                      | Fase 3                                                                        | Sí, algunos satisfacen requisitos de captura/geolocalización |
| `surveillanceDeployments` | `app.cat_surveillance_deployments` | `uav`, `ground-backpack`, `combined`                                                                                | Fase 3                                                                        | Sí                                                           |
| `humanActivities`         | `app.cat_human_activities`         | `not-detected`, `movement-detected`, `irregular-pattern`, `custody-or-activation-risk`                              | Fase 3                                                                        | Sí                                                           |
| `environmentDecisions`    | `app.cat_environment_decisions`    | `clear-for-mitigation`, `hold-for-reassessment`                                                                     | Fase 3                                                                        | Sí                                                           |
| `protectionLevels`        | `app.cat_protection_levels`        | `stanag-4`, `stanag-5`                                                                                              | Fase 4                                                                        | Sí                                                           |
| `neutralizationResults`   | `app.cat_neutralization_results`   | `neutralized`, `not-neutralized`                                                                                    | Fase 4                                                                        | Sí                                                           |
| `routeResults`            | `app.cat_route_results`            | `route-opened`, `not-applicable`                                                                                    | Fase 4                                                                        | Sí                                                           |
| `findings`                | `app.cat_findings`                 | `pending`, `confirmed`, `inconclusive`, `false-positive`                                                            | Revisión                                                                      | Sí, `false-positive` cierra en fase 2                        |
| `environmentReviews`      | `app.cat_environment_reviews`      | `pending`, `blocked`, `reviewed`                                                                                    | Revisión                                                                      | Sí                                                           |
| `workflowStatuses`        | `app.cat_workflow_statuses`        | `open`, `closed`, `discarded`                                                                                       | Etiquetas y filtros                                                           | Sí, el servidor calcula el estado                            |
| `workflowStages`          | `app.workflow_stages`              | las cuatro fases en ordinal 1–4                                                                                     | Navegación y filtros                                                          | Sí, orden fijo                                               |

Las tablas son particiones de `app.catalog_options`. Esto mantiene nombres explícitos para la lista blanca de consultas y centraliza restricciones, vigencia, versión y auditoría temporal. `stage_codes` controla el alcance por fase. `workflowStages.metadata.pdfLayer` conserva la relación documental 0, 1, 3, 2 sin confundirla con el orden operativo 1–4.

## Etiquetas contextuales

Un mismo código conserva una etiqueta canónica en la API. La interfaz puede anteponer contexto que no altere el significado, por ejemplo «Probabilidad: Alta» y «Prioridad: Alta» usan `riskLevels/high`; el valor persistido continúa siendo `high`. `riskLevels` tiene alcance global porque también alimenta la prioridad del módulo local, incluso cuando la consulta lleva `stage=indications`. Placeholders como «Selecciona» y filtros como «Todos» son texto de interfaz, no entradas de catálogo.

Los estados mexicanos usan códigos sin acentos (`michoacan`) y etiquetas con ortografía de presentación (`Michoacán`). El campo libre de área o corredor de una incidencia no se sustituye por `mexicoStates`.

## Operación y límites de esta entrega

Ejecutar el único archivo autocontenido `backend/Database/apply.sql` con una identidad autorizada para crear el esquema `app`. Sus secciones 2 y 7 crean y siembran los catálogos. La aplicación recibe la conexión mediante `ConnectionStrings__Catalogs`; no hay credenciales en el archivo. Las semillas son repetibles y no reactivan opciones retiradas: el `ON CONFLICT` actualiza etiqueta, orden y alcance, pero conserva `is_active`.

Las altas y modificaciones posteriores de opciones generan un registro append-only en `app.audit_events` con valores `before`/`after`, versión y rol de base de datos. Una operación administrativa puede atribuir además una cuenta de aplicación existente dentro de la misma transacción:

```sql
BEGIN;
SET LOCAL app.actor_user_id = 'UUID-DE-USUARIO-AUTORIZADO';
UPDATE app.catalog_options
SET label = 'Nueva etiqueta revisada'
WHERE catalog_key = 'soils' AND code = 'andosol';
COMMIT;
```

El proveedor `InMemory` existe solo si `ASPNETCORE_ENVIRONMENT=Testing` y `Catalogs__Provider=InMemory`; la aplicación rechaza esa combinación fuera de pruebas. Producción usa `PostgreSql` y responde `503` genérico si no puede consultar la base, sin recurrir a listas compiladas.

Las incidencias usan PostgreSQL por defecto; el proveedor JSON queda limitado al entorno `Testing`. El repositorio valida pertenencia y vigencia mediante la misma conexión y transacción que guarda la fase. Cada opción seleccionada se lee con `FOR SHARE`, por lo que una modificación administrativa espera hasta que el guardado confirme o revierta y no puede abrir una ventana entre validación y escritura.
