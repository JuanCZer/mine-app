> **Configuración futura:** los workflows de CI, integración y despliegue de producción están guardados como plantillas en `deploy/futuro/workflows/` y no se ejecutan. Para usarlos se tendrán que volver a colocar en `.github/workflows/` y revisar su configuración. `deploy.yml` necesita que `ci.yml` también esté en esa ruta. La guía siguiente describe ese flujo futuro.

# Despliegue privado: Caddy, contenedor web y PostgreSQL existente

La arquitectura acordada ejecuta Angular y ASP.NET Core en **un solo contenedor web**. Caddy está instalado en el host y recibe HTTPS; el contenedor escucha únicamente en `127.0.0.1:8080` del servidor. PostgreSQL ya existe fuera de Docker y se conserva sin cambios. No se crean tablas ni se ejecutan migraciones.

```text
Navegador → HTTPS/Caddy en el host → 127.0.0.1:8080
          → contenedor web: Angular + API → PostgreSQL existente
```

## Requisitos

- Linux con Docker Engine **28.0.0 o posterior**, Docker Compose **2.30.0 o posterior**, `curl` y un runner de GitHub Actions registrado con la etiqueta `mineapp-deploy`. El runner necesita acceso a Docker, lo que concede privilegios elevados sobre el host; úsalo solo con código confiable y restringe el repositorio, la rama y quién puede iniciar workflows. [Docker: formato raw de env_file](https://docs.docker.com/compose/how-tos/environment-variables/set-environment-variables/); [GitHub: seguridad de runners](https://docs.github.com/en/actions/reference/security/secure-use).
- Caddy instalado directamente en el host, configurado para el dominio y certificado HTTPS. El ejemplo [Caddyfile.example](../../deploy/Caddyfile.example) supone que Caddy recibe directamente al cliente y sustituye las cabeceras reenviadas; si hay otro proxy o CDN delante, la configuración de confianza debe revisarse.
- Conectividad desde Docker a la dirección real de PostgreSQL. `localhost` dentro del contenedor significa el contenedor, no el host PostgreSQL. La API en `Production` exige `Ssl Mode=VerifyFull`; si el PostgreSQL actual aún no tiene TLS verificable, **el despliegue de producción no funcionará** hasta configurarlo. El certificado debe corresponder al nombre empleado en la conexión.
- Un token **público** Mapbox `pk...`. Se incorpora al frontend durante el build y queda visible en el navegador; no usar un token secreto.

## Red y proxy confiable

1. Crear una red Docker independiente para el servicio web, o reutilizar una existente de confianza. Por ejemplo, después de comprobar que la subred no entra en conflicto:

   ```sh
   docker network create mineapp_proxy
   docker network inspect mineapp_proxy
   ```

   El servicio usa una red externa llamada `mineapp_proxy` por defecto en GitHub Actions. Si usas otra, configura la variable del entorno GitHub `production` llamada `MINE_APP_PROXY_NETWORK`. El contenedor necesita salida hacia PostgreSQL remoto; no marques la red como `internal` si eso impide la conexión.
2. Determinar y verificar la IP de origen con la que la conexión de Caddy llega al contenedor. Docker puede presentar la puerta de enlace de la red u otra dirección, según su configuración. La puerta de enlace mostrada por `docker network inspect` es un dato para investigar, **no una garantía**. Configura la IP individual verificada en la variable GitHub `MINE_APP_CADDY_PROXY_IP`. ASP.NET solo confiará en esa IP para interpretar las cabeceras reenviadas.
3. Añadir el bloque de [Caddyfile.example](../../deploy/Caddyfile.example) a la configuración de Caddy y verificarla con las herramientas de Caddy antes de aplicarla. Sustituye el dominio de ejemplo. Caddy debe proporcionar exactamente la IP real y `https`, descartando los valores que envíe el cliente.
4. Mantener el puerto web publicado solo en `127.0.0.1` y verificar un firewall que impida acceso externo directo a la red de contenedores. Docker advierte que versiones anteriores a Engine 28 pueden exponer puertos publicados en loopback a equipos de la misma red; incluso con versiones recientes, revisar que no haya rutas directas u otras reglas que permitan llegar al contenedor. Ver [Docker: publicación de puertos](https://docs.docker.com/engine/network/port-publishing/). **Los procesos locales con acceso a ese puerto pueden eludir Caddy y falsificar cabeceras**, y quienes controlan Docker también controlan la red. Restringir el acceso al host y Docker. Antes de servir usuarios, comprobar desde clientes distintos que llegan IP distintas, que se detecta HTTPS y que las cabeceras externas falsificadas no alteran la IP. Si la topología o el proxy anterior cambia, repetir la validación.

Si PostgreSQL se convierte después en un contenedor en una red existente, puede conectarse el servicio usando `compose.database-network.example.yaml` junto con `compose.yaml` y `MINE_APP_DATABASE_NETWORK`. El archivo requiere una red **ya creada**; no crea PostgreSQL. El workflow de despliegue actual usa solo `compose.yaml`, por lo que habrá que adaptarlo y probarlo antes de usar esa opción en Actions. No muevas PostgreSQL ni borres su almacenamiento al cambiar de red.

## Configuración privada

1. Crear `/opt/mine-app/runtime.env` fuera del checkout del runner, restringido a la cuenta de servicio autorizada. Debe contener las opciones reales de `ConnectionStrings__Postgres`, `ConnectionStrings__IdentityAdmin`, `IdentitySecurity__RateLimitHmacKeys__0__KeyId` e `IdentitySecurity__RateLimitHmacKeys__0__SecretBase64`, más las opciones necesarias de tu entorno. Usa `server/.env.example` como referencia de **nombres**, sin publicar valores. La identidad y clave activa deben corresponder con la base actual.
2. No incluir `POSTGRES_DEPLOY_URL`: la API no debe recibir credenciales para aplicar SQL. El workflow rechaza esa variable; la ejecución manual directa de Compose requiere verificarlo también.
3. Compose usa `env_file` con `format: raw`: escribe `CLAVE=valor` sin comillas envolventes, salvo que formen parte del valor. No copies a ciegas `.env` con otra sintaxis ni ejecutes el archivo con `source`. Caracteres como `$` se conservan literalmente. Las variables de contenedor son visibles para administradores del host o Docker; usa un gestor de secretos cuando esté disponible.
4. Crear `/opt/mine-app/certs`; se monta como lectura en `/etc/mine-app/certs`. Si la CA de PostgreSQL es privada, copiar allí el certificado público y configurar su ruta interna en la conexión. Verificar permisos para el usuario no privilegiado del contenedor. Nunca incorporar claves privadas ni contraseñas a la imagen.
5. El volumen `identity_keys` conserva las claves de sesión ASP.NET bajo `/var/lib/mine-app/keys`. Verificar que el usuario no privilegiado puede escribir y proteger respaldos del volumen. No ejecutar `docker compose down -v`: perder las claves puede invalidar sesiones.

## GitHub Actions

En el repositorio privado, registrar el runner para el servidor con etiqueta `mineapp-deploy` y configurar el entorno `production` con:

- `MAPBOX_PUBLIC_TOKEN`: token público Mapbox.
- `MINE_APP_CADDY_PROXY_IP`: IP individual comprobada de Caddy tal como la ve el contenedor.
- `MINE_APP_PROXY_NETWORK`: nombre de la red Docker externa; si no se indica se usa `mineapp_proxy`.

El workflow [deploy.yml](../../deploy/futuro/workflows/deploy.yml) permanece desactivado hasta configurar la variable de repositorio `ENABLE_PRODUCTION_DEPLOY=true`; después se inicia manualmente desde `main` con una rama `release/*` que apunte al commit actual de `main`, según la [guía de integración y release](github-flujo-main-release.md). Primero valida la rama release y usa máquinas de GitHub para comprobaciones e imagen de prueba; después construye la imagen en el runner privado y actualiza el contenedor. La ruta del archivo de runtime es `/opt/mine-app/runtime.env`, la de certificados `/opt/mine-app/certs` y el puerto es `8080`. No ejecutar pull requests no confiables en el runner privado.

La comprobación de GitHub construye una imagen de prueba con un token sintético. El runner privado reconstruye la imagen de producción con el token público real y `--pull`, de modo que no es el mismo artefacto y sus imágenes base pueden variar. Hace falta comprobar la imagen final en el servidor. La pipeline no ejecuta SQL ni publica claves privadas en la imagen. `docker compose config` sin `--quiet` puede revelar valores privados: no guardes su salida en logs públicos. El workflow espera el estado de los contenedores y consulta `/api/health`; esta comprobación no demuestra por sí sola que los demás flujos o PostgreSQL completo funcionen.

## Operación manual

Desde un checkout confiable, preparar las variables sin pegar contraseñas en la terminal:

```sh
export MINE_APP_ENV_FILE=/opt/mine-app/runtime.env
export MINE_APP_CERT_DIR=/opt/mine-app/certs
export MINE_APP_PROXY_NETWORK=mineapp_proxy
export MINE_APP_CADDY_PROXY_IP=IP_VERIFICADA_DEL_PROXY
export MINE_APP_IMAGE_TAG=version-identificable
export MAPBOX_PUBLIC_TOKEN='pk.TOKEN_PUBLICO_DE_MAPBOX'
docker compose build
docker compose up -d --no-build
```

Mantén las variables disponibles para comandos Compose posteriores. No registres el contenido de `runtime.env` en pantalla, historial de comandos o logs. Para acceder desde otros dispositivos configura el DNS y la publicación HTTPS de Caddy según tu red; el puerto de Docker sigue siendo local al servidor.

## Verificación y recuperación

1. Revisar el estado con `docker compose ps` y acceso HTTPS al dominio. Comprobar carga de Angular, navegación directa a `/incidencias` y recursos estáticos.
2. Verificar `/api/health`, login, catálogos, incidencias y mapa, además de la IP y protocolo del cliente a través de Caddy. Una respuesta saludable no demuestra todos los flujos.
3. Conservar la imagen previa y el Compose compatible. El workflow etiqueta la imagen web con SHA, ID de ejecución e intento, pero **no realiza rollback automático**; al recuperar, utilizar la etiqueta completa anterior sin reconstruir. Los tags de imágenes base pueden cambiar, de modo que reconstruir el mismo commit no garantiza los mismos binarios.
4. Un rollback del contenedor no revierte datos ni esquema de PostgreSQL; gestionar copias y recuperación de la base por separado.

**Pendiente:** no se han probado aquí Docker, Caddy, la red del host, los certificados, el runner ni la conexión remota. La configuración solo debe considerarse lista después de ensayarlos en el servidor, idealmente en un entorno aislado.
