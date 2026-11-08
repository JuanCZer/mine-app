# Plan de auditoría y simplificación de la configuración

**Objetivo:** identificar qué configuración y herramientas deben conservarse, adaptarse o pueden retirarse, con evidencia de sus dependencias y sin afectar la integridad de la aplicación.

**Alcance:** `package.json`, configuración Angular y TypeScript, `scripts/` y sus pruebas, configuración de `server/`, variables de entorno, conexión PostgreSQL, Mapbox, importación histórica, compilación, pruebas y documentación operativa. Revisar el código que consuma estos elementos cuando sea necesario para entender sus dependencias.

**Restricciones:** no modificar ni eliminar datos, usuarios, tablas o permisos remotos durante la auditoría; no ejecutar SQL de escritura contra el servidor; no introducir migraciones; mantener el flujo de SQL manual solicitado; no mostrar valores de `.env`, contraseñas, tokens ni cadenas de conexión; conservar los cambios existentes en el espacio de trabajo. El token público de Mapbox se entrega al navegador y no debe tratarse como secreto.

**Resultado esperado:** un informe con inventario y matriz de decisiones: elemento, propósito, consumidores, evidencia, clasificación, impacto, riesgo, alternativa, verificaciones y recuperación. Clasificaciones: **conservar**, **adaptar**, **eliminar candidato** y **pendiente de comprobar**. Una dependencia no identificada no constituye evidencia de que una pieza esté sin uso.

## 1. Preparación y línea base

- [x] Registrar el estado del proyecto, herramientas disponibles y cambios existentes, sin alterar ni exponer datos sensibles.
- [x] Inventariar comandos públicos de `package.json`, hooks automáticos, archivos de configuración y pruebas; indicar sus rutas actuales.
- [x] Obtener una línea base de compilación y pruebas locales ejecutables sin Docker ni escritura en PostgreSQL. Registrar éxitos, fallos previos y bloqueos de entorno.

**Entrega:** inventario inicial y resultados reproducibles de la línea base.

## 2. Dependencias y funcionamiento real

- [x] Mapear desde cada comando sus scripts, variables, proyectos, archivos generados, conexiones y pruebas. Identificar dependencias cruzadas, incluida `spawnWithSignals`.
- [x] Seguir los flujos de arranque de Angular y API, configuración de Mapbox, compilación, ejecución de pruebas, aplicación SQL e importación histórica.
- [x] Distinguir código operativo, código exclusivo de desarrollo/pruebas, datos y herramientas heredadas. Verificar referencias dinámicas y documentación, no solo coincidencias textuales.
- [x] Comprobar qué información histórica existe o permanece pendiente de conciliación, incluido el archivo `notes.json` y los datos del antiguo panel por perfil de navegador, sin extraer datos privados automáticamente.

**Entrega:** mapa de dependencias y lista de elementos que necesitan investigación adicional.

## 3. Configuración y seguridad

- [x] Inventariar nombres de variables y dónde se usan, sin leer ni registrar valores secretos en el informe; documentar prioridad entre entorno y archivos de configuración.
- [x] Distinguir las conexiones PostgreSQL de ejecución y administración, sus consumidores y privilegios esperados. No modificarlas ni unificarlas sin una evaluación específica.
- [x] Revisar límites entre frontend y backend, `.gitignore`, archivo público generado, carga de secretos, protección de acceso, sesión, CSRF y requisitos de identidad relevantes.
- [x] Revisar la política de TLS y el contexto actual de conexión remota; documentar limitaciones y opciones sin habilitarlo ni cambiar el servidor durante esta tarea.
- [x] Comprobar que `server/tests` permanece excluido del compilado y del contenido de producción.

**Entrega:** controles que deben preservarse, hallazgos respaldados y riesgos de cualquier simplificación.

## 4. Decisiones por elemento

- [x] Evaluar individualmente `generate-public-env.mjs`, `run-backend.mjs`, `apply-database.mjs`, `import-legacy.mjs`, `build-angular-clean.mjs` y `scripts/tests/environment.test.mjs`.
- [x] Evaluar los comandos asociados y alternativas concretas; verificar especialmente si el flujo SQL manual sustituye `db:apply` y si existe necesidad real de importación heredada.
- [x] Revisar variables, archivos de configuración, proyectos auxiliares y pruebas dentro del alcance; clasificar cada uno con evidencia y señalar las incógnitas.
- [x] Para cada propuesta indicar funcionalidad afectada, compatibilidad de comandos y API, impacto en desarrollo/despliegue, seguridad, riesgos, pruebas y reversión.
- [x] Identificar pruebas que se pueden trasladar o adaptar y las que deben mantenerse para cubrir controles vigentes.

**Entrega:** matriz detallada y propuestas priorizadas. Los candidatos no se eliminan en esta fase.

## 5. Validación y revisión independiente

- [x] Contrastar las propuestas con los flujos de login, administración de usuarios, incidencias y fases, mapa e importación cuando corresponda.
- [x] Diseñar verificaciones apropiadas: compilación Angular y API, pruebas de configuración y servidor, comprobación de comandos y exclusión de secretos y pruebas dirigidas al cambio.
- [x] Diferenciar comprobaciones locales, pruebas de PostgreSQL aislado y verificaciones contra el servidor remoto. Documentar explícitamente lo que no pueda ejecutarse sin Docker, credenciales o cambios de infraestructura.
- [x] Solicitar revisión independiente de dependencias y efectos de seguridad mediante subagentes; resolver discrepancias con evidencia y registrar limitaciones.

**Entrega:** informe revisado, con pruebas realizadas y pendientes, sin afirmar como verificado lo que no se haya ejecutado.

## 6. Propuesta de ejecución posterior

- [x] Dividir los cambios recomendados en lotes pequeños, priorizados por valor y riesgo, con archivos y comandos afectados.
- [x] Para cada lote definir requisitos, pasos, pruebas, criterios de aceptación y reversión; conservar las pruebas relevantes o explicar su reemplazo.
- [x] Separar expresamente cualquier propuesta que implique datos, esquema, credenciales o configuración del servidor. Si fuera necesario un cambio de esquema, entregar SQL manual revisable; no introducir migraciones.
- [x] Presentar al usuario el informe y el orden recomendado antes de implementar las propuestas de simplificación.

**Criterio de cierre:** existe una clasificación sustentada para cada componente del alcance, con dependencias y riesgos documentados, evidencia verificable y un plan posterior. Los elementos no comprobados permanecen pendientes; la auditoría por sí sola no elimina ni modifica funcionalidad.

## Resultado de la auditoría — 8 de noviembre de 2026

Informe y matriz: [Auditoría de configuración y simplificación](../../auditorias/2026-11-08-configuracion-y-simplificacion.md). Comprobaciones locales: Angular 149/149, servidor 254/254, configuración 38/38; build Angular y compilaciones .NET correctos. Revisión independiente estática de scripts y controles realizada mediante subagente.

**Pendientes externos:** comprobar los perfiles de navegador y respaldos históricos, consumidores externos/CI, PostgreSQL aislado y configuración/conectividad real del servidor remoto. Quedan clasificados como pendientes en el informe, no como verificaciones completadas. No se realizaron cambios funcionales ni de base de datos.

## Implementación posterior — 8 de noviembre de 2026

- [x] H1: permitir `ConnectionStrings__Catalogs` y `Cors__AllowedOrigins__N` desde `server/.env` mediante lista explícita; conservar precedencia del entorno y exclusión de `POSTGRES_DEPLOY_URL`.
- [x] H2: ejecutar `generate:env` antes de `build:clean`, con prueba aislada de archivo inexistente y rechazo de token inválido.
- [x] H3: corregir README y documentar opciones de entorno en `server/.env.example`.
- [x] Validación: 41/41 pruebas de configuración, 149/149 pruebas Angular y `npm run build:clean` aprobados. No se realizaron cambios ni pruebas contra PostgreSQL remoto.
- [ ] Evaluar la retirada de `db:apply` una vez revisados consumidores externos y el procedimiento SQL manual.
- [ ] Resolver la conciliación de datos heredados y respaldos antes de decidir sobre el importador.

## Limpieza de interfaz retirada — 8 de noviembre de 2026

- [x] Retirar las dos páginas de detección que ya no estaban enlazadas, sus componentes exclusivos y el servicio/exportador de compatibilidad sin uso activo.
- [x] Conservar los componentes de evidencias y cronología que utiliza incidencias, las redirecciones históricas y las pruebas de conservación del archivo local durante el cierre de sesión y el cambio de cuenta.
- [x] Verificar compilación estricta de Angular, TypeScript de pruebas y 144/144 pruebas Angular. La reducción de cinco pruebas corresponde a las pruebas exclusivas de los componentes y servicio retirados.
- [ ] Determinar si las herramientas de importación histórica y aplicación automática de SQL pueden retirarse después de conciliar datos y verificar automatizaciones externas.

## Revisión de limpieza del servidor — 8 de noviembre de 2026

- [x] Revisar referencias de archivos del servidor, registro de dependencias y utilidades de pruebas.
- [x] Retirar 46 archivos `.pyc` regenerables de caché en `server/tests`; no se modificó código funcional.
- [x] Conservar proveedores de prueba, SQL y configuración activa: tienen consumidores identificados.
- [ ] Decidir sobre el importador cuando se hayan conciliado los datos históricos de los navegadores.

No se identificó un archivo fuente completo que pudiera eliminarse con seguridad sin cambiar contratos, recuperación o cobertura de pruebas. Los directorios `bin/` y `obj/` son generados, pero se conservaron porque pueden ser necesarios para procesos o pruebas locales actuales.
