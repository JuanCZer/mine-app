# Plan de implementación: mapa de incidencias por sectores

> **Ejecución:** usar subagentes por tarea y revisión independiente. Marcar las casillas solo cuando se tenga evidencia de las pruebas y sus resultados.

**Objetivo:** sustituir el filtro y la lista lateral del mapa por una visualización de sectores coloreados según el número de incidencias, con puntos individuales al acercar y una ficha enlazada al registro.

**Arquitectura:** el backend calcula conteos completos por sector para las incidencias que el usuario puede consultar y ofrece puntos resumidos para la zona visible. Angular usa fuentes y capas compartidas de Mapbox para representar sectores y puntos, y muestra una ficha accesible al seleccionar una incidencia.

**Tecnología:** Angular, Mapbox GL JS, ASP.NET Core y PostgreSQL.

**Estado:** correcciones implementadas y verificadas localmente el 29 de octubre de 2026; siguen pendientes PostgreSQL real y revisión visual en navegador.

**Decisiones confirmadas por el usuario:** los sectores serán una cuadrícula geográfica; el mapa contará y mostrará todas las incidencias de la aplicación; el rojo aplicará desde 600 incidencias en adelante, incluidas las superiores a 1000; todos los roles pueden ver la distribución; el amarillo indica crecimiento de 100 a 300, ambos incluidos.

## Decisiones pendientes antes de implementar

1. **Cuadrícula geográfica:** definir tamaño, origen y proyección fijos, incluida la cobertura, antes de implementar. Los grupos automáticos de Mapbox cambian con el zoom y no equivalen a sectores estables.
2. **Alcance de datos:** el usuario solicitó todas las incidencias de la aplicación. Actualmente `/api/notes` y el detalle se limitan al propietario: hay que implementar una política explícita y coherente para que los usuarios autorizados puedan ver conteos, puntos y fichas de otros propietarios, sin conceder por ello permisos de edición. Todos los roles autenticados pueden acceder al mapa compartido; la ficha compartida es de solo lectura y no modifica los permisos de edición existentes.
3. **Rangos confirmados sin solapamientos:** verde **0–99**, amarillo **100–300** (cantidad creciente), naranja **301–599** y rojo **600 o más**. Así, 100 y 300 son amarillos, y 600 es rojo.
4. **Actualización:** definir si basta con actualizar al abrir y recargar la página o si se requiere actualización automática; no prometer tiempo real sin mecanismo y pruebas.
5. **Ficha:** confirmar los campos resumidos que pueden mostrarse. Propuesta: identificador, título, ubicación, fase/estado disponible y botón «Ver incidencia»; no incluir información sensible adicional sin necesidad.

## Hallazgos del estado actual

- `src/app/pages/map/map.ts` consume `NotesService.list()`, filtra coordenadas y crea un marcador y una capa de radio por incidencia; `map.html` muestra el filtro y panel lateral.
- La consulta actual se limita por defecto a 100 registros y como máximo a 500; no permite calcular correctamente sectores con 600–1000 incidencias. Además excluye eliminadas y aplica propiedad del usuario.
- El campo histórico `altitude` representa **latitud**, mientras `longitude` representa longitud. El campo de área o estado es texto libre, no una delimitación geográfica confiable.
- La ficha ya puede abrirse con `/incidencias?note=<id>`. El mapa también acepta `?note=<id>` para centrar una incidencia; ambas rutas deben conservarse con comprobación de acceso.

## Tarea 1 — Definir sectores y reglas de conteo

- [ ] Definir tamaño, origen y proyección de la cuadrícula; actualizar este plan con esas decisiones. Mantener el acceso de lectura del mapa para todos los roles autenticados y los rangos confirmados.
- [ ] Definir coordenadas válidas, incidencias eliminadas, estado de los registros, fechas incluidas y cómo se actualiza el conteo cuando una incidencia cambia de coordenadas o se elimina.
- [ ] Para polígonos administrativos, incorporar datos oficiales/versionados y claves estables; definir la asignación por coordenadas, el criterio determinista para puntos sobre fronteras y la contabilización de puntos fuera de cobertura. Verificar licencia y actualización de la fuente.
- [ ] Para cuadrícula, definir celdas estables y generar también celdas vacías dentro del alcance visible; no interpretar los clusters de pantalla como sectores permanentes.
- [x] Establecer en la leyenda que los colores representan **número de incidencias**, no población ni una clasificación automática de riesgo. La fuente del conteo actual es parcial; no debe interpretarse todavía como el total de la aplicación.

**Aceptación:** cada incidencia autorizada con coordenadas válidas se cuenta una sola vez o se informa como fuera de cobertura; los rangos no se solapan y los sectores sin incidencias pueden distinguirse de datos no disponibles.

## Tarea 2 — Backend: agregados y puntos

- [ ] Diseñar endpoints autenticados de agregados por sector y de puntos resumidos por límites del mapa; definir contratos tipados, límites, paginación y errores. Aplicar en ambos la misma política de acceso que a la ficha.
- [ ] Implementar el conteo en el servidor sin depender del límite de `/api/notes`; incluir únicamente registros autorizados y no eliminados, sin deduplicar incidencias distintas que compartan coordenadas.
- [ ] Cargar puntos por área visible y paginación o indicar claramente si hay resultados truncados; validar límites, tamaño de respuesta, identificadores y coordenadas finitas en el servidor.
- [ ] Mantener el enlace `?note=<id>` con recuperación autorizada aunque el registro no se encuentre en la primera página de puntos.
- [ ] Agregar pruebas de 0, 99, 100, 299, 300, 301, 599, 600, 1000 y 1001 según los rangos confirmados; probar más de 500 registros, coordenadas duplicadas e inválidas, frontera, fuera de cobertura, actualización y borrado.
- [ ] Probar el acceso compartido: todos los roles autenticados pueden consultar agregados, puntos y fichas de otros propietarios sin adquirir permisos de edición; una solicitud sin sesión recibe una denegación coherente y no obtiene agregados ni detalles.
- [ ] Si se requieren índices, vistas, extensión geoespacial u otros cambios de base, preparar un único SQL revisable y su guía para ejecución manual; no crear migraciones ni modificar la base remota durante las pruebas.

**Aceptación:** las cifras son completas para el alcance autorizado, sin depender de una página parcial y sin exponer fichas o conteos de otros usuarios sin autorización.

## Tarea 3 — Mapa de sectores y niveles de zoom

- [ ] Quitar el filtro por área y el panel lateral de lista; dedicar el espacio disponible al mapa, conservando controles necesarios, estados de carga/error y créditos de Mapbox.
- [ ] Dibujar sectores con polígonos o celdas reales y colorear por conteo con los rangos aprobados; incluir leyenda legible, cifra de cada sector y una representación diferenciada para datos no disponibles.
- [ ] Mostrar los puntos individuales a partir de un umbral de zoom documentado y ajustable; conservar su alineación con coordenadas reales. Si hay demasiados puntos, usar paginación o agrupación temporal sin confundirla con el conteo por sector.
- [ ] Utilizar fuentes y capas compartidas, no una fuente por incidencia. Gestionar cambios de vista, carga, limpieza, resize y destrucción del mapa.
- [ ] Comprobar estados sin incidencias, error de red, coordenadas inválidas, muchos puntos y tamaños de pantalla; mantener acceso mediante teclado y texto además del color.

**Aceptación:** el mapa muestra los sectores y sus valores reales; al acercar se pueden identificar las incidencias sin perder contexto ni bloquear la interfaz.

## Tarea 4 — Ficha y navegación a incidencia

- [ ] Al seleccionar un punto, abrir una ficha pequeña con los campos autorizados y el enlace «Ver incidencia» a `/incidencias?note=<id>`.
- [ ] Renderizar los valores de manera segura, sin insertar HTML sin sanitizar; comprobar que títulos o descripciones con caracteres especiales no ejecutan código.
- [ ] Permitir abrir/cerrar con teclado, anunciar la ficha y devolver el foco; probar puntos en el mismo lugar, incidencia eliminada y acceso denegado.
- [ ] Conservar el enlace existente desde la incidencia hacia `/mapa?note=<id>` y centrar el punto solo después de verificar acceso y coordenadas.

**Aceptación:** el usuario puede revisar y abrir la incidencia correcta, sin revelar información ajena ni perder navegación accesible.

## Tarea 5 — Pruebas, rendimiento y documentación

- [ ] Ejecutar pruebas unitarias y de integración de conteos, autorización, endpoints, límites de color, mapa, ficha y navegación. Registrar los comandos y resultados reales.
- [ ] Ejecutar `npm test -- --watch=false`, `npm run build:clean`, compilación Release del backend y la suite de backend aplicable; resolver fallos antes de marcar casillas.
- [ ] Probar en navegador real escritorio, móvil y zoom de texto; revisar rendimiento con datos de prueba representativos de miles de incidencias y sectores.
- [ ] Probar PostgreSQL en un entorno aislado; documentar las verificaciones remotas que no puedan ejecutarse, sin usar la base operativa para pruebas destructivas.
- [ ] Actualizar esta guía, el objetivo correspondiente y las instrucciones de despliegue con la fuente de geometrías, actualización, permisos, límites, configuración y evidencia.

**Criterio final:** conteos completos y autorizados, colores correctos, puntos visibles al acercar y ficha navegable, con pruebas y limitaciones documentadas.

## Referencias técnicas

- [Mapbox: mapa coroplético y capas por nivel de zoom](https://docs.mapbox.com/mapbox-gl-js/example/updating-choropleth/).
- [Mapbox: fuentes y capas de polígonos](https://docs.mapbox.com/mapbox-gl-js/guides/styles/work-with-layers/).
- [INEGI: Marco Geoestadístico](https://www.inegi.org.mx/programas/mg/default.html). Sus áreas geoestadísticas requieren revisar alcance y uso antes de adoptarlas como sectores.

## Revisión de implementación — 29 de octubre de 2026

**Realizado parcialmente:** se retiró el filtro, existe una cuadrícula en el frontend, una leyenda de rangos, una fuente compartida para polígonos y un popup con enlace a `/incidencias?note=<id>`. La función de colores y sus tres pruebas focales incluyen 100, 300, 301 y 600. Estos avances no cierran las tareas compuestas.

**Hallazgos pendientes:**

- La cuadrícula desplaza las celdas en longitudes negativas: `Math.ceil` genera, por ejemplo, [-99, -98] para una incidencia en -99.1332, fuera de su propia celda.
- El indicador de la lista pinta 300 como naranja aunque el rango confirmado y el color del polígono lo asignan a amarillo.
- Se utiliza la lista de incidencias del usuario actual, limitada y paginada; no hay agregación del backend ni lectura compartida. El texto que afirma mostrar el total de incidencias no está sustentado.
- Los puntos se muestran sin condición de zoom; todavía hay un panel con lista de sectores y no se representan las celdas vacías.
- La búsqueda por `?note=` depende de la página cargada y puede centrar otra incidencia si no encuentra la solicitada. No hay pruebas suficientes de ficha, teclado, permisos, conteos completos ni navegación cruzada.

**Evidencia:** `npm test -- --watch=false` pasó 114/114; `npm run build:clean` pasó sin advertencias; prueba focal de cuadrícula 3/3; compilación Release del backend sin advertencias. Estas pruebas no validan los escenarios señalados. Sin pruebas de PostgreSQL real ni revisión visual en navegador.

## Correcciones y verificación — 29 de octubre de 2026

- [x] Cuadrícula de 1 grado con cálculo coherente de latitud y longitud, colores sin solapamientos y leyenda sin lista lateral. Los puntos se solicitan y muestran al acercar (zoom 8 o superior).
- [x] Endpoints autenticados de conteo completo y puntos paginados por área; detalle compartido en modo de solo lectura para incidencias con coordenadas válidas, sin ampliar permisos de edición.
- [x] Ficha accesible y enlace independiente de solo lectura; búsqueda por ID sin seleccionar otra incidencia cuando no existe. Incidencias coincidentes muestran enlaces individuales.
- [x] Errores de carga, reintento y recuperación del estilo; datos no disponibles no se representan como sectores vacíos.
- [x] Pruebas locales: frontend 128/128 y build:clean sin advertencias; backend 244/244 y Release sin advertencias. Revisión cruzada completada y hallazgos corregidos. El backend de las pruebas de mapa usa el proveedor de archivos de Testing.
- [ ] Confirmar consultas Npgsql, permisos, paginación y conteos con PostgreSQL aislado; no se utilizó el servidor remoto.
- [ ] Comprobar comportamiento visual, selección y teclado en navegador real con Mapbox, incluidos sectores vacíos y puntos coincidentes.

La cuadrícula de un grado se adoptó como tamaño técnico inicial, no representa áreas de igual superficie. La vista compartida muestra la ficha completa de las incidencias geolocalizadas a todos los roles autenticados, conforme al alcance del mapa; la edición sigue separada.
