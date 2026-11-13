# GDS · Incidencias y validación por fases

Aplicación Angular con API ASP.NET Core. Cada incidencia reúne el registro de indicios, las evidencias y las cuatro fases del proceso: **indicios → reconocimiento → vigilancia → mitigación**.

La ruta inicial `/incidencias` muestra una secuencia de cuatro puntos. Guardar un borrador no permite avanzar: cada fase debe validarse antes de habilitar la siguiente, con comprobaciones tanto en la interfaz como en la API.

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
