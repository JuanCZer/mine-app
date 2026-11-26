# GDS · Incidencias y validación por fases

Aplicación Angular con API ASP.NET Core. Cada incidencia reúne el registro de indicios, las evidencias y las cuatro fases del proceso: **indicios → reconocimiento → vigilancia → mitigación**.

La ruta inicial `/incidencias` permite navegar y capturar datos en las cuatro fases desde una incidencia nueva, incluso antes del primer guardado. Para conservar los borradores en el servidor, primero se registra la incidencia con sus datos generales y después se guarda cada fase, sin tener que completar ni validar las anteriores. Cada fase muestra sus requisitos pendientes. La validación formal conserva su orden y requisitos, con comprobaciones tanto en la interfaz como en la API.

[Campos, reglas de validación y contrato de la API](docs/note-phases.md).

## Configuración local

Desde un checkout nuevo, crea los dos archivos locales y limita sus permisos:

```bash
cp .env.example .env
cp server/.env.example server/.env
chmod 600 .env server/.env
```

El `.env` raíz contiene únicamente `MAPBOX_PUBLIC_TOKEN`, un token público `pk`
propio de este proyecto. `npm start`, `npm run build`, `npm run watch` y
`npm test` y `npm run build:clean` generan `client/app/core/config/public-env.generated.ts` desde una
allowlist de ese único campo. El token queda visible en el bundle y en las
solicitudes del navegador: configura en Mapbox permisos mínimos, URLs permitidas,
monitoreo y rotación. Después de cambiarlo, ejecuta `npm run generate:env`.
Si invocas `ng` directamente, ejecuta antes ese mismo comando.

`server/.env` contiene `ConnectionStrings__Postgres`, `ConnectionStrings__IdentityAdmin`
y las claves HMAC de identidad. `npm run backend` carga esas variables y, si están
presentes, `ConnectionStrings__Catalogs` y los orígenes indexados
`Cors__AllowedOrigins__N`. Las variables ya inyectadas en el entorno tienen
precedencia sobre el archivo. Otras opciones del servidor, como
`DataProtection__KeyRingPath`, deben inyectarse en el entorno del proceso.

`POSTGRES_DEPLOY_URL` se usa para aplicar el esquema según
[la guía de base de datos](server/Database/README.md); `npm run backend` no
entrega esa credencial al proceso de la API. `npm run db:apply` carga únicamente la conexión de
despliegue y ejecuta `psql` sin colocarla en argumentos ni imprimirla; requiere
que `psql` esté disponible en `PATH`.

La importación heredada usa la misma credencial de despliegue mediante un proceso
separado: `npm run legacy -- analyze|import`. No forma parte del arranque de la API.
El procedimiento, la aprobación por huella y la reanudación del lote están en el
[runbook de corte](docs/runbooks/postgresql-cutover.md).

En producción, inyecta las conexiones y claves privadas con el gestor de secretos de la plataforma.
Los `.env` son solo una comodidad local: no se copian al artefacto ni sustituyen
HTTPS, TLS de PostgreSQL, rotación, restricciones de red o un pool configurado
para el proveedor.

La API autoriza `localhost:4200` y `localhost:4317` únicamente en Development.
En otros entornos configura cada origen exacto del frontend mediante variables
indexadas, por ejemplo `Cors__AllowedOrigins__0=https://operaciones.example.gob.mx`.
La lista admite orígenes HTTP(S) sin rutas y rechaza loopback fuera de Development.

## Ejecutar

Desde la raíz del proyecto, en dos terminales:

```bash
npm run backend
npm start
```

Abre `http://localhost:4200`. El acceso requiere una cuenta aprovisionada y mantiene la sesión en una cookie HttpOnly; las mutaciones usan protección CSRF. La API usa PostgreSQL por defecto y obtiene la conexión desde `ConnectionStrings__Postgres`. Consulta [Aprovisionamiento de usuarios](server/Auth/README.md) y [aplicación directa del esquema](server/Database/README.md).

En `Development`, la API publica Swagger UI en `http://localhost:5068/swagger` y el documento OpenAPI en `/swagger/v1/swagger.json`. Usa el flujo `GET /api/auth/csrf`, `POST /api/auth/login` y luego los endpoints protegidos. La UI conserva cookies del mismo origen y reenvía el token CSRF legible en las mutaciones `/api`; no incluye credenciales de ejemplo. Swagger responde 404 fuera de `Development`.

El historial `/historial` muestra el avance de cada incidencia y permite abrir sus fases. `/mapa` muestra las coordenadas. La propiedad histórica `altitude` contiene la latitud y se mantiene por compatibilidad.

El [panel local anterior](docs/detection-module.md) fue retirado: `/deteccion` redirige a `/incidencias`. Su archivo local anterior permanece intacto únicamente para recuperación y conciliación controladas.

## Idiomas EN / ESP

El selector junto al usuario conectado (y en acceso) cambia la interfaz sin recargar ni perder formularios, borradores o la vista del mapa. Español es el idioma inicial; la preferencia queda guardada en este navegador con la clave `mine.ui.language`. Si el navegador bloquea el almacenamiento, la selección funciona durante la sesión.

Todos los textos de interfaz se editan en `client/app/core/i18n/`. El archivo [dictionary.ts](client/app/core/i18n/dictionary.ts) reúne los módulos:

| Archivo | Textos |
| --- | --- |
| `account-dictionary.ts` | Encabezado, menú, acceso, perfil y usuarios |
| `workflow-dictionary.ts` | Incidencias, fases, campos, ayudas y requisitos |
| `map-dictionary.ts` | Mapa, ventanas, historial y ficha |
| `api-dictionary.ts` | Errores del servidor, eventos y etiquetas estándar de catálogos |
| `common-dictionary.ts` | Mensajes y acciones compartidas |

Cada módulo exporta `es` y `en` con las mismas claves. Para cambiar un texto existente, modifica sus valores en ambos idiomas; no hace falta editar el HTML. Por ejemplo, el título de la primera fase está en `workflow.layers.indicationsTitle` y la vista lo consume como `{{ dictionary.workflow.layers.indicationsTitle }}`.

Para añadir texto nuevo, agrega la misma clave a ambos diccionarios y úsala desde el componente:

```ts
readonly i18n = inject(I18nService);
get dictionary() { return this.i18n.dictionary(); }
```

Usa `i18n.format(plantilla, { count })` para parámetros, `i18n.date()` / `i18n.number()` para formatos y `i18n.message()` con `{ key, params }` para avisos persistentes. Guardar una traducción resuelta en un aviso impediría que ese aviso se actualice al alternar el idioma. Las pruebas verifican claves y parámetros equivalentes en ES/EN.

Los códigos internos, nombres, notas, evidencias y etiquetas históricas se conservan. Los catálogos estándar se presentan traducidos por código; las opciones desconocidas mantienen su etiqueta original. Los eventos nuevos incluyen metadatos traducibles, mientras que el historial anterior conserva su texto. Los errores del servidor añaden `code`, `parameters` e `issueDetails` sin eliminar los mensajes anteriores. No se requiere migración de base de datos para este cambio.

Cambiar EN/ESP es inmediato. **Editar el diccionario en código requiere compilar y desplegar la nueva versión.** El [plan de implementación](docs/superpowers/plans/2026-11-25-language-dictionary.md) detalla el alcance y la verificación.

## Verificar

```bash
npm run test:env
npm run build
npm run build:clean
npm test -- --watch=false
dotnet build server/MineApp.sln
npm run test:server
```

La solución `server/MineApp.sln` agrupa la API, el importador y los proyectos auxiliares .NET de pruebas. `dotnet build server/MineApp.sln` compila los proyectos; la suite del servidor se ejecuta con `npm run test:server` porque los proyectos auxiliares son invocados por pruebas Python.

`npm run build:clean` genera primero la configuración pública y ejecuta el Angular CLI local con `ng build --configuration production` sin shell, reemite la salida del proceso hijo y falla solo si el build devuelve un warning Angular `▲ [WARNING]` o un presupuesto excedido `exceeded maximum budget`. El wrapper preserva el exit code del hijo para cualquier error distinto de esos dos patrones.

## Docker y despliegue privado

La preparación de Docker Compose y GitHub Actions para ejecutar el frontend y la API en un mismo contenedor web se describe en [la guía de despliegue privado](docs/runbooks/docker-github-actions.md). La ejecución en producción requiere un proxy HTTPS, configuración privada y PostgreSQL con TLS verificable.

El flujo propuesto para proteger `main`, integrar PR y desplegar ramas `release/*` está documentado en [integración y release](docs/runbooks/github-flujo-main-release.md). La plantilla de protección y las automatizaciones de integración y producción se dejan desactivadas inicialmente.

## Despliegue provisional

La pipeline provisional y su preparación para el servidor privado se describen en [deploy/provisional/README.md](deploy/provisional/README.md). Los workflows de producción e integración futura están conservados como plantillas inactivas en `deploy/futuro/workflows/`.
