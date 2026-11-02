# Objetivo 4: plan de implementación de autenticación

> **Estado revisado el 18 de octubre de 2026:** **24 de 27 actividades completadas**. Las Tareas 1, 2 y 3 están implementadas; la última regresión backend completa aprobó 136/136 y las guardas de seguridad del runner aprobaron 19/19. Angular aprobó 52/52 y el build de producción; el contrato de entorno aprobó 32/32, el build .NET terminó limpio y `git diff --check` no encontró errores. Identity usa PostgreSQL y UUID; la API usa cookies, CSRF, autorización por política y ownership; no existen credenciales ni acceso demo. El runner automatizado para PostgreSQL aislado y el workflow real de fases ya están preparados; faltan ejecutarlos en un host con Docker y completar los escenarios visuales autenticados.

**Objetivo:** habilitar acceso real con usuarios persistidos en PostgreSQL y autorización comprobada por el backend.

**Arquitectura:** ASP.NET Core 10, Angular y los endpoints existentes. Se implementó ASP.NET Core Identity con un almacén PostgreSQL respaldado por Npgsql y por el DDL directo del [plan PostgreSQL](2026-09-27-03-postgresql.md), con cookie de autenticación y protección CSRF para la aplicación web servida bajo un mismo origen.

**Tecnología:** .NET 10, Identity, Npgsql, Angular 22, PostgreSQL.

**Especificación:** objetivo 4 de la solicitud del usuario; PostgreSQL confirmado. La matriz mínima de roles, ownership y política de sesiones quedó fijada en el ledger SDD. El dominio de producción, terminación TLS, MFA y proveedor de identidad corporativo continúan como decisiones de despliegue.

## Punto de partida histórico y decisión ejecutada

La inspección inicial del árbol local, cuya referencia era `HEAD a5de6f37f8ec1ea3fc9ad9ce83684568d1b09547`, encontró `/login` redirigido a incidencias, guards sin aplicar, sesión deducida desde `localStorage` y un interceptor que añadía bearer. Estos datos describen exclusivamente el estado previo a la implementación; ya no describen el código actual.

El backend inicial tenía un usuario demo fijo, tokens opacos en memoria y un fallback `demo-user` para solicitudes anónimas. La implementación retiró esos mecanismos: las rutas privadas exigen la identidad de la cookie validada, la sesión no reside en `localStorage` y el cliente no crea ni envía bearer.

**Decisión ejecutada:** usar Identity para usuarios, hashing, bloqueo y security stamp; conservar contratos de negocio y retirar el fallback demo. Los endpoints propios sobre `UserManager`/`SignInManager` no exponen registro público. [Guía oficial de Identity para SPA](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0).

**Alternativa condicionada:** si habrá SSO corporativo, clientes móviles o API para terceros, se deberá evaluar un proveedor OIDC/OAuth y validar emisor, audiencia, firma y expiración. Esta condición cambia el contrato de sesión; no se construyó un emisor JWT propio.

## Restricciones y decisiones pendientes

- PostgreSQL es la única base confirmada. `ConnectionStrings__Postgres` se resuelve exclusivamente en backend conforme al [plan de entorno](2026-09-27-06-environment.md).
- Propuesta de despliegue: SPA y `/api` bajo el mismo origen HTTPS, con proxy de desarrollo. Confirmar dominio, terminación TLS, número de réplicas y si existe IdP corporativo.
- Propuesta mínima: usuarios aprovisionados de forma administrativa, sin registro público; roles candidatos `operador`, `validador`, `administrador`. Confirmar quién ve cada incidencia y quién valida, elimina y gestiona catálogos. No asignar administrador por defecto ni asumir que todo usuario ve todo.
- Propuesta de sesión para estimar/pruebas: máximo absoluto 8 horas, sin persistencia entre cierres del navegador ni renovación deslizante. Revalidar usuario activo/security stamp en cada solicitud para revocación global inmediata; medir costo de consulta. Logout invalida todas las sesiones del usuario mediante security stamp: requiere confirmar este comportamiento. Si se requiere logout o revocación por dispositivo, diseñar almacenamiento de tickets/sesiones antes de implementar, sin fingir que borrar la cookie revoca una copia robada.
- Política implementada sin MFA: contraseña mínima 15 caracteres y máximo 128 valores Unicode (`Rune`), sin truncamiento, reglas arbitrarias de composición ni normalización. Login y aprovisionamiento aplican el mismo máximo antes de ejecutar el hasher. La validación local contra contraseñas comprometidas y la definición de MFA para usuarios privilegiados siguen pendientes para producción.
- Nunca volver a habilitar acceso demo como mecanismo de recuperación. Los datos demo solo se reasignan mediante la importación explícita del plan 03.

## Contratos y archivos

| Archivo previsto | Responsabilidad y propietario |
| --- | --- |
| `backend/Auth/ApplicationUser.cs` (nuevo) | Agente auth: `ApplicationUser : IdentityUser<Guid>`, con `DisplayName` e `IsActive`. |
| Sección de identidad de `backend/Database/apply.sql` | Agente datos: contrato SQL compatible con Identity; cualquier cambio se revisa y aplica directamente mediante el mismo archivo. |
| `backend/Auth/AuthContracts.cs`, `AuthEndpoints.cs`, `CurrentUser.cs` (nuevos) | Agente auth: DTO públicos, rutas, identidad extraída de claims. |
| `backend/Program.cs`, `backend/Notes.Api.csproj` | Integrador secuencial: registrar Identity y su almacén Npgsql, pipeline y endpoints; no ediciones paralelas con planes 03/05. |
| `src/app/core/services/auth.service.ts`, `core/models/auth.model.ts`, `core/guards/auth.guard.ts`, `core/interceptors/auth.interceptor.ts` | Agente frontend auth: estado de sesión validado, navegación y errores. |
| `src/app/pages/login/login.ts`, `login.html`, `src/app/app.routes.ts`, `src/app/app.config.ts` | Activar login, limpiar demo, guards y XSRF. |
| `src/app/layout/app-layout.html`, `app-layout.ts` | Integración con plan 01: identidad real y cierre de sesión, sin insignias de seguridad ficticias. |
| `backend.Tests/AuthTests.cs`, `AuthorizationTests.cs` (nuevos) | Casos HTTP sobre PostgreSQL de prueba; ninguna cuenta o conexión productiva. |
| `src/app/core/services/auth.service.spec.ts`, `core/guards/auth.guard.spec.ts`, `core/interceptors/auth.interceptor.spec.ts` (nuevos) | Estados de sesión, guards, destino del interceptor y errores. |

Contrato propuesto que deben compartir los subagentes:

- `GET /api/auth/csrf` → `204`, emite cookie de request token `XSRF-TOKEN`, legible por JavaScript; la cookie de autenticación y la cookie interna antiforgery permanecen `HttpOnly`. Renovar el request token tras login/logout. No cachear respuestas de sesión.
- `POST /api/auth/login` recibe `{ username, password }` + `X-XSRF-TOKEN` → `200 { user: PublicUser }` y cookie segura. `401` genérico para credenciales inválidas/cuenta inactiva; `429` para límite de intentos, `400` para CSRF inválido. No devolver bearer.
- `GET /api/auth/me` → `200 PublicUser` o `401`; `PublicUser = { id: string, username: string, displayName: string, permissions: string[] }`. Permisos calculados por servidor; el frontend solo controla presentación.
- `POST /api/auth/logout` + XSRF → `204`, revoca según la política acordada y elimina cookies; `401` ante sesión ya expirada permite limpiar la UI.
- `CurrentUser.GetRequiredId(ClaimsPrincipal principal): Guid` falla cerrado sin claim válido. Cada operación de datos recibe este identificador, nunca un `UserId` enviado por el navegador.
- `AuthService.ensureSession(): Promise<boolean>` comparte una petición `/me` entre guards concurrentes; estado `unknown | authenticated | anonymous | unavailable`. `login(username, password): Promise<void>` y `logout(): Promise<void>` conservan firmas; `user` refleja solo sesión comprobada. Un error de red no demuestra logout remoto.

Las cookies requieren defensa CSRF en las operaciones con efectos; configurar y comprobar explícitamente la validación antiforgery de los endpoints JSON. `SameSite` y CORS no la sustituyen. [ASP.NET Core antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0).

## Foco de revisión

- Recarga o acceso directo: no renderizar datos privados mientras `/me` sigue pendiente.
- Cookie copiada, expirada o usuario deshabilitado: el backend rechaza acceso y ninguna petición obtiene el espacio demo.
- Dos usuarios y un mismo ID de incidencia: el usuario ajeno no consulta ni modifica datos.
- CSRF ausente/incorrecto y credenciales incorrectas: respuesta coherente sin cambiar estado.
- Caída de red durante logout: no afirmar revocación remota; el usuario recibe estado verificable y posibilidad de reintentar.

## Tareas

### 1. Persistir identidad y retirar credenciales de demostración

**Consume:** tablas Identity definidas por el SQL directo del plan 03. **Produce:** `ApplicationUser`, almacén Npgsql, usuarios persistidos y aprovisionamiento fuera de endpoints públicos.

- [x] Registrar las decisiones pendientes de roles, alcance de datos, sesiones y despliegue. Se documentaron en el ledger SDD; la matriz mínima queda aislada en políticas modificables.
- [x] Definir pruebas equivalentes a `UserSurvivesRestart`, `InactiveUserCannotLogin` y `UnknownAndWrongPasswordHaveSamePublicResponse`; se comprobó RED y después GREEN. El harness también cubre bloqueo persistido, política de contraseña y límites de escritura runtime.
- [x] Crear `ApplicationUser`; integrar Identity con Npgsql sobre el DDL existente, hashing Identity V3 y actualización parcial de hash/bloqueo/stamps. Se retiraron `Hash`/`Verify`, cuenta y fallback demo.
- [x] Añadir aprovisionamiento administrativo documentado mediante `UserManager`, contraseña por stdin o variable dedicada, rol explícito y sin endpoint público, semilla o argumento de contraseña.
- [x] Preparar el runner cerrado `npm run test:postgres:objective4`: crea primero un contenedor propio y recupera su ID mediante `cidfile` antes de iniciarlo. Si `docker create` no entrega un ID, conserva el nombre aleatorio y la etiqueta como referencia pendiente; un fallo transitorio de `inspect` se reintenta desde `cleanup`/`finally` y solo entonces se resuelve a un ID de 64 hexadecimales. Una etiqueta incompatible bloquea permanentemente cualquier `rm`. Solo elimina después de validar ID y etiqueta, y conserva el estado para reintentar si `inspect` o `rm` fallan. La imagen multi arquitectura está fijada a `postgres:17.6-alpine@sha256:ef257d85…eee94`. Aplica `apply.sql` dos veces, separa login deployer/runtime, aprovisiona por stdin y cubre reinicios, respuestas genéricas, cuenta inactiva, revocación y cabeceras Testing ignoradas. La comprobación de secretos incluye cuerpo, cabeceras, logs y el hash Identity real leído de la base sintética. Sus guardas unitarias aprobaron 19/19.
- [ ] Ejecutar `npm run test:postgres:objective4` en un host con Docker; comprobar allí el ciclo completo y conservar evidencia de que ni logs ni respuestas contienen hashes o contraseñas. En este host el preflight falló cerrado antes de crear datos porque Docker no está instalado.
- [x] Ejecutar las pruebas disponibles de identidad: `python3 -m unittest backend-tests/test_identity.py -v` aprobó 15/15 y `dotnet build backend/Notes.Api.csproj --no-restore` terminó sin errores ni advertencias. El proyecto `backend.Tests` indicado originalmente no existe todavía.
- [x] Reaplicar `apply.sql` dos veces en PostgreSQL real y probar un usuario sintético: autenticación runtime, fallo y recuperación, rotación de security stamp, ausencia de UPDATE de tabla, permiso solo sobre seis columnas, y rechazo de `IsActive`, INSERT de usuarios y asignación de roles. La cuenta sintética se eliminó al terminar.

### 2. Entregar endpoints de sesión y autorización en servidor

**Consume:** Identity y `CurrentUser`. **Produce:** contratos HTTP anteriores y políticas de acceso aplicadas a todas las rutas privadas.

- [x] Añadir casos que fallen para login válido/inválido, `/me` anónimo, expiración, security stamp cambiado, cookie alterada, CSRF ausente y logout con una cookie copiada antes del cierre. El harness cubre además limitación de intentos y separación estricta del modo Testing.
- [x] Configurar cookie host-only, `HttpOnly`, `Secure` fuera de Development/Testing, `SameSite=Lax`, `Path=/`; el key ring exige ruta persistente fuera de desarrollo y puede compartirse únicamente mediante configuración del despliegue.
- [x] Implementar endpoints con `UserManager`/`SignInManager` sin registro público automático. Login, logout y mutaciones usan antiforgery; login tiene limitación de tasa y las escrituras de incidencia conservan auditoría transaccional.
- [x] Retirar diccionario/fallback demo; añadir autenticación, autorización y política cerrada por defecto. Solo salud, emisión de CSRF y login admiten anónimo; las APIs responden `401/403` sin redirección HTML.
- [x] Añadir cobertura equivalente a `AuthorizationTests.CrossUserAccessIsDenied` para lectura individual/listado, edición, borrado y fases; la identidad procede de claims verificados por el servidor y el acceso ajeno devuelve 404 opaco.
- [x] Ejecutar pruebas de auth/autorización; la regresión backend actual aprobó 136/136 pruebas y el build .NET terminó limpio. Contra PostgreSQL real se comprobaron cookie copiada después de logout, revocación por security stamp, separación de roles y ownership. Las cuentas sintéticas se desactivaron al terminar; sus eventos de auditoría se conservaron por la política append-only.

### 3. Activar login y sesión comprobada en Angular

**Consume:** contratos HTTP anteriores y API relativa `/api`. **Produce:** login accesible, guards que esperan `/me` y cierre de sesión comprobable.

- [x] Añadir pruebas que fallen para sesión pendiente, `/me` con `401`, red caída, varias rutas solicitando sesión, logout fallido y navegación de retorno no local.
- [x] Convertir `/login` en ruta de `LoginPage`; aplicar `authGuard` a rutas privadas y `guestGuard` a login. Esperar `/me` antes de resolver la sesión; aceptar destinos de retorno solo internos.
- [x] Eliminar campos precargados y texto de credenciales demo. Retirar `notes_token`/`notes_user` heredados; no persistir tokens de sesión ni confiar en usuario cacheado.
- [x] Configurar Angular XSRF para peticiones relativas bajo `/api`; sustituir URLs localhost absolutas en auth/notas. El interceptor solo maneja API propia, no agrega autenticación a terceros y diferencia `401`, `403`, `429` y error de red.
- [x] Mostrar usuario confirmado y logout en layout coordinado con plan 01. Ante fallo de logout, avisar de que no se pudo confirmar el cierre y permitir reintento, sin presentar la operación como completada.
- [x] Ejecutar `npm test -- --watch=false` y `npm run build`: la regresión actual aprobó 52/52 pruebas Angular y el build de producción. La integración por el proxy de desarrollo comprobó SPA 200, anónimo 401, CSRF 204, login 200, `/me` 200, catálogos 200, logout 204 y rechazo 401 de la cookie anterior.
- [ ] Completar la inspección visual autenticada de expiración, error de red y dos pestañas. El 17 de octubre de 2026, CUA comprobó que el enlace directo anónimo `/incidencias` redirige a `/login?returnUrl=%2Fincidencias` y que una recarga conserva ese destino sin mostrar datos privados. Los escenarios autenticados continúan pendientes porque este host no dispone de PostgreSQL aislado ni de una cuenta de prueba.

## Aceptación y entrega

- [x] Usuario real de PostgreSQL inicia sesión; credenciales incorrectas fallan; reiniciar API no borra la cuenta.
- [x] Anónimo, cookie inválida/expirada y usuario deshabilitado no acceden a incidencias; datos de otro dueño requieren permiso explícito comprobado en backend.
- [x] La sesión no reside en `localStorage`; cookie y XSRF cumplen la política; copia anterior de cookie tras revocación no permite acceso.
- [x] Preparar la adaptación del workflow de fases a usuarios reales, cookies/CSRF y proveedores PostgreSQL sin desactivar autenticación. El runner cubre dos usuarios, rol, ownership, concurrencia, las cuatro fases, reinicio real del contenedor, cierre por falso positivo y aserciones SQL de cuatro validaciones y ocho eventos de fase; la suite JSON existente se conserva.
- [ ] Ejecutar esa adaptación contra el contenedor desechable y registrar su resultado. No se considera ejecutada con PostgreSQL porque Docker no está disponible en este host.
- [x] Subagentes independientes revisaron los cambios y reprodujeron los casos de acceso/revocación. También verificaron como resueltos los tres hallazgos de seguridad descritos en el reporte SDD; el escaneo agregado no produjo reporte final porque agotó su cuota después de validarlos.

La implementación funcional del Objetivo 4 está disponible, pero el plan permanece abierto por la ejecución del runner PostgreSQL aislado y la inspección visual. El rollout todavía exige respaldo y aplicación ensayada del SQL, configuración HTTPS, MFA/política de contraseñas comprometidas y acceso administrativo recuperable sin bypass. El rollback preserva datos y no reactiva el modo anónimo.
