# Integración provisional en el servidor privado

Este flujo construye y actualiza un solo contenedor que sirve Angular y la API .NET. PostgreSQL sigue en el servidor existente; no se modifica el esquema ni se ejecutan scripts SQL.

## Organización

- `.github/workflows/deploy-provisional.yml`: única pipeline reconocida por GitHub. Se ejecuta al hacer push a `main` o manualmente desde `main`.
- `deploy/provisional/compose.yaml`: servicio y volumen de claves del contenedor.
- `deploy/provisional/deploy.env.example`: nombres de opciones de Compose, sin credenciales reales.
- `Dockerfile`: construcción compartida para frontend y backend.
- `deploy/futuro/workflows/`: plantillas inactivas de CI, integración por PR y despliegue definitivo. GitHub no las ejecuta en esa ubicación. Para recuperar el despliegue definitivo hay que devolver sus workflows a `.github/workflows/`, incluido `ci.yml`, y verificar sus condiciones.

GitHub exige que los archivos de workflows se encuentren directamente en `.github/workflows/`. La plantilla de reglas de `main` sigue desactivada y no instala una regla en GitHub por sí sola.

## Antes de ejecutar

1. El destino acordado es **desarrollo separado**: Compose usa el proyecto `mineapp-dev`, una imagen `mineapp-dev-web`, el volumen `mineapp-dev_identity_keys` y el puerto local `8081` como ejemplo. Comprueba que esos nombres y puerto no estén en uso en el servidor. Configura un dominio de desarrollo y una base de datos de pruebas independientes; el aislamiento del contenedor no separa por sí solo los datos de PostgreSQL. No apuntes `runtime.env` a producción.
2. Instalar Docker Engine 28 o posterior y Docker Compose 2.30 o posterior en el servidor Linux. Registrar un runner de GitHub Actions dedicado con la etiqueta `mineapp-provisional` y acceso a Docker. El acceso a Docker otorga privilegios elevados en el servidor. Solo código confiable debe poder llegar a `main`; mientras no exista protección de rama, cualquier persona con permiso de push puede ejecutar código en el runner.
3. Fuera del checkout del runner, crear `/opt/mine-app-provisional/deploy.env` a partir de `deploy.env.example`. Comprobar el puerto local y las rutas absolutas; rellenar el token público Mapbox y la IP real desde la que Caddy llega al contenedor. El token `pk` de Mapbox se incluye en el frontend y es visible en el navegador. No guardar contraseñas en este archivo ni en Git.
4. Crear el archivo `runtime.env` en la ruta indicada por `MINE_APP_ENV_FILE` y restringir sus permisos a quienes operan el servicio. Configurar allí las cadenas `ConnectionStrings__Postgres` y `ConnectionStrings__IdentityAdmin` y las opciones de seguridad indicadas en `server/.env.example`. No incluir credenciales de aplicación de SQL ni ejecutarlo con `source`. El runner debe poder leer los archivos requeridos. Compose usa `env_file` con `format: raw`; usar `CLAVE=valor` sin comillas envolventes a menos que formen parte del valor. Las variables del contenedor son visibles a administradores de Docker.
5. **La API utiliza `Production` y exige TLS verificable para PostgreSQL (`Ssl Mode=VerifyFull`).** Si la base remota aún no tiene TLS y certificados adecuados, el contenedor no podrá iniciar correctamente. No se ha desactivado esa validación. Crear la carpeta de certificados indicada y hacer accesible al usuario del contenedor la CA necesaria en `/etc/mine-app/certs`; el nombre en la conexión debe coincidir con el certificado. Usar la IP real del servidor de PostgreSQL o su DNS; `localhost` dentro del contenedor apunta al contenedor.
6. Configurar Caddy en el host para enviar el dominio de desarrollo a `127.0.0.1:8081` (o al puerto configurado), y verificar la IP de origen vista por el contenedor. El puerto publicado solo es accesible desde el host; la confianza de proxies requiere verificar la topología y las cabeceras antes de admitir usuarios. Véase la [guía de Caddy y seguridad](../../docs/runbooks/docker-github-actions.md).
7. Incorporar estos archivos al repositorio. Tras un push a `main`, el runner obtiene el commit concreto en su **propio directorio de trabajo**, construye la imagen y ejecuta `docker compose up -d --no-build --wait`. Esto espera a que el contenedor esté en ejecución, pero no garantiza que la API o PostgreSQL estén listos. Esto actualiza el checkout administrado por el runner; no hace `git pull` en otro clon personal del servidor. El directorio del runner es administrado por Actions y no debe almacenar configuración propia.

Las ejecuciones se serializan; GitHub puede reemplazar ejecuciones pendientes por una más reciente. La pipeline construye la imagen en el servidor, sin publicar un registry. No incluye rollback automático ni comprueba todos los flujos de la aplicación. Revisar el estado, logs y conectividad de la API y PostgreSQL en el servidor después de desplegar, sin compartir credenciales. No usar `docker compose down -v`: eliminaría el volumen de claves de sesión.

Por ejemplo, desde el checkout del runner y con los mismos permisos del servicio:

```sh
docker compose --env-file /opt/mine-app-provisional/deploy.env -f deploy/provisional/compose.yaml ps
docker compose --env-file /opt/mine-app-provisional/deploy.env -f deploy/provisional/compose.yaml logs --tail=100 web
```

Esos comandos no exponen intencionalmente el archivo de entorno, pero revisar los logs antes de compartirlos. No ejecutar `docker compose config` sin `--quiet` en logs públicos: puede imprimir configuración privada.
