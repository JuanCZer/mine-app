# Plan general de trabajo por objetivos

**Estado al 18 de octubre de 2026: Objetivos 1, 5 y 6 completaron su implementación; Objetivos 2 y 4 están implementados con puertas de infraestructura abiertas; el Objetivo 3 continúa por importación, TLS, respaldo y validaciones operativas. Las puertas externas de los Objetivos 5 y 6 bloquean Production, pero no representan código pendiente. El modelo de amenazas y el escaneo estándar de seguridad ya están terminados. El Task 19 documenta la matriz de QA de user administration, la evidencia de progreso y el gate de build limpio Angular.**

Este conjunto documenta el trabajo solicitado, su secuencia y la evidencia acumulada. Una casilla marcada representa trabajo comprobado en el repositorio o en la instancia indicada; no implica por sí sola que el sistema esté desplegado ni que hayan pasado las puertas operativas de producción.

**Decisiones confirmadas por el usuario:** PostgreSQL como base de datos; retirar GDS de la navegación y permitir colapsarla; ejecutar mediante subagentes.

**Base verificada en el repositorio:** Angular `^22.0.0` en `package.json` y .NET `net10.0` en `backend/Notes.Api.csproj`; no se plantea actualizar el framework.

**Regresión actual:** importador 4/4, contrato SQL 16/16, backend 136/136, Angular 52/52 y entorno 32/32; builds .NET y Angular y `git diff --check` aprobados. Los contratos adicionales cubren la normalización del backfill, la detección de identificadores cruzados y el rechazo de URLs de despliegue sin `sslmode=verify-full`. Estas pruebas no sustituyen la ejecución del importador, la reaplicación doble de `apply.sql` ni la conciliación en PostgreSQL aislado.

**Propuestas técnicas del plan:** reutilizar Angular y ASP.NET Core existentes, evolucionando `mine-app/backend` sin crear un backend desde cero; Identity con almacenamiento PostgreSQL mediante Npgsql, cookie de sesión bajo mismo origen, protección XSRF y roles/permisos propuestos en el objetivo 4. La matriz de permisos, política de sesiones y despliegue se acuerdan antes de implementar sus tareas dependientes.

**Forma de trabajo:** actividades mediante subagentes dedicados; el coordinador integra contratos, dependencias y resultados. Máximo cuatro agentes activos, incluido el coordinador.

## Planes por objetivo

| Objetivo                       | Plan específico                                               | Entregable                                                                                                |
| ------------------------------ | ------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| 1. Interfaz y navegación       | [01 · Interfaz y navegación](2026-09-27-01-ui-navigation.md)  | Barra corregida sin GDS ni pie ficticio; menú ocultable, persistencia y navegación accesible.             |
| 2. Catálogos y datos dinámicos | [02 · Catálogos](2026-09-27-02-catalogs.md)                   | Catálogos persistidos y contratados; formularios consumiendo datos reales con estados de carga y error.   |
| 3. PostgreSQL                  | [03 · PostgreSQL](2026-09-27-03-postgresql.md)                | Modelo relacional, SQL directo revisable, repositorios e importación comprobable de los datos existentes. |
| 4. Autenticación               | [04 · Autenticación](2026-09-27-04-authentication.md)         | Login funcional, sesión de servidor, autorización y atribución real de acciones.                          |
| 5. Backend y seguridad         | [05 · Backend y seguridad](2026-09-27-05-backend-security.md) | API con reglas y permisos comprobados; hallazgos validados, parches y verificación independiente.         |
| 6. Variables de entorno        | [06 · Configuración de entorno](2026-09-27-06-environment.md) | Configuración reproducible, ejemplos sin secretos y separación entre valores públicos y privados.         |

Cada documento contiene sus archivos, tareas y criterios de aceptación. Su numeración identifica objetivos; el orden de ejecución se define por las dependencias siguientes.

## Punto de partida histórico

- La estructura del menú utiliza flex sin dirección de columna, consistente con los elementos desplazados de la captura.
- La barra no admite ocultación de escritorio; «Espacio de trabajo» es texto fijo.
- La cabecera muestra estados fijos que no demuestran salud del servicio, autenticación ni garantías del entorno.
- ASP.NET Core ya ofrece endpoints de incidencias y fases; utiliza almacenamiento JSON y no tiene proveedor SQL configurado.
- El módulo de detección conserva datos en `localStorage` y continúa identificado como módulo legado. El objetivo 3 ofrece exportación e importación explícita opcional: conserva originales y crea incidencias con fases pendientes, sin convertir su historial en validaciones autenticadas.
- En el levantamiento inicial, el acceso era de demostración y la ruta de login estaba desactivada; el Objetivo 4 sustituyó ese comportamiento por sesión autenticada.
- Los riesgos de seguridad indicados requieren la validación definida en el objetivo 5 antes de declararlos solucionados.

## Contratos compartidos y decisiones de integración

| Área                    | Contrato propuesto y documento propietario                                                                                                                                                                                   |
| ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Persistencia            | PostgreSQL y Npgsql. El objetivo 3 define el esquema SQL aplicado directamente, el repositorio y la compatibilidad.                                                                                                          |
| Identidad               | ASP.NET Core Identity con identificadores `Guid`, integrado en el contexto de datos acordado; el objetivo 4 define usuarios y sesiones.                                                                                      |
| Sesión del navegador    | Cookie HttpOnly/Secure/SameSite=Lax bajo mismo origen y protección XSRF; no introducir un JWT artesanal. Detalle en objetivo 4.                                                                                              |
| Autenticación HTTP      | `GET /api/auth/csrf`, `POST /api/auth/login` con `{username,password}` y respuesta `{user}`, `GET /api/auth/me`, `POST /api/auth/logout` con 204.                                                                            |
| Incidencias y fases     | Conservar compatibilidad del contrato existente, `expectedRevision`, guardado separado de validación y secuencia obligatoria. Objetivos 3 y 5.                                                                               |
| Catálogos e incidencias | El objetivo 2 define códigos estables, respuestas y consumidores; el objetivo 3 aporta claves y relaciones persistentes para incidencias. Detección local consume catálogos sin sincronizar automáticamente sus expedientes. |
| Configuración privada   | `ConnectionStrings__Postgres` se resuelve exclusivamente en ASP.NET Core; objetivo 6.                                                                                                                                        |
| Configuración pública   | Solo `MAPBOX_PUBLIC_TOKEN` se genera desde `.env` para Angular; la API usa `/api` relativo y proxy/mismo origen según el objetivo 6.                                                                                         |

Antes de codificar, los propietarios de datos, autenticación y API deben fijar el mismo esquema y contratos. Una modificación posterior exige actualizar productores, consumidores, el archivo SQL y las pruebas afectadas en la misma entrega.

Los códigos del catálogo, claves de fase y valores históricos no se cambian solo para mejorar etiquetas. La identidad autenticada debe sustituir al responsable técnico deducido del cliente, sin inventar autoría para registros heredados.

## Oleada A: interfaz independiente

- [x] Ejecutar objetivo 1 en los archivos del layout, con una reserva exclusiva de esos archivos.
- [x] Retirar GDS de la navegación y «Espacio de trabajo»; sustituir estados aparentes de la cabecera por texto neutral.
- [x] Completar la matriz visual desktop/tablet/móvil y comprobar con un formulario real modificado que ocultar el menú no pierde datos. La evidencia está en `docs/qa/objective-1/` y el teclado, la persistencia y el comportamiento del layout tienen pruebas automatizadas.
- [x] Coordinar el espacio de login/cierre de sesión con objetivo 4, sin incorporar identidades ficticias.

Puede avanzar en paralelo al diseño de datos y configuración; su terminación no depende de disponer de PostgreSQL.

## Oleada B: configuración y base de datos

- [x] Ejecutar la base del objetivo 6: carga explícita de variables, ejemplos y validación de configuración.
- [x] Acordar el esquema del objetivo 3 con los requisitos de Identity, catálogos, incidencias, archivo legado y auditoría.
- [x] Incorporar proveedor PostgreSQL, repositorio Npgsql, un archivo SQL directo autocontenido y pruebas de contrato en el backend existente.
- [ ] Preparar respaldo e inventario de las fuentes y ejecutar la importación repetible ya implementada; las correspondencias exigen propietarios activos y la evidencia legado no se atribuye a una cuenta moderna. Falta hacerlo en PostgreSQL aislado y conciliar cada ocurrencia.
- [x] Comprobar arranque con configuración válida y fallo claro sin valores indispensables.

La configuración precede al arranque contra PostgreSQL. El esquema acordado precede a los cambios de repositorios, Identity y contratos nuevos.

## Oleada C: autenticación y capacidades del backend

- [x] Ejecutar objetivo 4 sobre el esquema acordado: identidad, sesión, XSRF, rutas protegidas y login visible.
- [x] Implementar la parte servidor de los catálogos del objetivo 2 y persistir sus códigos/relaciones.
- [x] Adaptar los endpoints de incidencias y fases conservando validaciones, concurrencia y separación entre guardar/validar.
- [x] Comprobar autorización de cada recurso y derivar usuario/actor de la sesión, no de un campo enviado por el navegador.
- [x] Publicar los contratos consumibles y sus respuestas de error antes de conectar los formularios.

Autenticación y catálogos pueden avanzar con propietarios separados tras acordar el esquema; el registro en `Program.cs` se integra por turnos.

## Oleada D: consumo dinámico e importación explícita de datos legados

- [x] Conectar servicios y formularios Angular a los contratos de catálogo, identidad e incidencias; mantener identificado el almacenamiento local del módulo de detección.
- [x] Representar carga, error, vacío y datos desactivados sin reemplazar fallos por listas inventadas.
- [ ] Migrar `notes.json` mediante el importador idempotente del objetivo 3, conservando respaldo y trazabilidad de origen.
- [ ] Ofrecer exportación/importación opcional de expedientes locales: archivar originales completos y crear incidencias con cuatro fases pendientes cuando se elija importarlos.
- [ ] Validar identificadores, coordenadas, evidencias, historial y estado de fases de las notas migradas; documentar por separado los campos y estados conservados en el archivo de detección legado.
- [ ] Confirmar lectura y escritura de incidencias en PostgreSQL antes del corte de `notes.json`; conservar la llave y exportación del navegador, sin retirar automáticamente el almacén de detección.

No eliminar datos JSON/locales por haber recibido una respuesta HTTP exitosa: comprobar conteos, referencias, conflictos y resultado persistido según objetivos 2 y 3.

Este alcance no incluye una API completa de `DetectionCase`. Sus expedientes locales no se consideran sincronizados ni se convierten automáticamente en incidencias validadas.

## Oleada E: seguridad, corrección y regresión

- [x] Registrar la revisión/artefacto de código auditado y el alcance real del objetivo 5.
- [x] Ejecutar escaneos con agentes dedicados y conservar evidencias reproducibles.
- [x] Validar candidatos con agentes distintos; distinguir hallazgos confirmados, descartados y límites de cobertura.
- [x] Encargar los parches a agentes distintos del descubridor, sobre la causa validada y con prueba pertinente.
- [x] Verificar las remediaciones con agentes distintos de sus autores y ejecutar regresión funcional; la última ejecución aprobó backend 136/136, Angular 52/52, entorno 32/32, ambos builds y `git diff --check`.
- [x] Reconciliar resultados con el código final; documentar hallazgos pendientes y limitaciones sin afirmar seguridad absoluta.

Los fallos básicos de acceso se atienden en autenticación/backend; esta oleada verifica el resultado integrado y revisa riesgos adicionales. Un escaneo sin hallazgos no constituye garantía de ausencia de vulnerabilidades.

## Cuatro slots y propiedad de archivos

| Slot | Responsabilidad durante implementación                                            | Responsabilidad durante auditoría                                      |
| ---- | --------------------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| 1    | Coordinador: contratos, orden, reservas de archivos e integración de entregables. | Coordinador: alcance, artefactos y separación entre autores/revisores. |
| 2    | Subagente de datos/API: esquema SQL directo y persistencia.                       | Subagente de descubrimiento.                                           |
| 3    | Subagente de identidad/configuración: sesión, permisos y entorno.                 | Subagente de validación y verificación, distinto del autor del parche. |
| 4    | Subagente de frontend: layout, login y consumidores de catálogos por turnos.      | Subagente de corrección, distinto del descubridor.                     |

Los nombres indican responsabilidades, no permiso para editar simultáneamente los mismos archivos. Se reutilizan slots al cerrar cada tarea; las dependencias secuenciales no generan agentes adicionales.

- `src/app/layout/*`: propietario frontend; autenticación solicita la integración de identidad/cierre de sesión al mismo propietario.
- Scripts SQL y modelos persistentes: propietario de datos; identidad acuerda los cambios de usuarios antes de editar y aplicar el DDL.
- Módulos de autenticación y configuración: propietario identidad/configuración; no modificar repositorios de datos sin traspaso explícito.
- `backend/Program.cs`: un único propietario de turno; objetivo 3 registra datos, objetivo 4 integra Identity/autorización y objetivo 5 endurece el pipeline, en ese orden.
- `backend/Notes.Api.csproj`, modelos compartidos y configuración común: reservar antes de editar; incorporar cambios ajenos mediante el propietario o traspaso documentado.
- Rutas, interceptores y servicios Angular: un propietario por archivo; el agente de consumidores recibe primero el contrato de sesión/API.
- Auditoría y validación son de lectura; el agente de parche recibe una reserva de los archivos concretos y la entrega antes de la verificación.

## Entorno y manejo de credenciales

El `.env` público de la raíz puede contener el token público de Mapbox. Aunque esté ignorado por Git, ese token será visible cuando lo use el navegador: el archivo ignorado no convierte configuración frontend en un secreto.

La conexión PostgreSQL y demás secretos permanecen en la configuración privada del backend, por ejemplo `backend/.env` con carga explícita o variables del proceso. No se generan dentro de archivos Angular, bundles, mapas de código fuente ni respuestas HTTP.

Los `.env` no se cargan automáticamente por su nombre. El objetivo 6 define el mecanismo, precedencia y validación; los archivos de ejemplo contendrán únicamente nombres y valores ficticios.

## Criterios de finalización de la implementación

- [ ] Los seis entregables cumplen sus criterios y cuentan con evidencia vinculada al código final.
- [x] UI comprobada en los tamaños y recorridos de teclado definidos; la matriz, prueba del formulario real, verificación del mapa e informe están en `docs/qa/objective-1/` y el ledger SDD del Objetivo 1.
- [ ] PostgreSQL conserva los datos a través de reinicios y reaplicaciones de `apply.sql`; la importación está conciliada y puede recuperarse de fallos.
- [x] Login, cierre de sesión, XSRF, permisos y aislamiento entre usuarios se verifican con peticiones directas y desde Angular.
- [ ] Catálogos e incidencias usan API/PostgreSQL; el módulo de detección local está identificado y su exportación/importación conserva originales sin prometer sincronización.
- [x] Las fases mantienen concurrencia y reglas de avance; regresión cubre saltos, datos incompletos, revisión obsoleta y cierre.
- [ ] Configuración y ejemplos son reproducibles; no se publican credenciales PostgreSQL ni secretos de sesión.
- [x] Hallazgos corregidos tienen verificación independiente; los pendientes y límites de cobertura están declarados.
- [x] Documentación describe el comportamiento implementado, comandos comprobados y recuperación; no confunde planes con garantías.

**Revisión documental actualizada el 15 de octubre de 2026:** existen el plan general y los seis planes específicos; los estados se reconciliaron con el código, las pruebas y la evidencia registrada. Las casillas abiertas resumen el trabajo técnico u operativo que todavía falta y se detallan en `2026-10-15-plan-status-audit.md`.
