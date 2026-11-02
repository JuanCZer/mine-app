# Especificación técnica: administración de usuarios y cambio de contraseña

> **Estado:** diseño aprobado el 20 de octubre de 2026. Este documento fija el contrato funcional, técnico y de seguridad que debe implementar el módulo. No autoriza registro público, recuperación de contraseña por correo ni nuevos roles.

## 1. Objetivo y alcance

La aplicación incorporará una sección **Administración** para gestionar cuentas internas y una sección **Cuenta** para que cada persona cambie su propia contraseña. La administración estará disponible únicamente para usuarios con rol `administrador` y permiso calculado por servidor `users:provision`.

El alcance incluye:

- listar y buscar usuarios;
- crear usuarios con una contraseña temporal aleatoria;
- asignar exactamente uno de los roles canónicos `operador`, `validador` o `administrador`;
- activar y desactivar cuentas;
- cambiar el rol de una cuenta;
- obligar a cambiar la contraseña temporal antes de usar el resto de la aplicación;
- permitir que cualquier usuario autenticado cambie su propia contraseña mediante su contraseña actual;
- invalidar todas las sesiones de la cuenta después de cambiar la contraseña, el rol o el estado;
- exigir reautenticación reciente para mutaciones administrativas;
- registrar las operaciones en la auditoría existente;
- proteger la consistencia mediante control de concurrencia y reglas de base de datos.

La interfaz puede ocultar opciones según los permisos de la sesión, pero la API seguirá siendo la autoridad. No se aceptará un rol, permiso, actor o identificador de usuario enviado por el navegador como prueba de autorización.

## 2. Roles y permisos

Cada cuenta tendrá exactamente un rol canónico.

| Rol | Permisos efectivos |
| --- | --- |
| `operador` | `incidents:read`, `incidents:write` |
| `validador` | permisos de `operador` más `phases:validate` |
| `administrador` | permisos de `validador` más `users:provision` y `catalogs:manage` |

`SessionAuthentication` seguirá calculando los permisos en el backend. La sesión se rechazará de forma cerrada si una cuenta no tiene rol, tiene más de uno o tiene un rol fuera de la lista canónica. Los tres roles se almacenarán en `AspNetRoles`; la pertenencia se almacenará en `AspNetUserRoles`.

La API y la base de datos impedirán:

- desactivar la propia cuenta del administrador autenticado;
- cambiar el propio rol del administrador autenticado;
- desactivar o degradar al último administrador activo;
- asignar más de un rol a una cuenta;
- asignar un rol distinto de los tres roles canónicos.

Para transferir la responsabilidad cuando solo existe un administrador, primero se debe crear o promover otro administrador activo. Después se podrá modificar la cuenta anterior.

## 3. Arquitectura

La solución conservará ASP.NET Core Identity, la cookie `GDS.Session`, la protección CSRF existente y PostgreSQL como fuente de verdad. No se usarán migraciones de Entity Framework; el modelo de la aplicación se adaptará al contrato SQL explícito.

Habrá dos fronteras de conexión:

1. `ConnectionStrings__Postgres` continuará usando la cuenta asociada a `gds_app_runtime` para autenticación, sesión, incidencias, catálogos y las operaciones actuales.
2. `ConnectionStrings__IdentityAdmin` usará una cuenta `LOGIN NOINHERIT` distinta por ambiente, miembro únicamente de `gds_identity_admin`, para crear cuentas, modificar estado o rol y cambiar contraseñas. Esa cuenta recibirá `CONNECT` directamente sobre la base del ambiente y ejecutará `SET ROLE gds_identity_admin` al abrir cada conexión; el backend comprobará `current_role` antes de usarla.

No habrá fallback de la conexión administrativa a la conexión principal. Si `ConnectionStrings__IdentityAdmin` falta, es inválida o no cumple la política TLS, el backend fallará al iniciar con un mensaje genérico que no contenga la cadena de conexión. En producción ambas conexiones exigirán `sslmode=VerifyFull` y secretos inyectados por la plataforma. Los archivos `.env.example` solo mostrarán el nombre de la variable, nunca una conexión funcional.

`gds_identity_admin` será un rol PostgreSQL `NOLOGIN` y `NOINHERIT`. La operación del servidor creará la cuenta `LOGIN` del ambiente, le concederá membresía y administrará su `CONNECT`; el SQL de aplicación no creará cuentas de infraestructura, contraseñas ni grants de conexión. El delta endurecerá también un rol preexistente mediante `ALTER ROLE gds_identity_admin NOLOGIN NOINHERIT` y aplicará esta allowlist exacta:

- `USAGE` en los esquemas `public` y `app`, sin `CREATE`;
- `SELECT` sobre las columnas `Id`, `UserName`, `NormalizedUserName`, `DisplayName`, `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `IsActive`, `CreatedAt`, `UpdatedAt`, `MustChangePassword`, `PasswordChangedAt` y `ManagementVersion` de `public."AspNetUsers"`;
- `INSERT` sobre `Id`, `UserName`, `NormalizedUserName`, `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `DisplayName`, `IsActive`, `CreatedAt`, `UpdatedAt`, `MustChangePassword`, `PasswordChangedAt` y `ManagementVersion` de `public."AspNetUsers"`; el SQL explícito de creación omitirá las demás columnas para que reciban sus defaults o `NULL` y no dependerá de un `INSERT` de EF que enumere todo el modelo;
- `UPDATE` sobre `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, `IsActive`, `UpdatedAt`, `MustChangePassword`, `PasswordChangedAt` y `ManagementVersion` de `public."AspNetUsers"`, sin `DELETE` ni actualización de las demás columnas;
- `SELECT` sobre `Id`, `Name` y `NormalizedName` de `public."AspNetRoles"`, sin escritura;
- `SELECT ("UserId", "RoleId")`, `INSERT ("UserId", "RoleId")` y `DELETE` a nivel de tabla sobre `public."AspNetUserRoles"`;
- `INSERT (actor_user_id, action, outcome, entity_type, entity_id, correlation_id, source_ip_hash, changes, security_context)` sobre `app.audit_events`, sin `SELECT`, `UPDATE` ni `DELETE`;
- `SELECT (key_sha256, actor_user_id, payload_sha256, user_id, protected_response, created_at, expires_at)`, `INSERT` sobre esas mismas columnas y `UPDATE (protected_response)` sobre `app.identity_idempotency_receipts` únicamente para convertir el recibo vencido en tombstone, sin `DELETE`;
- `SELECT (key_id, state, changed_at)` sobre `app.identity_rate_limit_key_state`, sin escritura desde solicitudes HTTP;
- `SELECT (key_id, bucket_hmac, window_started_at, window_ends_at, attempt_count, expires_at)`, `INSERT` sobre esas mismas columnas, `UPDATE (attempt_count, expires_at)` y `DELETE` sobre `app.identity_rate_limits` para el consumo y la limpieza acotada;
- ningún privilegio de secuencia: las tablas nuevas no usarán columnas identity y `app.audit_events.id` conservará su UUID por defecto;
- ningún `EXECUTE` directo sobre funciones de aplicación. Las funciones nuevas `app.enforce_identity_user_role_cardinality()`, `app.enforce_identity_role_catalog()` y `app.guard_identity_admin_mutation()`, además de la existente `app.reject_immutable_change()`, se invocarán exclusivamente mediante sus triggers y tendrán `EXECUTE` revocado de `PUBLIC` y de `gds_identity_admin`;
- la llamada de bloqueo usará la firma exacta `pg_catalog.pg_advisory_xact_lock(bigint)`, aceptada como built-in de plataforma, y el único `EXECUTE` público requerido de una extensión fuera de `pg_catalog` será `public.gen_random_uuid()` para defaults UUID. No se autoriza por familia, nombre sin firma ni esquema completo.

La verificación distinguirá la superficie administrada por este módulo de los baselines normales de PostgreSQL. `CONNECT` o `TEMPORARY` efectivos por `PUBLIC`, y las funciones built-in de `pg_catalog`, se inventariarán como baseline de plataforma y no se considerarán deriva. Las funciones de extensiones fuera de `pg_catalog` no recibirán esa excepción general: el delta conservará `PUBLIC EXECUTE` solo sobre la firma exacta `public.gen_random_uuid()`, revocará `PUBLIC EXECUTE` sobre cualquier otra función de extensión no allowlisted y abortará si no puede hacerlo; la verificación final reportará y rechazará cualquier sobrante. También serán deriva los grants directos o heredados a `gds_identity_admin` fuera de la allowlist sobre los esquemas `public`/`app`, sus objetos de aplicación o cualquier membresía adicional. El delta revocará esa deriva administrada y abortará si no puede hacerlo, sin retirar permisos de otros roles salvo el `PUBLIC EXECUTE` de extensiones que este contrato endurece explícitamente.

Los grants y la membresía de `gds_app_runtime` permanecerán intactos. Las operaciones de identidad nuevas nunca usarán una conexión de despliegue ni `gds_db_deployer` durante solicitudes HTTP.

La conexión `IdentityAdmin` también será la única frontera para los recibos de idempotencia y los contadores de límites de uso de identidad. De esta forma, `gds_app_runtime` no recibe permisos nuevos y todas las réplicas del backend comparten el mismo estado de seguridad.

### 3.1 Límites de uso distribuidos

Los límites de login, reautenticación, cambio de contraseña y mutaciones administrativas se almacenarán en PostgreSQL; no se usarán contadores en memoria por réplica. `app.identity_rate_limit_key_state` tendrá `key_id varchar(64)` como clave primaria y un `CHECK` con la expresión regular `^[a-z0-9][a-z0-9._-]{0,63}$`, `state` limitado a `accepted`, `active`, `retained` o `retired`, y `changed_at timestamptz`; un índice único parcial permitirá como máximo una fila `active` y un constraint trigger diferido exigirá exactamente una al confirmar. Esta tabla será la única autoridad sobre el `active key_id`: ninguna variable o preferencia local podrá elegirlo. `app.identity_rate_limits` tendrá `key_id varchar(64)` referenciado al estado, `bucket_hmac bytea`, `window_started_at timestamptz`, `window_ends_at timestamptz`, `attempt_count integer`, `expires_at timestamptz` y clave primaria sobre `(key_id, bucket_hmac, window_started_at)`. No contendrá nombre de usuario, UUID o IP en claro; `key_id` será un identificador de versión no secreto y `window_ends_at` permitirá decidir cuándo retirar una clave anterior sin confundirlo con el TTL de limpieza.

Todas las instancias cargarán desde el gestor de secretos `IdentitySecurity:RateLimitHmacKeys` como un array de objetos `{ KeyId, SecretBase64 }`. En variables de ambiente, la primera entrada usará exactamente `IdentitySecurity__RateLimitHmacKeys__0__KeyId` y `IdentitySecurity__RateLimitHmacKeys__0__SecretBase64`, y las siguientes incrementarán el índice. El backend validará la lista completa antes de convertirla al mapa versionado usado en memoria, para que ninguna sobrescritura de diccionario pueda ocultar un duplicado. La lista vacía, una entrada nula o incompleta y cualquier `KeyId` repetido se rechazarán al arrancar.

Cada `KeyId` será lowercase y cumplirá `^[a-z0-9][a-z0-9._-]{0,63}$`, alineado con `varchar(64)` y su `CHECK` en PostgreSQL. Cada `SecretBase64` será Base64 canónico sin whitespace: deberá decodificar correctamente, contener al menos 32 bytes aleatorios y coincidir byte por byte con la recodificación Base64 estándar del secreto decodificado; valores vacíos, con espacios, con alfabeto o padding no canónico, o menores de 32 bytes se rechazarán. La configuración no tendrá campo `current`, `active` ni otro selector local; si aparece uno heredado, el backend rechazará el arranque. PostgreSQL seguirá siendo la única autoridad del `active key_id`.

Readiness leerá PostgreSQL y retirará la instancia del tráfico si el mapa validado no contiene la clave `active` y todas las `retained`, si no hay exactamente una `active` o si no puede leer el estado. La misma comprobación se repetirá dentro de cada decisión y fallará cerrada con `503`, por lo que una instancia con mapa incompleto o una noción local distinta nunca atenderá la operación protegida. La clave del bucket será HMAC-SHA-256 con separación de dominio:

El delta y `apply.sql` sembrarán el identificador no secreto `rate-limit-v1` como `active` únicamente cuando la tabla de estado esté vacía; una reejecución nunca revertirá una rotación posterior. Antes de iniciar el backend, el gestor de secretos del ambiente deberá contener una clave distinta de al menos 32 bytes para ese `key_id`.

- login: `login | usuario normalizado | IP normalizada`, 10 intentos por minuto;
- reautenticación: `reauth | userId | IP normalizada`, 5 intentos por minuto;
- cambio de contraseña: `change-password | userId`, 5 intentos cada 15 minutos;
- mutaciones administrativas: `admin-mutation | actorUserId`, 30 intentos por minuto.

Cada decisión se ejecutará en una sola transacción y seguirá siempre este orden:

1. leerá con bloqueo compartido la fila `active` y las `retained` aplicables a la ventana, ordenadas por `key_id`, y exigirá exactamente una `active` y que el mapa local contenga todas; una retained solo será aplicable si la ventana canónica comenzó antes de su `changed_at`, que es el instante del flip;
2. calculará el HMAC del mismo bucket y ventana para cada clave requerida;
3. materializará con `INSERT ... ON CONFLICT DO NOTHING`, en orden ascendente de `key_id`, una fila candidata con contador cero para cada par `(key_id, bucket_hmac, window_started_at)`;
4. bloqueará **todas** las filas candidatas mediante `SELECT ... FOR UPDATE ORDER BY key_id` antes de leer contadores;
5. sumará `attempt_count` para la ventana a través de `active` y `retained`, incrementará únicamente la fila `active` después de adquirir todos los bloqueos y decidirá usando la suma anterior más ese intento;
6. confirmará también los intentos que terminan en `429`, antes de ejecutar el hasher o la mutación protegida.

La limpieza y la rotación tomarán primero los bloqueos de estado y después los de buckets en el mismo orden ascendente de `key_id`; ningún camino invertirá ese orden. Así se evita el deadlock y una rotación no reinicia ni divide la cuota. Si PostgreSQL no puede completar el protocolo, el endpoint falla cerrado con `503`. `expires_at` será el final de la ventana más un TTL de limpieza de 24 horas; cada comprobación elimina en un lote acotado filas expiradas y un servicio alojado repite esa limpieza.

La rotación tendrá tres etapas. Primero se distribuirá el secreto nuevo en el mapa de todas las instancias como `accepted`, se insertará su fila de estado `accepted` y se comprobará que todas las réplicas reportan haberlo cargado. Después, una sola transacción de despliegue bloqueará el estado, cambiará la clave `active` anterior a `retained` con `changed_at` igual al instante del flip y la `accepted` nueva a `active`; las solicitudes que estaban en curso conservarán su conjunto bloqueado y las siguientes observarán el nuevo estado. Las ventanas iniciadas antes del flip sumarán ambas claves; las iniciadas después usarán solo la nueva `active`, de modo que no se creen ventanas nuevas que prolonguen la retención anterior. Finalmente, la clave anterior permanecerá `retained` y configurada hasta que no exista ninguna fila suya con `window_ends_at` posterior al reloj actual más el margen; entonces otra transacción la marcará `retired` y solo después se eliminará el secreto de las instancias. La fila de estado podrá borrarse cuando la limpieza haya eliminado sus buckets y ya no tenga referencias. Una réplica con configuración opuesta o incompleta quedará no ready y no podrá atender solicitudes durante el rollout.

## 4. Modelo de datos

`ApplicationUser` añadirá estas propiedades:

| Propiedad .NET | Columna PostgreSQL | Regla |
| --- | --- | --- |
| `MustChangePassword` | `"MustChangePassword" boolean` | `NOT NULL DEFAULT false`; la API la establece en `true` al crear una cuenta. |
| `PasswordChangedAt` | `"PasswordChangedAt" timestamptz` | nullable; se establece en UTC después de un cambio de contraseña satisfactorio. |
| `ManagementVersion` | `"ManagementVersion" bigint` | `NOT NULL DEFAULT 1 CHECK ("ManagementVersion" > 0)`; aumenta en cada mutación administrativa o cambio de contraseña. |

Las cuentas existentes recibirán `MustChangePassword = false`, `PasswordChangedAt = NULL` y `ManagementVersion = 1`. No se inferirá una fecha de cambio que no existe.

`ConcurrencyStamp` seguirá siendo el sello interno de Identity. `ManagementVersion` será la versión optimista pública para los comandos administrativos. La API representará `ManagementVersion` como una cadena decimal para evitar pérdida de precisión en JavaScript.

La base garantizará la cardinalidad y el catálogo de roles al final de cada transacción mediante constraint triggers `DEFERRABLE INITIALLY DEFERRED`:

- `AspNetUsers`: `AFTER INSERT OR UPDATE`, para encolar siempre la comprobación de la fila nueva y, si cambiara el identificador, de los identificadores `OLD` y `NEW`;
- `AspNetUserRoles`: `AFTER INSERT OR UPDATE OR DELETE`, verificando tanto `OLD.UserId` como `NEW.UserId` cuando correspondan;
- `AspNetRoles`: `AFTER INSERT OR UPDATE OR DELETE`, para comprobar el catálogo después de cualquier alta, cambio o borrado y volver a validar los usuarios cuyas membresías apunten al `RoleId` anterior o nuevo.

La función diferida exigirá que el conjunto final de `AspNetRoles` contenga exactamente los tres códigos canónicos, con sus nombres normalizados esperados, y que toda membresía apunte a uno de ellos. Ignorará un usuario que ya no exista por una eliminación autorizada de despliegue, pero para todo usuario existente exigirá exactamente una membresía canónica. Por ello, insertar un usuario y confirmar sin insertar su membresía en la misma transacción falla; el orden usuario/membresía dentro de la transacción sí puede variar. Cambiar o borrar un rol canónico, insertar un cuarto rol, dejar un usuario sin rol o asignarle varios roles hará fallar el `COMMIT`. La API ejecutará `SET CONSTRAINTS ALL IMMEDIATE` antes de insertar la auditoría para convertir la infracción en un error controlado dentro de la misma transacción. El despliegue de actualización se detendrá y revertirá si encuentra una cuenta existente sin rol, con varios roles, con un rol no canónico o si no existe al menos un administrador activo; el script no elegirá ni corregirá roles de personas automáticamente.

La protección del último administrador usará una función/trigger en PostgreSQL y un bloqueo transaccional común mediante la firma exacta `pg_catalog.pg_advisory_xact_lock(bigint)`. Toda inserción, eliminación o actualización que afecte una membresía `administrador`, así como la activación o desactivación de un administrador, tomará el mismo bloqueo. Antes de confirmar se verificará que quede al menos un administrador activo. Esto evita que dos solicitudes concurrentes eliminen al último administrador tras haber leído el mismo estado anterior.

Las mutaciones administrativas establecerán `SET LOCAL app.actor_user_id` con el UUID autenticado. Las guardas de base de datos rechazarán una degradación o desactivación propia y cualquier mutación administrativa sensible sin actor válido. La API repetirá estas verificaciones para devolver un error de dominio claro, pero el trigger conservará la garantía ante errores o concurrencia.

Las cuatro funciones de trigger citadas serán `SECURITY INVOKER` y declararán `SET search_path = pg_catalog`. Todas sus referencias a tablas, secuencias, funciones, tipos y operadores que no pertenezcan a `pg_catalog` estarán calificadas con `public.` o `app.`; las llamadas de plataforma también usarán nombre y firma explícitos cuando sean sensibles, como `pg_catalog.pg_advisory_xact_lock(bigint)`. El SQL dinámico, si fuera imprescindible, solo aceptará identificadores constantes incluidos en la función y producirá nombres calificados. Ninguna función buscará objetos en `pg_temp`, ni dependerá de que `public` o `app` aparezcan en `search_path`. Así, aunque el `LOGIN` conserve el baseline `TEMPORARY`, un objeto temporal con el mismo nombre no puede interceptar lecturas, escrituras o llamadas de las guardas.

### 4.1 Bootstrap del primer administrador

`provision-user` se extenderá con un modo explícito `--bootstrap`. Solo aceptará `--role administrador`, se conectará mediante la credencial de despliegue y tomará el mismo bloqueo asesor de administración. La función de guarda SQL permitirá omitir `app.actor_user_id` únicamente si la sesión pertenece a `gds_db_deployer`, la operación declara localmente el modo bootstrap y `AspNetUsers` contiene cero filas bajo el bloqueo. La contraseña definitiva se leerá por stdin, se confirmará de forma interactiva, deberá tener de 15 a 128 runas y nunca aparecerá en argumentos o logs.

La cuenta, su membresía administrativa y el evento `identity.bootstrap.admin` se confirmarán en una sola transacción. Se creará con `IsActive = true`, `MustChangePassword = false`, `PasswordChangedAt = now()` y `ManagementVersion = 1`. El evento usará `actor_user_id = NULL`, `entity_type = 'user'`, `entity_id` igual al UUID de la cuenta creada y un `correlation_id` generado por el comando. `security_context` incluirá `origin = 'deployment_bootstrap'`, el `session_user` y el `current_role` efectivos de PostgreSQL y la misma correlación; nunca atribuirá el bootstrap al administrador recién creado. El comando no emitirá cookie ni sesión: al terminar, el administrador entrará mediante el login normal con la contraseña definitiva. Un cambio posterior de esa contraseña seguirá el endpoint normal, cerrará la sesión y exigirá un nuevo login.

Si ya existe cualquier usuario, `provision-user --bootstrap` fallará sin escribir. El comando no realizará aprovisionamiento posterior: una vez creada la primera cuenta, todas las altas requieren un administrador autenticado mediante la API, con reautenticación y auditoría. Esta decisión evita atribuir desde CLI una identidad administrativa que la credencial de despliegue podría falsificar.

Una instalación limpia aplicará la versión actualizada de `backend/Database/apply.sql` con cero usuarios y después ejecutará una sola vez el bootstrap. Una actualización mediante `backend/Database/user-administration.sql` para un servidor donde el `apply.sql` anterior ya fue desplegado tiene como precondición al menos un administrador activo y falla antes de modificar el esquema si no lo encuentra.

## 5. Contratos HTTP

Todos los contratos usan JSON UTF-8, rutas relativas bajo `/api`, cookies del mismo origen y protección CSRF en operaciones con efectos. Las respuestas de identidad incluirán `Cache-Control: no-store`; nunca contendrán `PasswordHash`, `SecurityStamp`, `ConcurrencyStamp`, datos de conexión ni secretos de infraestructura.

### 5.1 DTO públicos

**`PublicUser`** amplía el contrato actual con:

| Campo | Tipo | Descripción |
| --- | --- | --- |
| `id` | UUID como string | Identidad autenticada. |
| `username` | string | Nombre de acceso. |
| `displayName` | string | Nombre visible. |
| `permissions` | string[] | Permisos calculados por el servidor. |
| `mustChangePassword` | boolean | Obliga a ir a `/cuenta/contrasena`. |

**`AdminUserSummary`** contiene:

| Campo | Tipo |
| --- | --- |
| `id` | UUID como string |
| `username` | string |
| `displayName` | string |
| `role` | `operador \| validador \| administrador` |
| `isActive` | boolean |
| `mustChangePassword` | boolean |
| `createdAt` | fecha ISO 8601 UTC |
| `updatedAt` | fecha ISO 8601 UTC |
| `managementVersion` | entero decimal positivo como string |

### 5.2 Consulta administrativa

**`GET /api/admin/users`** requiere rol `administrador`. Acepta `q`, `role`, `status`, `page` y `pageSize`.

- `q` se recorta en extremos y busca una subcadena en `UserName` o `DisplayName` mediante `ILIKE`, sin eliminar diacríticos, con máximo 100 runas. La comparación será la de PostgreSQL bajo la collation configurada para esas columnas; la aplicación no hará transliteración, eliminación de acentos ni un segundo case-fold. Barra inversa, `%` y `_` se escapan antes de construir el patrón y se tratan como caracteres literales; la consulta usa parámetros y `ESCAPE '\'`. Un valor vacío equivale a omitir el filtro.
- `role` admite únicamente un rol canónico.
- `status` admite `active`, `inactive` o se omite para ambos.
- `page` inicia en 1.
- `pageSize` usa 25 por defecto y admite de 1 a 100.
- el orden estable es nombre de usuario normalizado y después UUID.

Responde `200` con `{ items: AdminUserSummary[], page, pageSize, total }`.

**`GET /api/admin/roles`** requiere rol `administrador` y responde `200` con los tres elementos `{ code, label }` en este orden: `operador`, `validador`, `administrador`. No obtiene opciones arbitrarias de la tabla; expone únicamente el contrato canónico.

### 5.3 Creación de usuario

**`POST /api/admin/users`** requiere rol `administrador`, CSRF y reautenticación administrativa vigente. Recibe:

| Campo | Regla |
| --- | --- |
| `username` | 3 a 64 caracteres ASCII de la lista `A-Z`, `a-z`, `0-9`, punto, guion y guion bajo; se recorta en extremos y se normaliza con Identity. |
| `displayName` | 1 a 120 runas después de recortar extremos; no admite controles Unicode. |
| `role` | exactamente un rol canónico. |

El encabezado `Idempotency-Key` es obligatorio y debe ser un UUID v4 canónico en formato RFC 4122 `D`: 36 caracteres ASCII, hexadecimal en minúsculas y guiones `8-4-4-4-12`, sin llaves, prefijo URN ni espacios. La UI genera una clave nueva al abrir el intento de creación y reutiliza exactamente la misma clave, actor y payload normalizado cuando reintenta por una respuesta perdida. Una intención de creación distinta siempre recibe otra clave.

El servidor genera una contraseña temporal de 24 caracteres con `RandomNumberGenerator` y un alfabeto sin caracteres visualmente ambiguos. La contraseña satisface la política de 15 a 128 runas, se entrega directamente al hasher de Identity y nunca se persiste en texto plano.

La creación de usuario, su membresía de rol, el evento de auditoría y el recibo de idempotencia se confirman en una sola transacción. La cuenta se crea activa, con `MustChangePassword = true`, `PasswordChangedAt = NULL` y `ManagementVersion = 1`.

La tabla `app.identity_idempotency_receipts` almacenará `key_sha256`, `actor_user_id`, `payload_sha256`, `user_id`, `protected_response`, `created_at` y `expires_at`, con `key_sha256` como clave única. Para calcular `key_sha256`, el servidor serializará el UUID del actor y la `Idempotency-Key` como strings RFC 4122 `D` en minúsculas, concatenará exactamente `actorUuid + ":" + idempotencyUuid`, codificará ese ASCII como UTF-8 y aplicará SHA-256. No usará `Guid.ToByteArray()`, bytes nativos ni ninguna representación dependiente de endian. El UUID v4 aleatorio aporta la resistencia de preimagen y elimina un secreto rotatorio para este índice. `payload_sha256` será SHA-256 de una representación UTF-8 canónica, inequívoca y con longitudes de campo del usuario normalizado, nombre visible recortado y rol. La clave queda así ligada al actor y el hash del payload se comprueba por separado.

La base guardará deliberadamente `actor_user_id` y `user_id` como UUID, los dos hashes SHA-256 y `protected_response` como ciphertext, además de sus fechas. No guardará la `Idempotency-Key` original, el payload canónico ni la contraseña temporal en texto plano; tampoco aparecerán en logs o auditoría. No habrá configuración `IdentitySecurity__IdempotencyHmacKey` ni rotación asociada a idempotencia.

Antes de crear, la API toma un bloqueo asesor derivado de `key_sha256` y lee el recibo:

- si no existe, crea la cuenta y protege el cuerpo completo de éxito con ASP.NET Core Data Protection, propósito `GdsMine.IdentityCreateReceipt.v1`, usando el key ring compartido entre réplicas;
- si existe, no ha expirado y coincide `payload_sha256`, descifra y devuelve exactamente el mismo status y cuerpo, con `Idempotency-Replayed: true`;
- si el mismo actor usa la misma clave con cualquier payload canónico diferente, devuelve `409 idempotency_key_reused` sin intentar crear;
- si ya expiró, devuelve `409 idempotency_result_expired` y no crea otra cuenta.

El ciphertext vive diez minutos desde `created_at`, sin extensión por replay. Una limpieza acotada establece `protected_response = NULL` al vencer, conservando el recibo como tombstone para impedir una creación duplicada. La cuenta y su auditoría permanecen. No existe endpoint de consulta o recuperación. La disponibilidad del key ring compartido durante esos diez minutos es una precondición de despliegue; un fallo al proteger el recibo revierte toda la creación.

Responde `201` con `{ user: AdminUserSummary, temporaryPassword: string }`, sin encabezado `Location` porque no existe un endpoint individual de consulta. La contraseña temporal se devuelve solamente en la respuesta original o en su replay idempotente exacto durante diez minutos. La respuesta incluye `Cache-Control: no-store` y `Pragma: no-cache`.

### 5.4 Cambio de rol y estado

**`PUT /api/admin/users/{id}/role`** requiere rol `administrador`, CSRF y reautenticación administrativa vigente. Recibe `{ role, expectedManagementVersion }`.

**`PUT /api/admin/users/{id}/status`** requiere rol `administrador`, CSRF y reautenticación administrativa vigente. Recibe `{ isActive, expectedManagementVersion }`.

`expectedManagementVersion` debe ser un string JSON que cumpla `^[1-9][0-9]*$` y que, al convertirse con comprobación de overflow, esté entre `1` y `9223372036854775807`. El binding del DTO no permitirá coerción numérica. Un número JSON se rechaza aunque sea pequeño; valores fuera de `bigint`, cero, negativos, signos, espacios o decimales responden `400 validation_failed`. El contrato preserva exactamente valores superiores a `2^53`, por ejemplo `"9007199254740993"`, de ida y vuelta entre PostgreSQL, .NET y Angular.

Ambos endpoints:

- rechazan UUID vacío o inválido;
- comparan la versión esperada dentro de la misma sentencia o transacción que escribe;
- aplican primero autorización, reautenticación, versión, cuenta propia y último administrador, incluso si el valor solicitado coincide con el almacenado;
- incrementan `ManagementVersion`, actualizan `UpdatedAt` y reemplazan `ConcurrencyStamp` por un valor aleatorio nuevo; `ConcurrencyStamp` no es un contador y nunca se incrementa aritméticamente;
- rotan `SecurityStamp` para invalidar todas las sesiones previas de la cuenta afectada;
- registran un evento de auditoría en la misma transacción;
- responden `200 AdminUserSummary` con la nueva versión;
- no incrementan versión ni escriben un evento de cambio si, después de superar todas esas guardas, la solicitud coincide con el estado almacenado; responden el resumen actual.

### 5.5 Reautenticación administrativa

**`POST /api/auth/reauth`** requiere una sesión activa con rol `administrador` y CSRF. Recibe `{ password }`. Verifica la contraseña actual sin crear una nueva sesión ni ampliar la duración absoluta de ocho horas de `GDS.Session`.

El cuerpo tendrá un máximo de 4 KiB medido en bytes de la solicitud. El servidor impondrá el límite al flujo HTTP antes de deserializar JSON y antes de invocar el hasher. `password` admite como máximo 128 runas, sin truncamiento ni normalización; un cuerpo mayor responde `413 validation_failed` y una contraseña mayor responde `400 reauthentication_failed`, sin ejecutar el hasher.

Después de una verificación satisfactoria, emite una cookie protegida `GDS.AdminStepUp` con:

- UUID del usuario;
- `SecurityStamp` vigente;
- instante UTC de reautenticación;
- expiración absoluta de cinco minutos.

La cookie será `HttpOnly`, `SameSite=Strict`, `Path=/api/admin` y `Secure` fuera de Development/Testing. Usará ASP.NET Core Data Protection con un propósito separado de la cookie de sesión. Cada mutación administrativa comprobará que la cookie se pueda descifrar, no haya expirado, corresponda al usuario de `GDS.Session`, conserve el mismo `SecurityStamp` y que la cuenta siga activa y sea administradora.

La StepUp no se consume al mutar otra cuenta: puede autorizar varias operaciones hasta su expiración absoluta de cinco minutos. Se invalida por TTL, logout o cambio de contraseña del actor, y también cuando otro administrador cambia el rol o estado del actor y rota su `SecurityStamp`. Cambiar el rol o estado de un usuario distinto no invalida ni renueva la StepUp del actor. Cuando detecte una causa de invalidación, el backend rechazará la mutación y eliminará la cookie cuando pueda hacerlo.

La reautenticación tiene un límite de cinco intentos por minuto por combinación de usuario e IP. Responde `204` al tener éxito, `400 reauthentication_failed` con un mensaje genérico si la contraseña es incorrecta y `429` al exceder el límite. Un fallo no cierra la sesión normal.

### 5.6 Cambio de contraseña propia

**`POST /api/auth/change-password`** está disponible para cualquier usuario autenticado, incluido uno con cambio obligatorio. Requiere CSRF y recibe:

| Campo | Regla |
| --- | --- |
| `currentPassword` | contraseña actual; máximo 128 runas antes de ejecutar el hasher. |
| `newPassword` | 15 a 128 runas, sin truncamiento ni normalización silenciosa. |
| `confirmation` | debe coincidir exactamente con `newPassword`. |

La nueva contraseña se verifica contra el hash actual y se rechaza si reutiliza la contraseña vigente. No se exige una composición arbitraria de mayúsculas, números o símbolos. El límite se cuenta por runas Unicode, no por unidades UTF-16.

El cambio actualiza de forma atómica `PasswordHash`, `PasswordChangedAt`, `MustChangePassword = false`, `ManagementVersion` y `UpdatedAt`, reemplaza `ConcurrencyStamp` por un valor aleatorio nuevo, rota `SecurityStamp` e inserta el evento de auditoría. `ConcurrencyStamp` no se incrementa. Al rotar `SecurityStamp`, todas las sesiones existentes quedan inválidas. Después de confirmar la transacción, el endpoint elimina las cookies de sesión y reautenticación de la respuesta y devuelve `204`. El frontend limpia su estado y navega a `/login`; el usuario debe iniciar sesión con la contraseña nueva.

El cuerpo tendrá un máximo de 4 KiB medido en bytes de la solicitud. El servidor impondrá el límite al flujo HTTP antes de deserializar JSON y antes de invocar el hasher; un cuerpo mayor responde `413 validation_failed`. El endpoint tiene un límite de cinco intentos cada quince minutos por usuario. Una contraseña actual incorrecta produce una validación genérica y nunca registra el valor recibido.

Para impedir que dos cambios de contraseña o una desactivación concurrente se sobrescriban, el backend leerá dentro de la transacción al usuario activo y conservará los valores observados de `PasswordHash`, `ConcurrencyStamp` y `ManagementVersion`. Después de verificar `currentPassword` y que la contraseña nueva no reutiliza ese hash, ejecutará una sola actualización condicional que incluya `WHERE Id = @id AND IsActive = true AND PasswordHash IS NOT DISTINCT FROM @observedPasswordHash AND ConcurrencyStamp = @observedConcurrencyStamp AND ManagementVersion = @observedManagementVersion`. Esa sentencia escribirá el hash nuevo y todos los sellos, fechas, indicadores y versión descritos arriba; la auditoría se insertará únicamente si actualizó una fila.

Si la actualización condicional afecta cero filas, toda la operación se revierte y responde `409 credential_state_changed`, sin reintentar con el estado nuevo ni escribir auditoría de éxito. Por tanto, dos solicitudes que partan del mismo hash y sellos pueden producir como máximo un `204`; la otra obtiene `409`. Una desactivación que gane la carrera conserva `IsActive = false`, porque la sentencia de contraseña exige que siga activa y nunca escribe `IsActive`.

## 6. Cambio obligatorio de contraseña

`SessionAuthentication.PrincipalAsync` incluirá un claim interno que refleje `MustChangePassword`, y `PublicUser` expondrá el booleano. La validación de la cookie ya consulta la cuenta en cada solicitud; también refrescará este estado sin renovar la cookie.

Una política de backend restringirá a una sesión con `MustChangePassword = true` a estas rutas:

- `GET /api/auth/me`;
- `GET /api/auth/csrf`;
- `POST /api/auth/change-password`;
- `POST /api/auth/logout`.

Cualquier otra ruta de `/api` responderá `403` con código `password_change_required`. La protección se aplicará en servidor antes de ejecutar lógica de incidencias, catálogos o administración.

En Angular, un guard posterior a `ensureSession()` enviará esas sesiones a `/cuenta/contrasena`. El guard de la página de cambio impedirá salir hacia una ruta privada mientras el indicador siga activo; cerrar sesión seguirá disponible. Una URL de retorno nunca evitará esta regla. El control del frontend mejora la navegación, mientras la política del backend garantiza el bloqueo real.

## 7. Errores y concurrencia

Los errores usarán `{ error, code, fieldErrors?, traceId? }`, sin detalles de infraestructura.

| HTTP | Uso |
| --- | --- |
| `400` | cuerpo inválido, contraseña fuera de política, confirmación distinta, contraseña actual incorrecta, rol o filtro inválido; una solicitud autenticada que alcanza la validación CSRF y lo omite o lo envía incorrecto usa `csrf_invalid`. |
| `401` | sesión ausente, expirada o revocada, siempre con `authentication_required`. |
| `403` | rol o permiso insuficiente usa `permission_denied`; el cambio de contraseña obligatorio conserva `password_change_required`; la reautenticación administrativa ausente o expirada conserva `admin_reauthentication_required`. |
| `404` | usuario objetivo inexistente. |
| `409` | nombre de usuario duplicado, versión obsoleta, estado de credenciales cambiado, clave idempotente reutilizada, intento de modificar la propia cuenta administrativa o protección del último administrador. |
| `413` | cuerpo de reautenticación o cambio de contraseña mayor de 4 KiB. |
| `429` | límite de intentos excedido. |
| `503` | dependencia de identidad o auditoría no disponible; no se confirma una mutación sin su auditoría. |

Los códigos de dominio estables serán `validation_failed`, `authentication_required`, `permission_denied`, `csrf_invalid`, `current_password_invalid`, `password_reuse`, `password_change_required`, `admin_reauthentication_required`, `reauthentication_failed`, `credential_state_changed`, `username_conflict`, `stale_management_version`, `self_management_forbidden`, `last_active_administrator`, `idempotency_key_reused`, `idempotency_result_expired`, `rate_limited` y `service_unavailable`. `authentication_required`, `permission_denied` y `csrf_invalid` no sustituyen los dos `403` de estado específicos: `password_change_required` y `admin_reauthentication_required` conservan su significado y precedencia cuando aplica esa condición.

Un conflicto de versión no sobrescribirá datos. La UI recargará la fila y pedirá al administrador revisar el nuevo estado antes de intentar otra operación.

## 8. Auditoría

Se usarán eventos append-only en `app.audit_events`:

- `admin.users.list`;
- `admin.user.create`;
- `admin.user.role.change`;
- `admin.user.status.change`;
- `identity.bootstrap.admin`;
- `auth.admin.reauthenticate`;
- `auth.password.change`.

Las mutaciones HTTP exitosas y su evento se escribirán en la misma transacción de la conexión `IdentityAdmin`; `identity.bootstrap.admin` se escribirá en la transacción de bootstrap mediante la credencial de despliegue. Si falla la auditoría, la mutación correspondiente se revierte. Los rechazos de autorización, validación, concurrencia y límite se registrarán mediante el mecanismo de auditoría seguro existente cuando PostgreSQL esté disponible, sin convertir un fallo de auditoría en autorización.

Cada evento incluirá actor cuando exista, cuenta objetivo, resultado, correlation ID y cambios permitidos como rol anterior/nuevo, estado anterior/nuevo y versiones. `identity.bootstrap.admin` será la única creación sin actor humano autenticado: conservará `actor_user_id = NULL` y la procedencia de despliegue definida en la sección 4.1. El evento `admin.users.list` usará `entity_type = 'user_directory'` y el identificador estable no vacío `entity_id = 'user-directory'`, compatible con `ck_audit_entity`; sus detalles podrán incluir filtros y paginación saneados, nunca el resultado completo. Nunca se incluirá contraseña actual, contraseña temporal, contraseña nueva, hashes, sellos de seguridad, cookies, tokens CSRF o cadenas de conexión. Los logs de aplicación tampoco serializarán DTO de contraseña ni cuerpos completos de estas rutas.

## 9. Interfaz de usuario

### 9.1 Navegación

El sidebar tendrá una sección **Administración** con la opción **Usuarios**, visible solamente cuando `PublicUser.permissions` contenga `users:provision`. Al colapsar el sidebar conservará el icono, el estado activo, el nombre accesible y el tooltip, siguiendo el comportamiento de la navegación existente.

Todos los usuarios tendrán acceso a **Cuenta → Cambiar contraseña**. Durante un cambio obligatorio, esta será la única pantalla privada accesible y mostrará una explicación directa junto con la acción de cerrar sesión.

### 9.2 Página Administración → Usuarios

La página contendrá:

- encabezado y botón **Crear usuario**;
- búsqueda con envío controlado y filtros de rol y estado;
- tabla con nombre de usuario, nombre visible, rol, estado y acciones;
- paginación basada en el contrato del servidor;
- formulario de creación con nombre de usuario, nombre visible y rol;
- confirmación explícita para cambiar rol o estado;
- estados de carga, vacío, error, conflicto y reintento.

La UI conservará en memoria la misma `Idempotency-Key` mientras una intención de creación tenga resultado incierto y la reutilizará con el mismo payload en cada reintento por timeout, desconexión o respuesta perdida. Solo descartará la clave cuando reciba una respuesta definitiva o el usuario cancele la intención; cualquier cambio de campos crea una intención y una clave nuevas. La contraseña temporal, incluida la recuperada por replay exacto, se presentará en un único diálogo una sola vez por intención. Tendrá una acción explícita para copiarla y un aviso de que no se podrá consultar otra vez. Al cerrar el diálogo se eliminarán contraseña, clave y payload del estado del componente y del DOM, y una respuesta tardía de esa intención se ignorará. No se almacenarán en `localStorage`, `sessionStorage`, telemetría ni historial de navegación.

### 9.3 Página Cuenta → Cambiar contraseña

El formulario tendrá contraseña actual, contraseña nueva y confirmación. Usará `autocomplete="current-password"` y `autocomplete="new-password"`, permitirá pegar valores, mostrará la regla de 15 a 128 caracteres y no alterará silenciosamente espacios o Unicode. Después de un `204`, mostrará brevemente el resultado, limpiará los campos y navegará a login sin conservar la contraseña en memoria de estado global.

### 9.4 Accesibilidad y respuesta adaptable

- todos los controles tendrán `label` asociado y mensajes de error vinculados con `aria-describedby`;
- errores de envío usarán `role="alert"` y confirmaciones no urgentes `role="status"`;
- diálogos atraparán el foco, devolverán el foco al disparador y admitirán teclado y Escape cuando no se pierda la única contraseña temporal;
- el primer campo inválido recibirá foco después de una respuesta de validación;
- el estado no dependerá únicamente del color;
- foco, contraste y tamaños de objetivo seguirán WCAG 2.2 AA;
- tabla, formularios y confirmaciones funcionarán en móvil, tablet, sidebar expandido y rail colapsado.

## 10. Delta SQL ejecutable y fuente para instalaciones limpias

El primer entregable será un único archivo SQL ejecutable y autocontenido:

`backend/Database/user-administration.sql`

El servidor existente ya tendrá desplegada la versión anterior de `backend/Database/apply.sql`. Para actualizarlo, el operador transferirá y ejecutará únicamente `backend/Database/user-administration.sql`, directamente como archivo y en su orden declarado. Ese archivo contendrá la totalidad del delta. No se ejecutará de nuevo `apply.sql`, no habrá un contenedor Markdown del cual copiar SQL y no se transferirá otro SQL manual al servidor durante la actualización. Los mismos cambios de esquema, funciones, triggers, tablas y grants se integrarán además en `backend/Database/apply.sql`, que seguirá siendo la fuente acumulada para instalaciones limpias; el delta y la sección equivalente de `apply.sql` deberán conservar la misma semántica. No se generarán migraciones ni varios scripts parciales.

`README.md`, `docs/data/postgresql-schema.md` y las pruebas de contrato PostgreSQL documentarán y verificarán ambos caminos sin mezclarlos: instalación limpia = `apply.sql` actualizado y luego bootstrap; servidor existente = `apply.sql` anterior ya desplegado y luego únicamente `user-administration.sql`. También documentarán el orden obligatorio SQL, backend y frontend. El archivo delta:

1. encerrará todas las mutaciones entre `BEGIN` y `COMMIT`; las validaciones que pueden abortar la actualización ocurrirán antes de ese `COMMIT`;
2. antes de cualquier DDL, verificará la integridad de roles y que exista al menos un administrador activo; una precondición fallida abortará sin modificar el esquema;
3. añadirá las tres columnas de identidad de forma idempotente;
4. creará de forma idempotente `app.identity_idempotency_receipts`, `app.identity_rate_limit_key_state` y `app.identity_rate_limits` con las columnas, constraints e índices definidos en esta especificación, y sembrará `rate-limit-v1` como `active` solo si el estado está vacío;
5. sembrará únicamente los tres roles canónicos sin crear usuarios ni contraseñas;
6. añadirá la restricción diferible de un rol y las guardas de último administrador/autogestión como funciones `SECURITY INVOKER`, con `SET search_path = pg_catalog`, referencias no pertenecientes a `pg_catalog` siempre calificadas y sin resolución posible mediante `pg_temp`;
7. creará `gds_identity_admin` si no existe y ejecutará siempre `ALTER ROLE gds_identity_admin NOLOGIN NOINHERIT` sobre el rol resultante;
8. inspeccionará membresías y privilegios directos o heredados administrados del rol, revocará membresías en otros roles y grants sobre objetos de aplicación fuera de la allowlist, y abortará si no puede inspeccionarlos o revocarlos; inventariará por separado `CONNECT`, `TEMPORARY` y built-ins de `pg_catalog` como baselines de plataforma, pero conservará `PUBLIC EXECUTE` de extensiones fuera de `pg_catalog` únicamente para la firma allowlisted `public.gen_random_uuid()` y rechazará cualquier otra;
9. preservará sin cambios los grants de `gds_app_runtime`;
10. añadirá índices y comentarios necesarios;
11. será seguro al ejecutarse nuevamente mediante `IF NOT EXISTS`, bloques condicionales, `CREATE OR REPLACE`, `DROP TRIGGER IF EXISTS` y semillas `ON CONFLICT`;
12. después del `COMMIT`, terminará con consultas de verificación de solo lectura para roles, columnas, restricciones, triggers, atributos `SECURITY INVOKER`/`proconfig` de funciones, firmas y privilegios de ejecución, cuentas sin un rol canónico y cantidad de administradores activos. La verificación exigirá `rolcanlogin = false`, `rolinherit = false`, ausencia de atributos elevados o membresías no permitidas, coincidencia exacta de grants administrados con la allowlist, `pg_catalog.pg_advisory_xact_lock(bigint)` como firma de bloqueo, `public.gen_random_uuid()` como única función de extensión con `PUBLIC EXECUTE` y un reporte separado de los demás baselines `PUBLIC` aceptados.

Las validaciones estructurales críticas se ejecutarán antes de `COMMIT` y lanzarán una excepción si detectan deriva; las consultas finales de conciliación deberán devolver cero filas problemáticas y serán las últimas sentencias del archivo. `user-administration.sql` contendrá únicamente SQL y comentarios operativos: no contendrá host, IP, nombre de base, usuario LOGIN, contraseña, URL, token ni ningún secreto. Sus comentarios indicarán que se ejecuta con la cuenta de despliegue y que la cuenta LOGIN del ambiente, su `CONNECT` y su membresía en `gds_identity_admin` se configuran por el mecanismo seguro del servidor, fuera del archivo.

## 11. Secuencia de despliegue

El orden de entrega y despliegue es obligatorio: primero base de datos mediante el único archivo delta, después backend y al final frontend. Ninguna fase comienza hasta que las verificaciones de la anterior hayan terminado correctamente.

1. Confirmar que el servidor existente ya recibió la versión anterior de `backend/Database/apply.sql`, que hay al menos un administrador activo, y crear y comprobar un respaldo restaurable de PostgreSQL.
2. Detener temporalmente mutaciones de identidad.
3. Ejecutar directamente y por completo `backend/Database/user-administration.sql` con la cuenta de despliegue; no ejecutar `backend/Database/apply.sql` en este upgrade ni copiar sentencias desde documentación.
4. Revisar todas las consultas de verificación y confirmar al menos un administrador activo.
5. Crear fuera del repositorio la cuenta `LOGIN NOINHERIT` administrativa del ambiente, concederle `CONNECT` directo y membresía únicamente en `gds_identity_admin`, configurar `ConnectionStrings__IdentityAdmin` mediante el gestor de secretos y comprobar que cada conexión hace `SET ROLE gds_identity_admin`.
6. Configurar en todas las instancias la entrada `rate-limit-v1` mediante `IdentitySecurity__RateLimitHmacKeys__0__KeyId` y `IdentitySecurity__RateLimitHmacKeys__0__SecretBase64`, con un secreto canónico Base64 de al menos 32 bytes decodificados, y verificar que readiness reconoce exactamente el estado active/retained de PostgreSQL.
7. Comprobar que la cuenta administrativa puede realizar únicamente las operaciones previstas y que no puede leer o modificar incidencias, catálogos ni importaciones.
8. Desplegar backend y ejecutar health check, login, `/me`, reautenticación, creación sintética, cambio de rol/estado y cambio de contraseña.
9. Desplegar Angular y recorrer navegación, tabla, diálogo de contraseña temporal y flujo obligatorio.
10. Desactivar la cuenta sintética y conservar sus eventos de auditoría.

En una instalación limpia se ejecutará el `backend/Database/apply.sql` actualizado, se verificará que hay cero usuarios y se invocará `provision-user --bootstrap` una sola vez para crear el primer administrador. El modo bootstrap queda prohibido en un upgrade: el delta exige al menos un administrador activo antes de escribir. Después del primer usuario, el `provision-user` normal rechazará nuevas altas con una guía para usar la API administrativa reautenticada; no habrá un segundo proceso CLI de creación que eluda al actor y la auditoría de la API.

El cambio SQL es aditivo. Si se revierte la aplicación, las columnas, roles y guardas permanecerán para no perder datos; se retirará el secreto de la conexión administrativa y la membresía de su cuenta LOGIN. No se ejecutará `DROP` como rollback automático.

## 12. Estrategia de pruebas

### Backend y contratos

- sesión ausente, expirada y revocada devuelve `401 authentication_required`; un operador o validador autenticado recibe `403 permission_denied` en todas las rutas `/api/admin`.
- listado, búsqueda, filtros, orden estable y paginación limitada; `%`, `_` y barra inversa se buscan como literales, la comparación sigue la collation PostgreSQL declarada y la auditoría usa `entity_type = 'user_directory'` con `entity_id = 'user-directory'`.
- creación con cada rol canónico, nombre duplicado, ausencia o formato no UUID v4 de `Idempotency-Key` y entradas inválidas.
- replay en otra instancia con la misma clave, actor y payload que devuelve exactamente el mismo `201` y contraseña; el mismo actor y clave con payload distinto devuelve `409`; el mismo UUID bajo otro actor deriva otro `key_sha256` y no permite leer el recibo ajeno; la expiración a los diez minutos no crea otra cuenta.
- serialización canónica de `key_sha256` con vectores compartidos entre backend y Angular, incluyendo `00112233-4455-6677-8899-aabbccddeeff:123e4567-e89b-42d3-a456-426614174000` → `62a2b5e4351b21ac9b4446e43682c26070f0ae39cad27d56f960bb96f356b775`; rechazar formatos UUID no canónicos y demostrar que ningún camino usa bytes de `Guid` ni depende de endian.
- persistencia de idempotencia limitada a `actor_user_id`, `user_id`, `key_sha256`, `payload_sha256`, ciphertext y fechas; ausencia de clave original, payload canónico y contraseña en texto plano en base, logs y auditoría, y ausencia de secreto HMAC de idempotencia en configuración.
- contraseña temporal diferente entre intenciones, longitud correcta, aparición una sola vez en la UI y ausencia en logs/auditoría.
- cada mutación ejecutada con una sesión y permisos válidos pero con CSRF ausente o incorrecto devuelve `400 csrf_invalid`; cuerpos o campos inválidos no relacionados con CSRF conservan `validation_failed` o su código de dominio específico.
- una sesión con cambio obligatorio bloqueada fuera de su allowlist devuelve `403 password_change_required`, y una mutación administrativa sin StepUp vigente devuelve `403 admin_reauthentication_required`, sin degradarse a `permission_denied`.
- reautenticación correcta, incorrecta con `reauthentication_failed`, expirada a los cinco minutos, de otro usuario y con `SecurityStamp` rotado; una StepUp autoriza varias mutaciones de otros usuarios sin consumirse ni renovarse, pero falla tras logout, cambio de contraseña del actor o cambio de rol/estado del actor por otro administrador.
- rechazo de cuerpos de reautenticación y cambio de contraseña mayores de 4 KiB antes de deserializar o ejecutar el hasher, y rechazo de contraseñas de reautenticación de 129 runas sin ejecutar el hasher.
- cambio de rol y estado con versión vigente, versión obsoleta y dos solicitudes concurrentes; `expectedManagementVersion` rechaza todo JSON number y conserva exactamente `"9007199254740993"` (> `2^53`) desde el DTO hasta PostgreSQL y de regreso; cada escritura real reemplaza `ConcurrencyStamp` con un valor nuevo en vez de incrementarlo.
- rechazo de autodesactivación, autodegradación y eliminación concurrente del último administrador, incluidos requests no-op que deben ejecutar esas guardas antes de devolver el estado actual.
- cuenta desactivada rechazada en la siguiente solicitud y sesiones anteriores rechazadas después de cualquier rotación de sello.
- sesión con `MustChangePassword` limitada a `/me`, CSRF, cambio de contraseña y logout.
- contraseña actual incorrecta, 14/15/128/129 runas, confirmación distinta y reutilización de la actual.
- cambio satisfactorio que limpia `MustChangePassword`, registra `PasswordChangedAt`, reemplaza `ConcurrencyStamp`, revoca todas las cookies copiadas y exige nuevo login.
- dos cambios de contraseña concurrentes sobre los mismos valores observados producen exactamente un `204` y un `409 credential_state_changed`; una desactivación concurrente nunca vuelve a activar la cuenta ni queda sobrescrita por el cambio.
- límites compartidos entre al menos dos instancias para login, reautenticación, cambio de contraseña y mutaciones administrativas, con incrementos atómicos, `429`, fallo cerrado `503`, HMAC sin identificadores en claro y limpieza por TTL. Una prueba concurrente hará solicitudes mientras se cambia en PostgreSQL de `active=v1` a `active=v2, retained=v1`, con mapas opuestos o incompletos en réplicas: ninguna instancia usará un selector local, la que carezca de una clave requerida quedará no ready/fallará `503`, todas bloquearán candidatos en orden de `key_id`, no habrá deadlock y la suma de ambas versiones impedirá exceder la cuota.
- binding de `IdentitySecurity:RateLimitHmacKeys` desde el array JSON y desde `IdentitySecurity__RateLimitHmacKeys__0__KeyId` y `IdentitySecurity__RateLimitHmacKeys__0__SecretBase64`, seguido de validación antes de construir el mapa; startup rechazará lista vacía, entradas nulas o incompletas, `KeyId` duplicado, mayúsculo o fuera de `^[a-z0-9][a-z0-9._-]{0,63}$`, Base64 vacío, inválido, con whitespace o no canónico, secretos decodificados de 31 bytes y cualquier campo `current`/`active` local; aceptará el límite de 64 caracteres, Base64 canónico de 32 bytes y múltiples claves con IDs únicos.
- auditoría atómica: un fallo de inserción de auditoría revierte la mutación.
- respuestas y logs sin hashes, contraseñas, cookies, tokens o conexiones.

### PostgreSQL

- probar por separado el orden de instalación limpia (`apply.sql` actualizado y bootstrap) y el de actualización (`apply.sql` anterior ya desplegado y únicamente `user-administration.sql`), ejecutar el archivo delta dos veces con resultado idempotente y comprobar que todas sus mutaciones se confirman o revierten como una sola transacción;
- comprobar que `backend/Database/user-administration.sql` es el único artefacto SQL de actualización, que no depende de copiar bloques desde Markdown, que no contiene host, IP, nombre de base, usuario LOGIN, contraseña, URL, token ni secretos, y que sus últimas sentencias son las consultas de verificación posteriores al `COMMIT`;
- comprobar en la automatización de entrega que el delta SQL y sus verificaciones son un prerrequisito del despliegue backend, y que el backend saludable es un prerrequisito del despliegue frontend;
- comprobar las tres columnas, sus defaults y las tres semillas canónicas;
- comprobar que `rate-limit-v1` se siembra como única clave activa solo con estado vacío, que el constraint diferido rechaza cero o dos claves activas y que una transacción puede hacer el flip `active → retained` / `accepted → active` sin estado intermedio observable;
- comprobar que el `CHECK` de `key_id` acepta 1 y 64 caracteres lowercase válidos y rechaza vacío, 65 caracteres, mayúsculas y caracteres fuera de `[a-z0-9._-]`;
- comprobar que el constraint trigger de `AspNetUsers` cubre `INSERT OR UPDATE`; rechazar al confirmar un usuario insertado sin membresía, cero o dos membresías y un rol no canónico; permitir usuario y su única membresía insertados en cualquier orden dentro de una transacción;
- rechazar el cambio o borrado de un rol canónico y la inserción de un cuarto rol mediante los triggers diferidos de `AspNetRoles`;
- rechazar dos transacciones concurrentes que dejarían cero administradores activos;
- comprobar bootstrap transaccional y evento `identity.bootstrap.admin` solo con cero usuarios, rol `administrador` y credencial de despliegue; rechazar contraseñas definitivas de 14/129 runas, aceptar 15/128, y validar `IsActive = true`, `MustChangePassword = false`, `PasswordChangedAt` establecido y `ManagementVersion = 1`; el evento debe tener `actor_user_id = NULL`, apuntar mediante `entity_id` al administrador creado y registrar `origin = deployment_bootstrap`, `session_user`, `current_role` y correlación sin atribuírselo al usuario nuevo; rechazar bootstrap con cualquier usuario existente y exigir al menos un administrador activo en upgrades;
- comprobar que `gds_identity_admin` queda `NOLOGIN NOINHERIT`, sin atributos elevados ni membresías inesperadas, y que la cuenta `LOGIN NOINHERIT` del ambiente solo tiene `CONNECT` directo, membresía en ese rol y usa `SET ROLE`; inyectar grants administrados no autorizados y comprobar que el delta los revoca o aborta atómicamente si no puede hacerlo;
- comprobar por columna y objeto la allowlist sobre ambos esquemas, las tres tablas Identity, `app.audit_events`, `app.identity_rate_limit_key_state`, `app.identity_rate_limits`, `app.identity_idempotency_receipts` y funciones; verificar accesos denegados fuera de ella y confirmar que `CONNECT`, `TEMPORARY` y built-ins de `pg_catalog` se reportan como baseline;
- comprobar las firmas exactas `pg_catalog.pg_advisory_xact_lock(bigint)` y `public.gen_random_uuid()`; conceder `PUBLIC EXECUTE` a otra función de extensión fuera de `pg_catalog` debe ser revocado por el delta o hacer fallar la verificación, sin aceptar comodines por nombre o extensión;
- crear objetos señuelo en `pg_temp` con los mismos nombres que tablas y helpers de identidad y ejecutar cada trigger/guarda; las funciones `SECURITY INVOKER` con `search_path = pg_catalog` deben operar exclusivamente sobre objetos calificados reales y el baseline `TEMPORARY` no debe permitir shadowing;
- comprobar que `gds_app_runtime` conserva exactamente sus privilegios anteriores;
- comprobar rollback total ante un fallo inyectado antes de la auditoría;
- restaurar el respaldo en una instancia aislada y repetir las verificaciones.

### Angular y accesibilidad

- opción Administración visible solo con `users:provision` y guard de ruta para acceso directo;
- Cuenta visible para todos y redirección obligatoria cuando corresponda;
- formularios, errores, carga, vacío, conflicto y reintento;
- un timeout de creación reutiliza la misma clave y payload; editar el formulario crea otra intención; la contraseña temporal se muestra una sola vez y desaparece junto con la clave y el payload del DOM y del estado al cerrar;
- cambio de contraseña limpia sesión y navega a login;
- navegación completa mediante teclado, orden de foco, lector de pantalla, contraste y zoom de 200 %;
- revisión en móvil, tablet y escritorio con sidebar expandido y colapsado.

Las suites existentes de autenticación, autorización, incidencias, catálogos, workflow y entorno se ejecutarán completas. La entrega requiere, en este orden, `backend/Database/user-administration.sql` y sus pruebas de contrato, backend con build .NET sin advertencias y pruebas, y frontend con pruebas Angular y build de producción; además requiere `git diff --check` y revisión de seguridad independiente.

## 13. Criterios de aceptación

- Un administrador reautenticado crea una cuenta activa con exactamente un rol y obtiene una contraseña temporal una sola vez, con replay exacto y cifrado durante diez minutos ante pérdida de respuesta.
- La nueva cuenta no accede a ninguna función operativa hasta cambiar la contraseña temporal.
- El cambio de contraseña exige la contraseña actual, cumple 15 a 128 runas, rechaza reutilización, revoca todas las sesiones y devuelve al login.
- Solo un administrador puede listar, buscar, crear, activar, desactivar o cambiar el rol de usuarios.
- Una StepUp puede autorizar varias mutaciones durante cinco minutos sin renovarse y deja de ser válida cuando cambia el estado de seguridad del actor.
- Ninguna secuencia concurrente permite desactivar o degradar al último administrador activo ni modificar la propia cuenta administrativa.
- Los conflictos de versión y credenciales responden `409` y no sobrescriben el estado más reciente; dos cambios de contraseña concurrentes no pueden confirmar ambos.
- Cada mutación confirmada tiene un evento de auditoría en la misma transacción y ningún artefacto expone credenciales.
- `gds_identity_admin` usa una conexión separada, queda endurecido como `NOLOGIN NOINHERIT` con privilegios mínimos y `gds_app_runtime` conserva sus permisos actuales.
- Una instalación sin usuarios crea exactamente un primer administrador activo mediante bootstrap auditado, con contraseña definitiva y sin cambio obligatorio.
- El servidor existente se actualiza ejecutando solo `backend/Database/user-administration.sql` después del `apply.sql` ya desplegado; es un delta idempotente y transaccional, no contiene datos de conexión ni secretos, termina con verificaciones y se entrega antes del backend y el frontend. `apply.sql` incorpora los mismos cambios como fuente para instalaciones limpias y ambos caminos tienen pruebas de orden.

## 14. Fuera de alcance

- registro público o autoaprovisionamiento;
- recuperación o restablecimiento administrativo de contraseñas;
- envío de credenciales por correo, SMS o mensajería;
- MFA, SSO u OIDC;
- edición de nombre de usuario o nombre visible después de crear la cuenta;
- eliminación física de usuarios;
- roles personalizados, permisos editables o varias membresías por usuario;
- sesiones por dispositivo o cierre selectivo de una sola sesión;
- migraciones de Entity Framework.

Estas capacidades requerirán una especificación y una revisión de seguridad independientes antes de incorporarse.
