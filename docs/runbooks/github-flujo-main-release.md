> **Configuración futura:** los workflows de CI, integración y despliegue de producción están guardados como plantillas en `deploy/futuro/workflows/` y no se ejecutan. Para usarlos se tendrán que volver a colocar en `.github/workflows/` y revisar su configuración. `deploy.yml` necesita que `ci.yml` también esté en esa ruta. La guía siguiente describe ese flujo futuro.

# Flujo de integración en main y despliegue release

## Estado inicial

- La plantilla [main.disabled.json](../../.github/rulesets/main.disabled.json) define un ruleset para `main` con enforcement `disabled`. Es **solo un archivo local**: no crea ni cambia por sí mismo ninguna regla en GitHub. No se ha verificado el estado real del repositorio. Si no existen otras reglas activas, GitHub seguirá permitiendo pushes directos; el archivo no los bloquea.
- La integración automática requiere **ambas variables de repositorio** `ENABLE_MAIN_AUTO_MERGE=true` y `MAIN_POLICY_NO_BYPASS_CONFIRMED=true`. Como no se han creado, el job queda omitido. Los eventos del workflow pueden aparecer como omitidos en Actions.
- Producción requiere la variable de repositorio `ENABLE_PRODUCTION_DEPLOY=true`. Mientras no exista o no sea exactamente `true`, el job de validación y los trabajos dependientes se omiten, aun si alguien lanza manualmente el workflow.
- El workflow de comprobaciones [ci.yml](../../deploy/futuro/workflows/ci.yml) se ejecutaría para pull requests y push a `main` al restaurarse; ejecuta pruebas y build, no integra PR ni despliega.

## 1. Protección de main: preparada, no aplicada

Un administrador puede importar la plantilla desde **Settings → Rules → Rulesets** del repositorio y mantenerla **Disabled** mientras se prepara. Comprueba la disponibilidad de reglas para el plan y la visibilidad del repositorio. Antes de activarla, verifica especialmente el nombre y origen del check de GitHub Actions (`verify`), y que los ajustes permitan la estrategia de merge. Al activarla exige PR, una aprobación, revisión nueva tras cambios, resolución de conversaciones y check; también bloquea force push y eliminación de `main`. La aprobación de la última actualización debe provenir de alguien distinto de quien la subió; si trabajas solo necesitarás otra persona habilitada.

Una variable de Actions **no activa ni sustituye** las reglas de GitHub. Tampoco anula reglas existentes de la organización. La protección efectiva y los permisos de bypass deben comprobarse en GitHub. [Estados de ruleset](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets); [reglas disponibles](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets).

## 2. Integración automática de PR a main: preparada, apagada

[merge-main.yml](../../deploy/futuro/workflows/merge-main.yml) escucha eventos de PR hacia `main`, acepta solo ramas del mismo repositorio y nunca ejecuta el código del PR en su job con permisos de escritura. Al habilitarse, consulta las reglas activas y exige al menos una revisión, que se descarten aprobaciones obsoletas, método `merge` y un check obligatorio llamado `verify`. Después solicita **auto-merge nativo de GitHub** con el SHA del head. GitHub debe esperar las revisiones y checks exigidos antes de integrar; la solicitud de auto-merge se hace al abrir/actualizar el PR, no cuando llega la aprobación. [GitHub: auto-merge](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/automatically-merging-a-pull-request).

Antes de habilitarlo, un administrador debe:

1. Activar el ruleset, validar que `verify` sea el check correcto y, cuando GitHub lo permita, restringir su emisor a la app GitHub Actions. Un check homónimo de otra integración no debe contar como CI.
2. Confirmar que GitHub Actions y otros actores no tengan permisos de bypass sobre esas reglas, habilitar auto-merge en el repositorio y verificar permisos/licencia de GitHub. `MAIN_POLICY_NO_BYPASS_CONFIRMED=true` es una declaración administrativa; el workflow **no puede comprobar por sí mismo la ausencia de bypass** con permisos mínimos.
3. Configurar en variables del repositorio `MAIN_POLICY_NO_BYPASS_CONFIRMED=true` y `ENABLE_MAIN_AUTO_MERGE=true`, y probar con un PR inocuo sin aprobación, con aprobación, con un nuevo commit y con CI fallido. No activar con la regla deshabilitada.

La consulta del workflow ocurre al solicitar auto-merge; no puede impedir que un administrador modifique las reglas después. Mantener activas las protecciones y controlar la administración es imprescindible. Cambiar la variable a `false` evita nuevas solicitudes, pero no necesariamente cancela auto-merges ya habilitados: revísalos y deshabilítalos en GitHub.

## 3. Producción desde release/*: preparada, apagada

[deploy.yml](../../deploy/futuro/workflows/deploy.yml) se inicia manualmente **usando el workflow de `main`** y recibe `release_branch`, por ejemplo `release/v1.0`. El trabajo de validación se ejecuta solo si `ENABLE_PRODUCTION_DEPLOY=true` es una variable de repositorio y el workflow se seleccionó desde `main`.

El workflow exige que la rama pertenezca al mismo repositorio, tenga prefijo `release/` y apunte exactamente al mismo commit que `main` en el inicio. Ejecuta las comprobaciones contra ese SHA y usa ese mismo SHA para construir y desplegar. Justo antes de actualizar el contenedor vuelve a consultar GitHub para verificar que `main` y `release/*` siguen apuntando al SHA validado. Si no puede verificarlos, falla sin actualizar el contenedor. Como la consulta y el despliegue no son una operación atómica, evita merges o actualizaciones simultáneas durante la ventana de despliegue.

Después de integrar un PR con merge commit, la rama fuente generalmente **no** apunta al nuevo commit de `main`. Para cumplir esta condición, crea o actualiza una rama `release/*` desde el `main` ya integrado antes de solicitar el despliegue. Si `main` avanza antes de iniciar el flujo, actualiza la rama y vuelve a empezar. La igualdad de SHA demuestra que la versión coincide con `main`; no demuestra que esa rama haya originado un PR aprobado. Las aprobaciones se garantizan mediante la protección real de `main` cuando esté activa.

Antes de habilitar producción: configurar el entorno `production` para aceptar ejecuciones desde `main`, proteger el runner privado, revisar sus permisos y completar los requisitos de [Docker y Caddy](docker-github-actions.md), incluida la verificación de TLS PostgreSQL. Después, establecer la variable de repositorio `ENABLE_PRODUCTION_DEPLOY=true` y ensayar primero en un entorno aislado. El workflow no ejecuta SQL ni hace rollback automático.

## Límites de la validación

Las reglas, variables, permisos, CI y runner reales de GitHub no se han configurado ni probado desde este workspace. El archivo JSON y los workflows deben subirse al repositorio para que GitHub los reconozca. La disponibilidad de rulesets y auto-merge depende del plan y configuración del repositorio. No hay pruebas de Docker o despliegue real en esta máquina.
