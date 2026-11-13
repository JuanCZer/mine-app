# Docker y GitHub Actions para servidor privado

**Objetivo:** preparar la aplicación para ejecutarse en contenedores y automatizar compilación, comprobaciones y despliegue controlado sin publicar credenciales.

**Restricciones:** conservar la base PostgreSQL remota; no alterar tablas ni ejecutar migraciones; usar configuración sensible en tiempo de ejecución; no incluir `.env`, claves ni datos heredados en el contexto de build; evitar ejecutar código no confiable en infraestructura privada; no desplegar automáticamente hasta contar con el mecanismo y configuración del servidor.

## Actividades

- [x] Examinar la aplicación, sus versiones y requisitos de red, identidad y PostgreSQL.
- [x] Excluir credenciales, datos y artefactos generados del contexto Docker.
- [x] Confirmar topología de frontend/backend y mecanismo de acceso al servidor privado.
- [x] Preparar Dockerfile(s) y configuración de ejecución con usuario no privilegiado, configuración externa y almacenamiento persistente para llaves de Data Protection.
- [x] Preparar proxy de `/api`, navegación Angular y escucha interna; añadir pruebas estáticas de configuración. La verificación real en Docker permanece pendiente.
- [x] Configurar GitHub Actions para pruebas, build de imagen y despliegue mediante el mecanismo seleccionado; separar eventos de código no confiable del servidor privado.
- [x] Documentar variables sin valores, configuración HTTPS y TLS PostgreSQL, prerrequisitos y rollback seguro.
- [x] Ejecutar comprobaciones locales disponibles y registrar limitaciones.
- [ ] Construir y ejecutar imágenes y verificar IP/HTTPS, conexiones PostgreSQL, volumen y flujo real cuando Docker y el servidor estén disponibles.

**Resultado local:** 45/45 pruebas de configuración, 144/144 de Angular y 254/254 del servidor; `npm run build:clean` y `dotnet publish` aprobados. Los YAML se analizaron sintácticamente. La máquina no tiene Docker ni Nginx: las imágenes, Compose, Caddy, runner y despliegue no se ejecutaron ni validaron de extremo a extremo.

## Revisión de arquitectura presentada por el usuario

El diagrama posterior muestra Caddy en una red Docker de proxy, un contenedor web que incluye frontend y backend, y PostgreSQL existente en una red Docker de backend. Esto difiere de la implementación inicial con dos contenedores y Caddy en el host. **La configuración inicial no debe considerarse compatible con el diagrama hasta aclarar y validar la topología real.**

- [x] Confirmar un contenedor web que incluye frontend y backend.
- [x] Confirmar Caddy en el host y PostgreSQL existente fuera de Docker; no crear ni modificar la base de datos. La red Docker web y la IP del proxy quedan por verificar en el servidor.
- [x] Compilar Angular en una etapa y servirlo desde ASP.NET cuando `Spa:Enabled` está activado; pruebas locales cubren rutas SPA, errores y autorización de `/api`.
- [x] Preparar una red Docker web externa y un archivo opcional para la futura red de PostgreSQL; restringir cabeceras reenviadas a la IP de Caddy configurada. La red y la IP efectivas no se han comprobado.
- [x] Ajustar imagen, Compose, GitHub Actions y documentación a Caddy en el host y PostgreSQL remoto; la comprobación de `/api/health` utiliza el puerto local del contenedor.
- [ ] Validar imagen, Caddy, IP de proxy, conectividad PostgreSQL, TLS, volumen y recuperación cuando Docker y el servidor estén disponibles.

**Aclaración:** el diagrama mostraba Caddy y PostgreSQL en redes Docker; el usuario confirmó después que Caddy está en el host y PostgreSQL existe fuera de un contenedor. La configuración activa refleja esa situación; la segunda red Docker de PostgreSQL es una opción futura y no crea el servicio. No se han ejecutado Docker ni Actions.

**Verificación de la arquitectura actual:** `npm run test:server` aprobó 255 pruebas; la prueba de SPA posterior al ajuste de métodos pasó; `npm run test:env` aprobó 44 pruebas. La solución compiló con tres advertencias conocidas del proyecto auxiliar IdentityHarness. Se analizaron los YAML y comandos shell. Docker, Caddy, PostgreSQL remoto y GitHub Actions no se ejecutaron. El runner exige Docker Engine 28 o posterior; hay que verificar firewall y rutas directas antes de exponerlo.
