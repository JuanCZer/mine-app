# Configuración de PostgreSQL remoto: TLS e identidad

Esta guía prepara la conexión entre el backend que corre en la Mac y PostgreSQL en el servidor remoto. Sustituye los valores de ejemplo por los correspondientes a tu instalación. No guardes contraseñas, claves privadas ni secretos en este archivo o en Git.

En la revisión previa, el servidor aceptó conexiones PostgreSQL pero no ofreció TLS; `rate-limit-v1` era el identificador HMAC activo y no existía un usuario `LOGIN` asociado directamente al rol `gds_identity_admin`. Verifica de nuevo el estado antes de realizar cambios.

## 1. Preparar TLS en el servidor

Necesitas un certificado de servidor, su clave privada y el certificado público de la autoridad certificadora (CA). El SAN del certificado debe incluir el nombre DNS o la IP exacta que utilizará la aplicación para conectarse.

Con una cuenta administrativa de PostgreSQL, identifica las rutas de configuración:

```sql
SHOW config_file;
SHOW hba_file;
SHOW data_directory;
SHOW ssl;
```

En el archivo `postgresql.conf` indicado, configura las rutas reales:

```conf
ssl = on
ssl_cert_file = '/ruta/segura/server.crt'
ssl_key_file = '/ruta/segura/server.key'
```

La clave privada debe ser accesible para el usuario del sistema que ejecuta PostgreSQL y tener permisos restringidos. No copies esa clave a la Mac. Las rutas y permisos dependen del sistema operativo y de la instalación.

En `pg_hba.conf`, agrega reglas para la IP o red autorizada de la Mac. Por ejemplo:

```conf
hostssl   NOMBRE_BD   all   IP_DE_TU_MAC/32   scram-sha-256
hostnossl NOMBRE_BD   all   IP_DE_TU_MAC/32   reject
```

PostgreSQL usa la primera regla que coincide. Coloca estas reglas antes de reglas generales que permitan conexiones sin TLS y revisa las reglas existentes para no bloquear a otros clientes. Sustituye `NOMBRE_BD` e `IP_DE_TU_MAC` por los valores reales.

Aplica la configuración con el procedimiento correspondiente a tu instalación; algunos cambios pueden requerir un reinicio. Comprueba:

```sql
SHOW ssl;
```

Debe responder `on`. Después de conectar desde la Mac, ejecuta en esa misma sesión:

```sql
SELECT ssl, version, cipher
FROM pg_stat_ssl
WHERE pid = pg_backend_pid();
```

El campo `ssl` debe ser `true`. Además, verifica desde el cliente la identidad del servidor con `VerifyFull`, como se indica más abajo.

## 2. Crear el login de administración de identidad

Con una cuenta administrativa, en la base correspondiente, crea un login independiente. Sustituye `NOMBRE_BD` por el nombre real; si contiene mayúsculas o caracteres especiales, enciérralo entre comillas dobles:

```sql
CREATE ROLE gds_identity_login
    LOGIN
    NOINHERIT
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOREPLICATION;

GRANT CONNECT ON DATABASE NOMBRE_BD TO gds_identity_login;
GRANT gds_identity_admin TO gds_identity_login;
```

El rol de grupo `gds_identity_admin` debe existir previamente. Asigna la contraseña desde `psql`, sin escribirla directamente en un archivo SQL:

```text
\password gds_identity_login
```

Conéctate como `gds_identity_login` y comprueba que puede asumir el rol:

```sql
SET ROLE gds_identity_admin;
SELECT session_user, current_role;
RESET ROLE;
```

Durante la consulta, `session_user` debe ser `gds_identity_login` y `current_role` debe ser `gds_identity_admin`.

## 3. Configurar las conexiones en la Mac

Copia a la Mac **solo el certificado público de la CA** y guarda su ruta. En `backend/.env`, configura la conexión de ejecución y la conexión aislada de identidad con el host incluido en el certificado:

```dotenv
ConnectionStrings__Postgres=Host=HOST_DEL_CERTIFICADO;Port=5432;Database=NOMBRE_BD;Username=USUARIO_RUNTIME;Password=SU_CONTRASEÑA;Ssl Mode=VerifyFull;Root Certificate=/ruta/en/tu/mac/ca.crt

ConnectionStrings__IdentityAdmin=Host=HOST_DEL_CERTIFICADO;Port=5432;Database=NOMBRE_BD;Username=gds_identity_login;Password=SU_CONTRASEÑA;Ssl Mode=VerifyFull;Root Certificate=/ruta/en/tu/mac/ca.crt
```

Usa los valores reales y conserva las contraseñas fuera del repositorio. No uses `Trust Server Certificate=true`: el proyecto lo rechaza. Restringe los permisos del archivo:

```bash
chmod 600 backend/.env
```

Si también utilizas `POSTGRES_DEPLOY_URL`, configúrala para verificar el certificado del servidor con `sslmode=verify-full` y, cuando corresponda, `sslrootcert`. La conexión de despliegue es administrativa y no debe usarse como conexión runtime de la API.

## 4. Configurar el secreto HMAC

PostgreSQL guarda el identificador de la clave activa, pero no el secreto. En `backend/.env` configura:

```dotenv
IdentitySecurity__RateLimitHmacKeys__0__KeyId=rate-limit-v1
IdentitySecurity__RateLimitHmacKeys__0__SecretBase64=EL_SECRETO_VIGENTE_EN_BASE64
```

Si otra instancia de la aplicación ya usa `rate-limit-v1`, recupera el mismo secreto desde su configuración o gestor de secretos. Si confirmas que es una instalación nueva y ninguna otra instancia utiliza esa clave, genera un secreto nuevo de 32 bytes:

```bash
openssl rand -base64 32
```

Guárdalo en el archivo protegido y distribúyelo de forma segura a las instancias que lo necesiten. No lo compartas en mensajes ni lo guardes en Git. Una tabla de límites vacía por sí sola no demuestra que no existan otras instancias configuradas.

## 5. Comprobación posterior

1. Confirma que PostgreSQL acepta TLS y que el cliente verifica el certificado y el host con `VerifyFull`.
2. Verifica la conexión del usuario runtime y la sesión de `gds_identity_login` con `SET ROLE`.
3. Confirma que la clave HMAC configurada corresponde al identificador activo y coincide entre instancias.
4. Inicia el backend y revisa `/api/health`. El arranque puede ejecutar tareas de limpieza que modifican registros en la base; tenlo en cuenta al elegir el entorno.
5. Prueba el inicio de sesión y las operaciones de administración con cuentas y datos de prueba. Las pruebas de concurrencia y rollback deben hacerse en una base aislada.

Para el procedimiento de despliegue y sus requisitos adicionales, consulta [postgresql-cutover.md](postgresql-cutover.md).
