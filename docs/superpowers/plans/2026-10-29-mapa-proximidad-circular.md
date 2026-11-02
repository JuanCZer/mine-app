# Plan de trabajo: zonas circulares de proximidad en el mapa

> **Ejecución:** utilizar subagentes dedicados y revisión independiente; marcar las casillas únicamente cuando exista evidencia de cada actividad.

**Objetivo:** sustituir los sectores cuadrados por zonas circulares de 1 km y mostrar la concentración de incidencias cercanas sin duplicar incidencias en las cifras generales.

**Arquitectura:** el backend calcula distancias geográficas entre coordenadas autorizadas y entrega resultados resumidos; Angular representa círculos geodésicos y puntos en Mapbox. La solución conserva el acceso de lectura para todos los roles autenticados y la ficha de incidencias sin ampliar permisos de edición.

**Tecnología:** Angular, Mapbox GL JS, ASP.NET Core y PostgreSQL.

**Estado:** correcciones implementadas y probadas localmente el 29 de octubre de 2026. Quedan pendientes la validación con PostgreSQL aislado y la revisión visual con Mapbox real.

## Reglas y decisiones

- El radio solicitado es **1,000 metros**, no un cuadrado de 1 km ni 1 km². La distancia se calculará sobre coordenadas geográficas y el límite de 1,000 m se incluye.
- Cada incidencia se identifica por su ID y cuenta **una sola vez en el total general**, aunque esté dentro de varios círculos. Dos incidencias distintas con las mismas coordenadas sí son dos registros.
- La proximidad se determina comparando pares únicos de incidencias distintas; A–B y B–A representan la misma relación. Cada círculo muestra un conteo local de IDs distintos cercanos; los conteos de círculos que se solapan no se sumarán como un total general.
- **Centro confirmado por el ejemplo:** cada incidencia geolocalizada es centro; se cuenta una vez junto con las incidencias distintas a una distancia de hasta 1 km. Los círculos pueden solaparse sin duplicar el total general. El ejemplo de una incidencia central y 105 cercanas produce 106 y color amarillo.
- Si se prefiere un solo círculo por grupo, definir su centro y política de pertenencia antes de implementar: las agrupaciones transitivas pueden incluir puntos separados por más de 1 km. No asumir que un clúster de pantalla tiene radio geográfico real de 1 km.
- Colores existentes: verde **0–99**, amarillo **100–300**, naranja **301–599**, rojo **600 o más**. En círculos centrados en una incidencia, el mínimo local es 1; zonas sin círculo no deben presentarse como un conteo medido de cero.
- Se cuentan todas las incidencias accesibles para el mapa compartido, sin las eliminadas o con coordenadas ausentes/inválidas. No se deducen permisos de edición de la visibilidad del mapa.
- Sin cambios automáticos ni migraciones de base de datos: cualquier SQL necesario se entregará en un solo archivo para revisión y ejecución manual en el servidor.

## Estado actual

- `backend/Data/PostgresNoteRepository.cs` y `backend/Data/NoteStore.cs` calculan sectores por grados, no distancias en kilómetros. `backend/Data/IMapRepository.cs` y `/api/map/sectors` exponen esa cuadrícula.
- `src/app/pages/map/map.ts` dibuja polígonos cuadrados y una capa de fondo verde. Los puntos y sus fichas ya se cargan de forma separada; deben mantenerse disponibles.
- El backend de incidencias almacena latitud y longitud; el modelo histórico llama `altitude` a la latitud. El esquema no declara actualmente PostGIS ni un índice espacial.
- Las pruebas locales y la compilación no sustituyen la validación contra PostgreSQL aislado ni la revisión real de Mapbox en navegador.

## Tarea 1 — Definir la semántica de proximidad

- [ ] Confirmar si el centro propuesto es cada incidencia y si el conteo local incluye la incidencia central. El código usa esa regla, pero no consta confirmación explícita de ella.
- [ ] Definir visualización de círculos solapados y cómo se explica su conteo; la vista presenta cada zona como círculo de 1 km y evita inflar el total general al sumar superposiciones.
- [ ] Definir cuándo se recalcula tras crear, editar coordenadas o eliminar una incidencia, y qué datos se muestran mientras se carga o falla la consulta.
- [ ] Fijar comportamiento de duplicados de coordenadas, puntos en el límite de 1,000 m, coordenadas inválidas, antimeridiano y latitudes altas; el cálculo usa distancia geodésica con validación de coordenadas y se excluyen registros inválidos.

**Aceptación:** un ejemplo con círculos que se cruzan muestra cada incidencia una sola vez en el total general y explica que el conteo de cercanía pertenece a cada círculo.

## Tarea 2 — Cálculo geográfico y contrato del backend

- [ ] Crear un contrato de zonas circulares para la vista solicitada: la respuesta del mapa devuelve cada centro con latitud, longitud, conteo local y radio de 1,000 m, además del total único sin sumar zonas y sin depender del viewport.
- [ ] Sustituir la agregación `floor` por una distancia geodésica en metros con regla `distancia <= 1000`; el cálculo en backend usa haversine y el frontend dibuja círculos geodésicos de 1 km.
- [ ] Determinar si PostgreSQL tiene PostGIS y permisos para su uso. Si se adopta, evaluar `geography` y un índice apropiado; si no, diseñar prefiltrado espacial conservador más cálculo geodésico, con límites y mediciones. Mantener el comportamiento coherente en el proveedor de pruebas.
- [ ] Calcular vecinos sobre **todos los datos autorizados**, no solo sobre la página de puntos visibles; incluir el margen de 1 km fuera del viewport cuando corresponda para no truncar conteos junto al borde.
- [ ] Evitar consultas cuadráticas sin límite: definir paginación o alcance por viewport, índices y presupuesto de respuesta, y medir con datos representativos. Una respuesta parcial debe indicarlo explícitamente.
- [ ] Crear pruebas para 999 m, exactamente 1,000 m y 1,001 m; IDs duplicados, coordenadas idénticas, círculos solapados, frontera del viewport, longitud negativa, antimeridiano, latitudes altas, borrado, actualizaciones y más de 500 registros.
- [ ] Probar que todos los roles autenticados reciben el mismo alcance de lectura aprobado, que solicitudes sin sesión fallan y que no se conceden permisos de edición.
- [ ] Si se requiere extensión, índice, vista u otro cambio en PostgreSQL, preparar un solo archivo SQL y su guía para ejecución manual; probar primero en una base aislada.

**Aceptación:** la respuesta cuenta cada ID una sola vez por círculo y una vez en el total general; no depende del límite de paginación y respeta el radio de 1 km.

## Tarea 3 — Círculos y visualización en Mapbox

- [x] Reemplazar las capas de cuadrícula y fondo verde mundial por una capa de zonas calculadas; la vista actual usa círculos geodésicos de 1 km y mantiene la carga y los errores del mapa.
- [x] Generar círculos como geometría geográfica de radio 1,000 m, no como radios en píxeles. La distancia usada para contar y la geometría mostrada se mantienen coherentes con la lógica geodésica.
- [ ] Colorear cada zona según su conteo local con los umbrales acordados y mostrar una leyenda y etiqueta numérica; las zonas superpuestas no se suman al total general.
- [ ] Definir y probar el nivel de zoom adecuado: un círculo de 1 km puede ser poco visible a escala nacional. Al acercar, conservar los marcadores de incidencias y su agrupación accesible cuando comparten coordenadas.
- [ ] Usar fuentes y capas compartidas, limitar datos por zona y cancelar o descartar respuestas obsoletas; limpiar eventos y recursos al salir.
- [ ] Probar accesibilidad, teclado, contraste, móvil y recuperación de errores, además del comportamiento con miles de incidencias y zonas superpuestas.

**Aceptación:** los círculos representan 1 km sobre el terreno y su color corresponde al conteo local; el total general no se infla por superposición.

## Tarea 4 — Ficha, navegación y revisión final

- [x] Conservar la ficha de solo lectura y su enlace a la incidencia correcta; la ruta de detalle y el flujo de accesso siguen funcionando con la API compartida.
- [ ] Ejecutar pruebas de backend y frontend: `dotnet build backend/Notes.Api.csproj -c Release --nologo -warnaserror`, `npx vitest run src/app/pages/map/map-sector.util.spec.ts` y `npm run build`; registrar resultados.
- [ ] Revisar el cálculo y la autorización con un subagente independiente; corregir hallazgos y repetir las pruebas afectadas.
- [ ] Probar consultas e índices en PostgreSQL aislado, y comprobar visualmente radio, zoom, solapamientos y fichas en navegador real con Mapbox.
- [ ] Actualizar este plan y el seguimiento del mapa; dejar expresamente pendientes las pruebas que no se hayan podido ejecutar.

**Criterio final:** mapa circular, distancias comprobadas, conteos sin duplicados según la regla acordada y evidencia de pruebas locales y de integración.

## Referencias técnicas

- [PostGIS: `ST_DWithin` con geography y distancia en metros](https://www.postgis.net/docs/manual-3.5/ST_DWithin.html).
- [Mapbox: fuentes y capas; los radios de una capa circle se miden en unidades de pantalla](https://docs.mapbox.com/mapbox-gl-js/guides/styles/work-with-layers/).

## Revisión independiente — 29 de octubre de 2026

- PostgreSQL compara coordenadas en lugar de IDs: dos incidencias en el mismo punto no se cuentan entre sí; el proveedor de archivos compara IDs y sus pruebas no detectan la diferencia.
- Los repositorios cargan todos los registros y calculan distancias en un bucle cuadrático por solicitud, sin límite ni prefiltrado. Falta medición de rendimiento y control de carga.
- Los círculos desaparecen al zoom 8, cuando aparecen los puntos; los solapamientos pueden mezclar colores, y una recarga de zonas no actualiza la fuente ya creada. La interfaz no explica el total único frente a los conteos locales.
- `npm test -- --watch=false`: 125/129 aprobadas; cuatro fallos en `map.spec.ts` esperan dos fuentes cuando la implementación actual crea una. Se requiere revisar las aserciones y el comportamiento, no solo cambiar los números.
- `dotnet build backend -c Release --nologo -warnaserror` y `npm run build:clean` pasaron sin advertencias; la prueba aislada de geometría pasó 5/5, pero no comprueba suficientemente el límite exacto ni los casos geográficos.
- No se ejecutaron consultas contra PostgreSQL aislado ni comprobaciones visuales con Mapbox real. Mantener pendientes las pruebas de distancias, permisos reales, concurrencia y escala.

## Correcciones y verificación — 29 de octubre de 2026

- [x] Conteo por registro para coordenadas coincidentes en ambos proveedores; índice espacial y agrupación de coordenadas para reducir el coste. El ejemplo central + 105 incidencias produce 106 y amarillo.
- [x] Límites de carga: hasta 50.000 ubicaciones por defecto (`Notes:MaximumMapLocations`, configurable), dos cálculos concurrentes por proceso y error explícito al superar capacidad; sin entregar conteos parciales. La conexión PostgreSQL se libera antes del cálculo.
- [x] Zonas y puntos visibles simultáneamente al acercar, actualización de datos, descarte de respuestas tardías, total único y explicación de conteos locales. El color de mayor conteo se muestra sobre los demás en solapamientos.
- [x] Dibujo limitado a círculos cerca del viewport, con máximo 1.000 zonas y 1.000 incidencias visibles; cuando se supera un límite se solicita acercar, sin mostrar un subconjunto como completo.
- [x] Pruebas frontend 138/138, build:clean sin advertencias; suite completa backend 252/252, pruebas focales de incidencias 32/32, prueba de concurrencia/cancelación 1/1, compilación Release sin advertencias. Revisiones independientes del cálculo y de filtros/zoom; sin nuevos hallazgos en el alcance revisado.
- [ ] Verificar resultado visual, círculos superpuestos y antimeridiano con Mapbox en navegador real. La opacidad elegida evita mezclar colores, pero puede ocultar elementos del mapa base.
- [ ] Ejecutar las consultas del proveedor PostgreSQL y mediciones de carga en una base aislada; el límite de dos cálculos es por proceso, no global entre réplicas.

### Ajuste de leyenda

- [x] Ubicar la leyenda en una columna lateral compacta en escritorio y debajo del mapa en pantallas pequeñas. Pruebas frontend 140/140 y build:clean sin advertencias; revisión visual en navegador aún pendiente.
