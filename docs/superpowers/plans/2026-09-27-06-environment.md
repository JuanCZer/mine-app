# Objetivo 6: plan de configuración y dos archivos .env

> **Estado revisado el 18 de octubre de 2026:** **18 de 18 actividades de implementación completadas; 6 puertas de liberación externas abiertas**. La carga pública/privada, exclusiones, wrappers, reemplazo atómico y pruebas están implementados; una copia limpia con valores sintéticos aprobó instalación, build y revisión de artefactos, y la regresión consolidada actual aprobó 32/32 pruebas de entorno. El código del Objetivo 6 está implementado y verificado localmente; la liberación permanece bloqueada hasta comprobar PostgreSQL/HTTP aislado, un checkout versionado, el mapa con token autorizado y las restricciones de la cuenta Mapbox. Ningún valor secreto se documenta aquí.

**Objetivo:** tener un `.env` para el token público de Mapbox y otro para la conexión PostgreSQL privada, ambos excluidos de Git y con carga explícita comprobable.

**Arquitectura:** generar configuración Angular desde una allowlist pública de un solo campo; cargar configuración privada únicamente al iniciar backend. En despliegue, aceptar variables/secretos inyectados por la plataforma con precedencia sobre archivos locales.

**Tecnología:** Angular 22, Node compatible con el proyecto y `util.parseEnv`, .NET 10/IConfiguration, PostgreSQL.

**Especificación:** objetivo 6 del usuario; usar PostgreSQL y separar configuración Mapbox de conexión de BD. No se han proporcionado credenciales ni dominio productivo para rellenar valores reales.

## Situación inicial y distinción necesaria

Al levantar el plan, `mapbox.config.ts` contenía un token público `pk` versionado, los `.env` no estaban ignorados y no existía un cargador explícito. La implementación retiró el literal, añadió allowlists, wrappers y reglas de exclusión. No se ha inspeccionado la cuenta Mapbox, sus permisos o restricciones; un token público del navegador no se presenta como secreto.

Un token público Mapbox utilizado por el navegador seguirá siendo visible en el bundle/red aunque su archivo fuente esté ignorado. Lo protegemos con token propio por proyecto/entorno, permisos mínimos, restricciones de URL, vigilancia de uso y rotación cuando corresponda. Estas restricciones son una mitigación, no convierten el token público en secreto. [Documentación de tokens de Mapbox](https://docs.mapbox.com/accounts/guides/tokens/).

Angular recibe únicamente `MAPBOX_PUBLIC_TOKEN` mediante el archivo generado; ASP.NET Core recibe `ConnectionStrings__Postgres` mediante el wrapper del backend. Las variables inyectadas por el entorno tienen precedencia. [Entornos Angular](https://angular.dev/tools/cli/environments), [configuración ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0).

## Contrato y archivos

| Archivo                                                                                     | Contenido/uso propuesto                                                                                                                       | Git         |
| ------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| `.env`                                                                                      | Solo `MAPBOX_PUBLIC_TOKEN`; lectura para generar configuración pública.                                                                       | Ignorado.   |
| `backend/.env`                                                                              | `ConnectionStrings__Postgres` para runtime y `POSTGRES_DEPLOY_URL` para aplicar el esquema explícitamente.                                    | Ignorado.   |
| `.env.example`                                                                              | `MAPBOX_PUBLIC_TOKEN=` y comentario de token público, sin valor real.                                                                         | Versionado. |
| `backend/.env.example`                                                                      | `ConnectionStrings__Postgres=` y `POSTGRES_DEPLOY_URL=` con descripción de uso, sin conexiones funcionales.                                  | Versionado. |
| `scripts/generate-public-env.mjs` (nuevo)                                                   | Leer `.env` raíz, aceptar variable inyectada del mismo nombre con precedencia, emitir únicamente `mapboxPublicToken`.                         | Versionado. |
| `scripts/run-backend.mjs` (nuevo)                                                           | Leer solo `backend/.env`, respetar variables ya inyectadas, iniciar `dotnet run --project backend` sin valores secretos en argumentos/salida. | Versionado. |
| `src/app/core/config/public-env.generated.ts` (generado)                                    | `PUBLIC_ENV: Readonly<{ mapboxPublicToken: string }>`; el bundle contiene solo dato público.                                                  | Ignorado.   |
| `src/app/core/config/mapbox.config.ts`                                                      | Mantener export `MAPBOX_TOKEN`, resolviendo el campo generado; eliminar literal del token actual.                                             | Versionado. |
| `.gitignore`, `backend/.gitignore`, `package.json`, `README.md`                             | Ignorar archivos, scripts y flujo documentado; limpiar regla duplicada solo si hace falta.                                                    | Versionado. |
| `proxy.conf.json`, `angular.json` y servicios Angular                                       | `/api` relativo, proxy local al backend y configuración de producción mismo origen acordada con auth.                                         | Versionado. |
| `scripts/tests/environment.test.mjs` (nuevo)                                                | Pruebas aisladas del generador/lanzador y ausencia de valores privados.                                                                       | Versionado. |

`ConnectionStrings__Postgres` se transforma en `ConnectionStrings:Postgres` para `builder.Configuration.GetConnectionString("Postgres")`. No añadir claves de DB al objeto generado ni servir `.env` como asset. Los archivos privados quedan fuera de `public/`, `src/` y output publicado; configurar despliegue para no copiar el árbol de trabajo completo.

`generatePublicConfig({ projectRoot, env }): Promise<void>` lee rutas relativas al script/proyecto, no al directorio accidental del shell. `buildBackendEnvironment({ projectRoot, env }): Promise<Record<string,string>>` combina backend `.env` con entorno del proceso, sin cargar `.env` raíz. Los entrypoints solo invocan estas funciones y preservan código de salida/señales. Usar parser de formato `.env`, nunca `eval` ni `source` de shell; Node documenta `util.parseEnv` para esta tarea. [Node util.parseEnv](https://nodejs.org/api/util.html#utilparseenvcontent).

## Restricciones y foco de revisión

- Se crean exactamente dos archivos locales `.env`, más sus dos plantillas sin secretos; no inventar contraseña ni conexión productiva.
- El generador acepta solo `MAPBOX_PUBLIC_TOKEN`; validar formato público `pk` y rechazar token secreto `sk` sin imprimirlo. Serializar el string con `JSON.stringify` para que no se interprete como código TypeScript.
- Sin token Mapbox, la aplicación puede mostrar estado explícito «Mapa no configurado» o fallar el prebuild: **propuesta de este plan: fallar prebuild/start con mensaje sin valor**. Pruebas unitarias usan token público ficticio, no conexión real a Mapbox.
- Sin conexión, backend falla al iniciar con nombre de configuración faltante, sin fallback a JSON/demo; pruebas reciben PostgreSQL aislado.
- Variables inyectadas por CI tienen precedencia; ausencia de `.env` se permite si la variable requerida existe. Permisos locales del archivo privado `0600` cuando el sistema los soporte.

Los cinco casos principales a revisar son valor faltante, `.env` con caracteres especiales, ejecución desde otro directorio, variable privada presente en CI durante build frontend y variable inyectada que debe prevalecer sobre el archivo. Cada caso tiene prueba en la tarea 1 o 2.

## Tareas

### 1. Separar Mapbox y generar únicamente configuración pública

**Consume:** token público suministrado/configurado por el responsable. **Produce:** `PUBLIC_ENV` y export `MAPBOX_TOKEN` existente, sin cambiar consumidores.

- [x] Añadir pruebas que fallen: `missingPublicTokenFailsWithoutValue`, `secretMapboxTokenIsRejected`, `malformedPublicMapboxTokensAreRejected`, `privateSentinelNeverAppearsInGeneratedOutput`, `tokensWithSyntaxCharactersAreRejectedWithoutDisclosure`, `injectedPublicVariableWins` y `worksOutsideProjectCwd`.
- [x] Implementar generador con `util.parseEnv`, allowlist exacta y escritura determinista. No copiar `process.env`, no emitir otros campos ni imprimir configuración completa.
- [x] Integrar hooks `prestart`, `prebuild`, `prewatch` y `pretest` de `package.json` para generar archivo; CI genera con variable pública suministrada o valor sintético de prueba. Documentar que invocar `ng` directamente debe ejecutar generación previa.
- [x] Cambiar `mapbox.config.ts` para consumir `PUBLIC_ENV.mapboxPublicToken`, conservar `MAPBOX_TOKEN` y comprobar integración mediante pruebas y build.
- Puerta externa `ENV-01`: comprobar visualmente `/mapa` con un token autorizado. El módulo legado `/deteccion` fue retirado del flujo activo, pero el mapa de incidencias continúa como ruta autenticada.
- [x] Crear `.env.example` vacío y `.env` local únicamente cuando se disponga del token autorizado. Evitar reutilizar silenciosamente token de otro proyecto; mantener el cambio revisable sin revocar tokens de otros productos.
- [x] Ejecutar `node --test scripts/tests/environment.test.mjs`; después `npm run build` con variable pública de prueba y marcador privado sintético en el entorno. Verificar que el marcador privado no está en `dist`, incluido source maps, sin usar una credencial real como marcador.

### 2. Cargar conexión privada al iniciar .NET

**Consume:** `backend/.env` o secret manager del entorno. **Produce:** `ConnectionStrings:Postgres` disponible para plan 03 exclusivamente en el proceso backend.

- [x] Añadir pruebas que fallen para variable ausente, valor con espacios/caracteres especiales, precedencia de entorno, `.env` raíz no leído, código de salida y secreto ausente de stdout/stderr/argumentos.
- [x] Implementar `run-backend.mjs`; configurar `npm run backend` para usarlo. Usar spawn con arreglo de argumentos y `shell: false`; no interpolar la conexión en comando ni invocar shell. Propagar señales para detener `dotnet` sin procesos huérfanos.
- [x] Crear plantilla backend vacía y documentar campos de conexión, usuario runtime con privilegios mínimos y `POSTGRES_DEPLOY_URL` separada para aplicar SQL directamente cuando proceda. La clave usada por runtime sigue siendo `ConnectionStrings__Postgres`.
- [x] En `Program.cs`/registro del plan 03, validar configuración al arrancar y usar `GetConnectionString("Postgres")`; no imprimir conexión al gestionar error de arranque.
- [x] Documentar producción: secretos inyectados por plataforma, HTTPS/red/TLS de BD según proveedor, rotación y pool de conexiones; `.env` es comodidad local y no un gestor de secretos.
- Puerta externa `ENV-02`: ejecutar wrapper y backend con PostgreSQL efímero y comprobar que la UI y los endpoints no devuelven configuración privada.

### 3. Asegurar exclusión de Git y flujo reproducible

**Consume:** archivos anteriores. **Produce:** configuración reconstruible sin secretos versionados y comandos documentados.

- [x] Añadir patrones `.env`, `.env.*`, excepciones `!.env.example` y exclusión exacta del archivo generado. Cubrir ambos niveles; no ignorar plantillas ni scripts.
- [x] Comprobar `git check-ignore .env backend/.env src/app/core/config/public-env.generated.ts`; comprobar que las dos `.env.example` no están ignoradas y `git ls-files` no contiene `.env` reales. Si hubiera alguno tracked, retirar solo del índice y preservar archivo local.
- [x] Inspeccionar historial/diff y artefactos con salida redactada. Si se demuestra secreto privado ya publicado, coordinar rotación primero; borrar del archivo actual o ignorarlo no borra historia. Token público Mapbox no se describe como secreto de base de datos.
- [x] Añadir API relativa y proxy local coordinados con plan 04; comprobar XSRF/login con el origen final. No llevar la conexión PostgreSQL al proxy Angular.
- [x] Documentar preparación desde checkout limpio, dos plantillas, variables requeridas, precedencia, comandos `npm start`, `npm run backend`, `npm run build`, y cómo regenerar tras cambiar token.
- Puerta externa `ENV-03`: completar con PostgreSQL aislado y respuestas HTTP la verificación independiente de copia limpia que ya aprobó instalación, build y revisión de artefactos con valores sintéticos.

## Puertas de liberación externas

Estas puertas validan el ambiente y la distribución final; no representan componentes de configuración pendientes. Los generadores, wrappers, plantillas, validadores y pruebas ya están implementados. Este host no dispone de Docker, Podman, PostgreSQL ni `psql`, y no tiene acceso administrativo a la cuenta propietaria de Mapbox.

| ID | Puerta de liberación | Estado actual | Evidencia necesaria para cerrarla |
| --- | --- | --- | --- |
| ENV-01 | Mapa activo con token autorizado | `/mapa` está implementado; la prueba sintética no sustituye tiles y workers reales. | Recorrido visual autenticado sin errores, con tiles, marcadores, controles y redimensionamiento correctos. |
| ENV-02 | Backend y respuestas HTTP con PostgreSQL efímero | Wrapper, API relativa, sanitización y health checks tienen pruebas locales; no hay motor aislado. | Arranque real, health/readiness y escaneo de UI, headers, cuerpos y logs sin marcadores privados. |
| ENV-03 | Copia limpia completa con PostgreSQL | La copia limpia ya aprobó `npm ci`, suite de entorno, build y revisión de artefactos sintéticos. | Repetir la parte runtime contra PostgreSQL aislado y conservar el reporte independiente final. |
| ENV-04 | Checkout versionado reproducible | Las plantillas y scripts existen en el workspace, pero todavía son cambios no confirmados. | Obtener el proyecto desde el artefacto o revisión versionada, crear solo los dos `.env` locales y repetir setup/build. |
| ENV-05 | Restricciones de la cuenta Mapbox | El frontend recibe únicamente el token público; no hay acceso a la cuenta del proveedor. | Orígenes, scopes mínimos, monitoreo y política de rotación comprobados en la cuenta propietaria. |
| ENV-06 | Ausencia de datos privados en HTTP | Bundle, source maps, assets y logs del wrapper ya están verificados. | Escaneo de respuestas/endpoints y logs del runtime con un marcador privado sintético y PostgreSQL aislado. |

## Aceptación

- Puerta externa `ENV-04`: dos `.env` locales separados e ignorados ya están comprobados; falta repetirlo desde el artefacto o checkout versionado.
- Puerta externa `ENV-05`: Mapbox obtiene solo el token público configurado; falta verificar permisos y dominios en la cuenta propietaria.
- [x] Backend recibe conexión privada y no arranca silenciosamente en modo demo/JSON; frontend funciona con API relativa.
- Puerta externa `ENV-06`: el marcador privado no aparece en bundle, source maps, assets o logs del wrapper; falta comprobar respuestas/endpoints del runtime.
- [x] Se registran resultados reales de scripts/tests/build, revisión independiente y correcciones de todas las rondas independientes y de seguridad.

La implementación del Objetivo 6 está completa. La liberación exige cerrar `ENV-01` a `ENV-06` con evidencia del ambiente correspondiente. Este plan debe preceder al arranque PostgreSQL/login de planes 03/04. No solicitar que el usuario pegue credenciales en el chat: configurarlas localmente o mediante el mecanismo de secretos elegido.
