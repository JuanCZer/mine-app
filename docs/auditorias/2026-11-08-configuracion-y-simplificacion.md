# Auditoría de configuración y simplificación

**Fecha:** 8 de noviembre de 2026. **Estado:** análisis local de código y pruebas; propuestas sin aplicar.

## Alcance y límites

Se revisaron `package.json`, configuración de Angular/TypeScript y proxy, `scripts/`, ejemplos de entorno, configuración de seguridad y PostgreSQL del servidor, importador, SQL, pruebas y documentación relacionada. La revisión de código y sus referencias no demuestra que no existan consumidores externos, tareas programadas o automatizaciones fuera del repositorio. No se leyeron ni se publicaron valores de los `.env`, no se accedió a la base remota y no se modificaron tablas, credenciales ni funcionalidades. Esta revisión focalizada no es una prueba de penetración ni una certificación de seguridad.

El repositorio tenía cambios de trabajo anteriores, incluido el traslado a `client`, `server` y `server/tests`; se conservaron. En el inicio se observaron 225 entradas de `git status --short` (número de líneas, no número de cambios funcionales). Herramientas locales: Node 24.18.0, .NET SDK 10.0.302 y Python 3.14.5. Los comandos de build y test pueden regenerar archivos ignorados y artefactos locales; no acceden intencionalmente a PostgreSQL remoto.

## Flujos y dependencias identificadas

1. `npm start`, `npm run build`, `npm run watch` y `npm test` ejecutan sus respectivos hooks `pre*`, que llaman `generate:env` → `scripts/generate-public-env.mjs` → `client/app/core/config/public-env.generated.ts`. El token `MAPBOX_PUBLIC_TOKEN` es público y queda en el frontend.
2. `npm run backend` → `scripts/run-backend.mjs` → `dotnet run --project server`. El wrapper carga una lista restringida de variables desde `server/.env`, recibe variables heredadas y filtra la conexión de despliegue.
3. `npm run db:apply` → `scripts/apply-database.mjs` → `psql` → `server/Database/apply.sql`. El wrapper verifica `verify-full` y usa `spawnWithSignals` del script de backend.
4. `npm run legacy` → `scripts/import-legacy.mjs` → `backend-import/LegacyImport.Tool.csproj` → lógica en `server/Import`; también usa `spawnWithSignals` de `run-backend.mjs`. Su ejecución es manual; no participa en el arranque ordinario.
5. `npm run build:clean` → `scripts/build-angular-clean.mjs` → Angular CLI de producción con una política adicional para advertencias. `npm run test:env` ejecuta `scripts/tests/environment.test.mjs`; `npm run test:server` descubre `server/tests` por áreas. Los dos runners PostgreSQL tienen comandos separados.
6. En el servidor, `PostgresDataSources` usa `ConnectionStrings:Postgres`, `ConnectionStrings:IdentityAdmin` y, opcionalmente, `ConnectionStrings:Catalogs`. El pool de identidad es separado. La seguridad de identidad y sus límites de frecuencia dependen de sus propias opciones y almacenamiento. Los roles de la aplicación son distintos de los roles de PostgreSQL.

## Hallazgos verificados

### H1 — Variables opcionales en `server/.env` no siempre llegan al backend

`scripts/run-backend.mjs`, función `buildBackendEnvironment`, incorpora explícitamente desde el archivo las conexiones `ConnectionStrings__Postgres`, `ConnectionStrings__IdentityAdmin` y claves HMAC indexadas; el resto de variables heredadas del entorno se conserva, excepto la credencial de despliegue filtrada. Por ello, opciones como `ConnectionStrings__Catalogs` y `Cors__AllowedOrigins__0` no se incorporan automáticamente **solo por agregarlas a `server/.env`**. El backend sí puede recibirlas como variables de entorno. No se inspeccionó el contenido real del archivo ni se afirma que el usuario esté sufriendo este problema.

**Propuesta:** documentar exactamente qué carga el wrapper y evaluar una lista explícita y probada de variables adicionales. No copiar todo el archivo indiscriminadamente: podría exponer al proceso credenciales de despliegue. Probar precedencias, índice de variables, errores y exclusión de `POSTGRES_DEPLOY_URL`.

### H2 — `build:clean` omite la generación previa de Mapbox

`package.json` tiene `prebuild`, pero no `prebuild:clean`. `build-angular-clean.mjs` invoca Angular CLI directamente, por lo que `npm run build:clean` no ejecuta el generador de configuración por sí mismo. En un checkout sin archivo generado puede fallar; con uno existente puede usar configuración anterior. No se ejecutó una prueba destructiva de borrar el archivo generado.

**Propuesta:** si se conserva, introducir una generación previa explícita y verificar checkout limpio y cambios de token. Si se retira la política de advertencias, actualizar también la matriz de QA, que la menciona.

### H3 — Documentación del wrapper incompleta

`README.md` dice que `npm run backend` carga solo la conexión runtime, pero el código también admite desde `server/.env` la conexión `IdentityAdmin` y las claves HMAC. Debe corregirse junto con H1 para evitar instrucciones ambiguas, sin mostrar valores.

La revisión estática independiente también observó que el pool `IdentityAdmin` interviene en límites de frecuencia y disponibilidad de identidad durante el inicio de sesión, no solo en la administración. El estado saludable de un endpoint concreto no demuestra por sí mismo que todas las operaciones de incidencias funcionen. `Development` permite conexiones PostgreSQL sin la verificación TLS exigida en producción y configura cookies sin `Secure`; TLS de PostgreSQL y HTTPS entre navegador y aplicación protegen tramos diferentes. No se verificó la configuración efectiva del servidor remoto.

## Matriz de decisiones

| Elemento | Clasificación | Dependencia, riesgo y alternativa | Verificación antes de aplicar |
|---|---|---|---|
| `generate-public-env.mjs` y hooks `pre*` | Adaptar/conservar función | La configuración de Mapbox y los comandos de Angular dependen de él; quitarlo sin reemplazo rompe hooks o genera configuración obsoleta. Valida token público y escritura del archivo generado. | Build limpio, arranque y tests; validar que ningún secreto privado entre al bundle. |
| `run-backend.mjs` | Adaptar | Necesario para el comando actual y compartido por `db:apply`/`legacy`; separa credencial de despliegue y gestiona señales. Se puede simplificar con un reemplazo explícito. | Arranque, precedencia, carga mínima, exclusión deploy, señales y errores sin revelar secretos. |
| `apply-database.mjs` y `db:apply` | Candidato a eliminar | El usuario aplica SQL manualmente. Su retirada también elimina verificación automática de URL/TLS y propagación de errores; conservar `server/Database/apply.sql`, que es independiente, y documentar el procedimiento manual. Verificar consumidores externos. | Pruebas del wrapper y documentación; revisar seguridad y proceso manual, sin ejecutar SQL remoto. |
| `import-legacy.mjs`, comando `legacy`, `backend-import` y `server/Import` | Pendiente de comprobar | `server/Data/notes.json` contiene cero registros; puede haber expedientes antiguos de `gds-detection-cases-v1` en perfiles de navegador no revisados. No confundir con modelos `Note` activos de incidencias. El importador registra datos heredados y conciliación. | Verificar y conciliar perfiles autorizados, copias y requisitos de retención; comprobar dependencias de proyectos, pruebas y SQL antes de retirar. |
| `build-angular-clean.mjs`, `build:clean` | Adaptar o candidato a eliminar | Añade una política de fallar ante advertencias; `npm run build` normal es independiente. La matriz de QA la referencia. Corregir H2 si se mantiene. | Revisión de CI externa, ejecución en checkout limpio, presupuesto y advertencias. |
| `scripts/tests/environment.test.mjs` | Adaptar/conservar cobertura | Comprueba generación, exclusión de secretos, precedencia, señales, despliegue y build. Eliminarlo no rompe producción, pero quita verificaciones y `test:env`. | Reasignar o eliminar únicamente las pruebas de funciones retiradas, manteniendo controles vigentes. |
| `.env.example`, `server/.env.example`, `.gitignore` | Conservar y adaptar | Separan variables públicas y privadas y excluyen `.env` y el archivo generado; los ejemplos no son secretos. Se constató que las rutas se ignoran. | `git check-ignore`; pruebas de entorno y revisión de archivos que llegarían a Git. |
| `angular.json`, `tsconfig*.json`, `proxy.conf.json` | Conservar o reemplazar de forma coordinada | Definen fuente `client`, build, tests y proxy `/api` a localhost para desarrollo. El proxy de desarrollo no configura el de producción. | Build/test Angular, navegación local y plan de proxy en despliegue. |
| `server/Notes.Api.csproj`, exclusiones `tests/**` | Conservar | Las exclusiones evitan incorporar código y contenido de pruebas al proyecto web; la exclusión de la carpeta no elimina las pruebas. | Build Release y revisión de artefactos. |
| `server/tests` y harness | Adaptar por área, conservar cobertura | Hay pruebas de múltiples servicios y de integración; algunas usan proveedores exclusivos de Testing. Eliminar dobles de prueba puede afectar la suite aunque no afecte el runtime. | Suite completa y pruebas específicas de flujos alterados. |
| `server/Program.cs`, `Notes:Provider=Json`, `Catalogs:Provider=InMemory` | Pendiente de evaluación | Los proveedores no PostgreSQL están limitados a Testing y se usan para pruebas; no implican que el flujo de producción use JSON. | Inventariar tests que dependen de ellos antes de sustituirlos. |
| `server/Database/apply.sql` | Conservar | Es el SQL completo para aplicación manual, independiente del wrapper. Incluye soporte legado y objetos compartidos; retirar fragmentos exige análisis de datos e integridad. | Revisión de esquema, copias y pruebas en instancia aislada; SQL manual revisable para el servidor. |
| `ConnectionStrings__Postgres`, `ConnectionStrings__IdentityAdmin`, HMAC | Conservar responsabilidades; evaluar configuración | Las conexiones y seguridad cumplen papeles distintos. Unificarlas o eliminar las claves sin rediseño puede romper login, administración y límites de frecuencia. | Tests de autenticación, identidad y permisos; revisar roles reales separadamente y sin cambios en remoto. |
| `ConnectionStrings__Catalogs`, CORS, `DataProtection:KeyRingPath`, proxy confiable | Conservar opciones según despliegue; documentar carga | Catálogos tiene fallback a conexión principal; CORS y DataProtection dependen del entorno y arquitectura. No asumir que una opción ausente en el archivo de ejemplo es inútil. | Tests de configuración, orígenes, proxy y persistencia de sesión en el entorno correspondiente. |
| Política TLS PostgreSQL y cookies/CSRF | Conservar controles | Producción exige `VerifyFull`; Development/Testing permite excepción y avisa. Sesión y antiforgery usan cookies y políticas de seguridad; el login y endpoints tienen controles adicionales. No se comprobó la conexión remota durante esta auditoría. | Pruebas TLS y sesión; validación de certificados, red y despliegue cuando estén disponibles. |

## Prioridades y lotes sugeridos

1. **Documentación y diagnóstico (bajo riesgo):** corregir H3, explicar qué variables se toman del archivo y cuáles del entorno; inventariar consumidores externos. Aceptación: guía coherente con el código y revisión de ejemplos. Reversión: restaurar documentación.
2. **Configuración de Mapbox y build (riesgo bajo-medio):** decidir conservar o retirar `build:clean`; si se conserva, corregir H2. Aceptación: pruebas de generador y build en checkout limpio, sin divulgar el token. Reversión: restaurar hook/wrapper.
3. **Carga de configuración de backend (riesgo medio):** evaluar agregar solo opciones requeridas a una lista explícita o sustituir el wrapper por un mecanismo equivalente. Aceptación: pruebas de precedencia y secretos, arranque y flujos de autenticación. Reversión: mantener wrapper/configuración previos documentados.
4. **SQL manual (riesgo medio):** solo después de confirmar que `db:apply` no se usa en automatizaciones, considerar retirar el wrapper y su comando; mantener SQL y un procedimiento manual seguro con manejo de errores. Aceptación: documentación y pruebas correspondientes; no cambiar DB. Reversión: restaurar script y comando.
5. **Importación heredada (riesgo de pérdida de recuperación):** verificar perfiles, respaldos y retención antes de decidir. Si se retira, analizar por separado CLI, lógica, pruebas y objetos SQL; no borrar registros ni tablas como consecuencia automática. Aceptación: conciliación documentada y tests de incidencias. Reversión: conservar código y respaldos hasta cerrar la decisión.
6. **Proveedores de Testing y otras opciones (riesgo medio-alto):** revisar solo con evidencia de su uso, sustitutos y costo de pruebas. No reemplazar mecanismos de seguridad o identidades por razones de nombres.

## Comprobaciones y pendientes

- `.env`, `server/.env` y `client/app/core/config/public-env.generated.ts` son ignorados por Git según `git check-ignore`; no se inspeccionaron sus valores.
- `server/Data/notes.json`: arreglo vacío, cero registros. No se inspeccionaron perfiles de navegador; la existencia y relevancia del almacén local histórico siguen sin comprobar.
- Las pruebas PostgreSQL con Docker, los permisos y el estado de la base remota, las configuraciones externas de CI y la conexión TLS real no se comprobaron en esta auditoría. Los hallazgos relativos al entorno remoto son límites o riesgos de diseño, no diagnóstico del estado real.
- `npm run test:env`: 38/38; `npm test -- --watch=false`: 149/149; `npm run test:server`: 254/254.
- `npm run build` terminó correctamente. `dotnet build server/Notes.Api.csproj -c Release --nologo -warnaserror` y `dotnet build backend-import/LegacyImport.Tool.csproj --nologo` terminaron sin advertencias ni errores. No se ejecutaron los runners PostgreSQL ni se comprobó la conectividad remota.

## Criterios de seguridad y recuperación

Antes de cada lote: conservar una referencia recuperable de los archivos y configuración sin incluir secretos, identificar consumidores externos, comprobar criterios de aceptación y registrar el resultado. Antes de tocar datos o esquema, elaborar SQL revisable para ejecución manual y un respaldo verificado; esta auditoría no autoriza ni ejecuta cambios de base de datos. La restauración del código no revierte escrituras en PostgreSQL, por lo que la recuperación de datos requiere un plan independiente.

## Actualización de implementación — 8 de noviembre de 2026

H1, H2 y H3 fueron atendidos: el wrapper incorpora explícitamente las opciones de catálogos y CORS desde `server/.env`, `build:clean` genera primero la configuración de Mapbox y se actualizó la documentación. Se verificaron precedencia y filtrado de credenciales, además de la generación en un proyecto temporal sin archivo previo. Resultado: 41 pruebas de configuración y 149 de Angular aprobadas; `npm run build:clean` terminó correctamente. Los candidatos a retirar `db:apply` y el importador siguen pendientes de verificar consumidores externos y datos heredados, respectivamente.

**Actualización de rutas:** posteriormente, `server/Notes.Api.csproj` fue renombrado a `server/MineApp.Api.csproj`. Las menciones anteriores describen la ruta y los resultados vigentes al momento de la auditoría.
