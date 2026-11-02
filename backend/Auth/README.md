# Identidad y aprovisionamiento administrativo

La identidad vive únicamente en PostgreSQL mediante ASP.NET Core Identity y
`ApplicationIdentityDbContext`. Aplique `backend/Database/apply.sql` con la cuenta
de despliegue antes de usarla. No use migraciones EF ni `EnsureCreated` en runtime.
La conexión se recibe en `ConnectionStrings__Postgres`. Desde la raíz,
`npm run backend` carga únicamente esa clave desde `backend/.env`; una ejecución
directa de `dotnet` requiere que la variable ya esté inyectada en el proceso.
Antes de atender HTTP o ejecutar `provision-user`, la aplicación valida esa cadena
mediante la misma política usada por incidencias, catálogos y auditoría. Production,
Staging y cualquier entorno distinto de Development/Testing exigen
`Ssl Mode=VerifyFull` y rechazan `Trust Server Certificate=true`; un fallo termina
el arranque o el comando con un mensaje sanitizado.

El comando local `provision-user` usa `UserManager` y `RoleManager`, crea los roles
mínimos `operador`, `validador`, `administrador` si faltan y asigna únicamente el
rol solicitado. Rol, usuario y asignación se guardan en una transacción. No hay
cuentas, contraseñas ni asignaciones predeterminadas, ni endpoint de registro.
Ejecute con una conexión administrativa autorizada que tenga los privilegios de
`gds_db_deployer`. El login de la API usa `gds_app_runtime`: puede leer identidad y
actualizar únicamente `PasswordHash`, `AccessFailedCount`, `LockoutEnd`,
`SecurityStamp`, `ConcurrencyStamp` y `UpdatedAt`. No puede cambiar `IsActive`,
nombre, email u otros atributos administrativos, ni crear usuarios o asignar roles.
`apply.sql` revoca el permiso UPDATE de tabla de versiones anteriores antes de
conceder esos permisos por columna. Vuelva a aplicarlo para actualizar instalaciones.
`RuntimeIdentityStore` emite escrituras parciales con control de concurrencia;
`AuthenticationService` usa el hasher versionado directamente para evitar que
`UserManager.CheckPasswordAsync` o los métodos de bloqueo actualicen toda la entidad.

Después de compilar, desde la raíz del repositorio, con la conexión administrativa
inyectada por su gestor de secretos en el entorno del proceso:

```sh
python3 -c 'import getpass,sys; sys.stdout.write(getpass.getpass("Contraseña nueva: ") + "\n")' |
  dotnet backend/bin/Debug/net10.0/Notes.Api.dll provision-user \
    --username nombre.apellido --display-name 'Nombre Apellido' --role administrador
```

`getpass` oculta la entrada y entrega la contraseña al proceso por stdin. También
se acepta `GDS_PROVISION_PASSWORD`, inyectada por un gestor de secretos para ese
proceso; el comando elimina esa variable de su propio entorno al leerla. No escriba
su valor literal en el shell, un script, el repositorio o argumentos. Elimine la
variable del proceso padre cuando termine y evite trazas de shell (`set -x`).
No canalice contraseñas usando `echo` con valores literales. No ejecute el comando
interactivamente con entrada visible: use la tubería anterior o el gestor de secretos.

La contraseña debe tener al menos 15 caracteres, sin reglas de composición, y
como máximo 128 valores Unicode (`Rune`). Login y aprovisionamiento aplican el
mismo límite antes de invocar el hasher; una contraseña de 129 runas se rechaza
por exceder el máximo lógico. Las pruebas del servicio también comprueban que una
cadena sobredimensionada de 8 MiB se rechaza antes de invocar el hasher. Se permiten
espacios y Unicode; la contraseña se conserva sin recortarla ni normalizarla. Identity
guarda hashes V3 versionados y los actualiza al verificar si hace falta. Tras 5
fallos se bloquea la cuenta por 15 minutos. El incremento, la decisión de bloqueo,
el reinicio de `AccessFailedCount` a 0 al bloquear y la escritura de `LockoutEnd`
se ejecutan atómicamente en PostgreSQL, por lo que intentos concurrentes no pierden
actualizaciones. Una cuenta inactiva siempre recibe el mismo resultado público de
credenciales inválidas.
Un nombre ya existente devuelve error sin modificar la cuenta ni sus roles.
El comando devuelve código 0 al completar y 1 al fallar, sin registrar contraseñas.
La conexión administrativa debe limitarse a este proceso, sin sustituir la del
servicio web.

## Sesiones HTTP y CSRF

La API usa exclusivamente la cookie `GDS.Session` protegida por Identity/Data
Protection. No entrega ni acepta bearer. La cookie es host-only, HttpOnly,
`Path=/`, `SameSite=Lax`, no persistente y tiene un ticket con 8 horas absolutas;
no hay renovación deslizante. En cada solicitud se consultan usuario activo,
security stamp y roles vigentes. Logout rota el stamp mediante
`RuntimeIdentityStore.RotateSecurityStampAsync`, comprueba el resultado y revoca
todas las copias de sesiones anteriores del usuario.

Configure `DataProtection__KeyRingPath` con un directorio persistente fuera del
repositorio, accesible únicamente a la cuenta del servicio. Production y otros
ambientes no locales se niegan a arrancar sin él. `ApplicationName` es estable:
`GdsMine.Authentication.v1`. Las instancias del mismo despliegue deben compartir
el key ring. Development/Testing usan por defecto un directorio externo dedicado
bajo `LocalApplicationData/GdsMine/data-protection/<ambiente>`; nunca guardan
claves en el árbol del repositorio. En despliegue proteja el volumen y sus copias
con los controles de acceso/cifrado de la plataforma.

1. `GET /api/auth/csrf`: 204; emite la cookie interna HttpOnly
   `GDS.Antiforgery` y `XSRF-TOKEN` legible por JavaScript.
2. `POST /api/auth/login`: JSON `{ username, password }` y header
   `X-XSRF-TOKEN` con el valor de `XSRF-TOKEN`; devuelve 200 `{ user }` o
   401 genérico. El cuerpo completo de login está limitado a 4 KiB y un exceso
   devuelve 413 antes de deserializar o hashear. Renueva el token CSRF asociado
   a la identidad autenticada.
3. `GET /api/auth/me`: 200 `{ id, username, displayName, permissions }` o 401.
4. Las mutaciones de incidencias/fases y `POST /api/auth/logout` requieren
   cookie y `X-XSRF-TOKEN`. CSRF ausente/incorrecto devuelve 400. Logout devuelve
   204, borra la sesión y entrega un token CSRF anónimo nuevo. Sesión expirada: 401.

Todas las cookies tienen `Path=/`, `SameSite=Lax` y carecen de Domain; Secure está
activo fuera de Development/Testing. Production debe recibir HTTPS. No configure
cabeceras de proxy de fuentes no confiables; la terminación TLS debe preservar
el esquema HTTPS mediante una configuración de proxy de confianza del despliegue.
El cliente debe enviar credenciales y leer el token renovado después de login/logout.
El login admite 10 solicitudes por minuto por IP remota, sin cola; después devuelve 429. El contador reside en cada instancia. Tras un proxy la IP usada debe ser la
IP remota confiable; coordine límites en el ingress si hay múltiples instancias.
La respuesta pública de credenciales inválidas conserva el mismo trabajo de hash
para usuario desconocido y contraseña incorrecta, sin revelar cuál condición falló.

## Borde HTTP y proxy

Todas las respuestas incluyen `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Content-Security-Policy: frame-ancestors 'none'`,
`Referrer-Policy: strict-origin-when-cross-origin` y una `Permissions-Policy`
restrictiva. La política CSP completa permanece en
`Content-Security-Policy-Report-Only` mientras se ensayan en navegador los workers,
tiles y telemetría de Mapbox. Se puede ajustar con
`HttpSecurity__ContentSecurityPolicyReportOnly` o desactivar temporalmente solo ese
reporte con `HttpSecurity__EnableContentSecurityPolicyReportOnly=false`; la
protección aplicada `frame-ancestors 'none'` permanece activa.

HSTS se emite durante 180 días solo fuera de Development/Testing y solo cuando la
solicitud observada es HTTPS. No habilita `includeSubDomains` ni `preload` sin una
decisión de dominio. Si un proxy termina TLS, registre cada IP con
`HttpSecurity__KnownProxies__0`, `__1`, etc., o cada CIDR con
`HttpSecurity__KnownNetworks__0`. Se procesa un solo salto simétrico de
`X-Forwarded-For`/`X-Forwarded-Proto`; una lista ausente o un emisor distinto se
rechazan incluso si el entorno intenta activar forwarded headers automáticamente.

Kestrel limita por defecto cada cuerpo a 1 MiB. El rango configurable de
`HttpSecurity__MaxRequestBodyBytes` es 64 KiB–16 MiB; el tamaño debe coordinarse con
el ingress sin ampliar campos de negocio. El endpoint de login conserva su límite
más estricto de 4 KiB.

Solo salud, CSRF y login permiten anónimos. Cualquier usuario autenticado activo
recibe `incidents:read` y `incidents:write`; `validador`/`administrador` agregan
`phases:validate`; `administrador` agrega `users:provision` y `catalogs:manage`.
Estos permisos describen capacidades; no crean endpoints administrativos nuevos.
La política `CanValidatePhase` exige esos roles en servidor y conserva ownership.
Propietario, actor y validador se obtienen del UUID autenticado; los nombres de
revisión enviados como contenido de formulario nunca son autoridad de identidad.
La lectura individual consulta el identificador y propietario sin depender del
límite del listado; una incidencia ajena devuelve 404. Autenticación/autorización devuelven 401/403
sin redirecciones HTML. Los errores de infraestructura se redactan antes de responder.

## Pruebas

`python3 -m unittest discover -s backend-tests -v` ejecuta Identity, sesiones,
catálogos, flujo, importación y contratos SQL. Compile antes con `dotnet build backend`.
`SessionHarness` usa Kestrel HTTP/HTTPS real, certificados efímeros y SQLite en
archivo temporal únicamente para Identity; no certifica el DDL ni los permisos
PostgreSQL. `IdentityHarness` también usa SQLite exclusivamente dentro del harness.
Ambos reutilizan las clases de producción; el runtime continúa registrado con Npgsql.

Las suites históricas de catálogos/fases usan UUID/roles sintéticos únicamente con
`ASPNETCORE_ENVIRONMENT=Testing` más `Authentication__EnableTestHeaders=true`.
`X-Test-User` debe ser un UUID no vacío y `X-Test-Roles` contiene roles permitidos
separados por comas. Sin header no existe fallback anónimo. Ese esquema no se
registra en Development/Production aunque la opción esté activada. CSRF continúa
siendo obligatorio también para las pruebas con header.
