# GDS · Incidencias y validación por fases

Aplicación Angular con API ASP.NET Core. Cada incidencia reúne el registro de indicios, las evidencias y las cuatro fases del proceso: **indicios → reconocimiento → vigilancia → mitigación**.

La ruta inicial `/incidencias` muestra una secuencia de cuatro puntos. Guardar un borrador no permite avanzar: cada fase debe validarse antes de habilitar la siguiente, con comprobaciones tanto en la interfaz como en la API.

[Campos, reglas de validación y contrato de la API](docs/note-phases.md).

## Configuración local

Desde un checkout nuevo, crea los dos archivos locales y limita sus permisos:

```bash
cp .env.example .env
cp backend/.env.example backend/.env
chmod 600 .env backend/.env
```

El `.env` raíz contiene únicamente `MAPBOX_PUBLIC_TOKEN`, un token público `pk`
propio de este proyecto. `npm start`, `npm run build`, `npm run watch` y
`npm test` generan `src/app/core/config/public-env.generated.ts` desde una
allowlist de ese único campo. El token queda visible en el bundle y en las
solicitudes del navegador: configura en Mapbox permisos mínimos, URLs permitidas,
monitoreo y rotación. Después de cambiarlo, ejecuta `npm run generate:env`.
Si invocas `ng` directamente, ejecuta antes ese mismo comando.

`backend/.env` contiene `ConnectionStrings__Postgres` para la cuenta runtime de
privilegios mínimos y `POSTGRES_DEPLOY_URL` para aplicar el esquema según
[la guía de base de datos](backend/Database/README.md). `npm run backend` carga
solo la conexión runtime en el proceso de la API; la credencial de despliegue no
se le entrega. Las variables ya inyectadas en el proceso tienen precedencia sobre
los archivos locales. `npm run db:apply` carga únicamente la conexión de
despliegue y ejecuta `psql` sin colocarla en argumentos ni imprimirla; requiere
que `psql` esté disponible en `PATH`.

La importación heredada usa la misma credencial de despliegue mediante un proceso
separado: `npm run legacy -- analyze|import`. No forma parte del arranque de la API.
El procedimiento, la aprobación por huella y la reanudación del lote están en el
[runbook de corte](docs/runbooks/postgresql-cutover.md).

En producción, inyecta ambos valores con el gestor de secretos de la plataforma.
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

Abre `http://localhost:4200`. El acceso requiere una cuenta aprovisionada y mantiene la sesión en una cookie HttpOnly; las mutaciones usan protección CSRF. La API usa PostgreSQL por defecto y obtiene la conexión desde `ConnectionStrings__Postgres`. Consulta [Aprovisionamiento de usuarios](backend/Auth/README.md) y [aplicación directa del esquema](backend/Database/README.md).

En `Development`, la API publica Swagger UI en `http://localhost:5068/swagger` y el documento OpenAPI en `/swagger/v1/swagger.json`. Usa el flujo `GET /api/auth/csrf`, `POST /api/auth/login` y luego los endpoints protegidos. La UI conserva cookies del mismo origen y reenvía el token CSRF legible en las mutaciones `/api`; no incluye credenciales de ejemplo. Swagger responde 404 fuera de `Development`.

El historial `/historial` muestra el avance de cada incidencia y permite abrir sus fases. `/mapa` muestra las coordenadas. La propiedad histórica `altitude` contiene la latitud y se mantiene por compatibilidad.

El [panel local anterior](docs/detection-module.md) fue retirado: `/deteccion` redirige a `/incidencias`. Su archivo local anterior permanece intacto únicamente para recuperación y conciliación controladas.

## Verificar

```bash
npm run test:env
npm run build
npm run build:clean
npm test -- --watch=false
dotnet build backend
python3 backend-tests/test_note_phases.py
```

`npm run build:clean` ejecuta el Angular CLI local con `ng build --configuration production` sin shell, reemite la salida del proceso hijo y falla solo si el build devuelve un warning Angular `▲ [WARNING]` o un presupuesto excedido `exceeded maximum budget`. El wrapper preserva el exit code del hijo para cualquier error distinto de esos dos patrones.
