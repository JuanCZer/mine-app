# Informe de QA visual — Objetivo 1

**Fecha:** 16, 19 y 20 de octubre de 2026
**Navegador:** Safari, Responsive Design Mode
**Datos:** dos incidencias sintéticas en el entorno aislado de QA; no se modificó PostgreSQL ni información real.

## Resultado

El cierre vigente del rail conserva **17/17 pruebas focales** y **56/56 pruebas Angular**. La prueba focal compila el LESS real y comprueba el token del rail (72 px), el margen del workspace basado en el mismo token, la ocultación de `.nav-label` al colapsar, su visibilidad al expandir, el contrato CSS del tooltip y la ausencia de `.sidebar-collapsed` a 767 px. jsdom expone el token CSS y el CSSOM, pero no prueba la geometría renderizada ni la apertura del pseudo-elemento; Safari tampoco permitió forzar hover/foco en esta sesión. Por eso el tooltip tiene verificación estructural, mientras la validación visual de su estado abierto permanece `LIMITED` en [matrix.csv](./matrix.csv). Ver `sidebar-icons-task-report.md`.

La optimización de estilos no cambió el presupuesto ni otra configuración de `angular.json`. Ese archivo ya estaba modificado por una configuración previa de `proxyConfig` para `ng serve`; el diff de esta tarea no lo incluye. El build actual finaliza sin advertencias de presupuesto.

La revisión inicial del 16 de octubre quedó complementada el 19 de octubre para el nuevo límite del sidebar. La barra lateral conserva el empuje del workspace desde 768 px; por debajo usa el panel modal. La evidencia de RED/GREEN y de geometría en Safari está en `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-push-task-report.md`.

La mejora del rail quedó comprobada el 19 de octubre con la UI sintética: en Safari se observó el rail compacto a 72 px, el sidebar expandido a 280 px y el control dentro de la barra; el divisor del encabezado del sidebar coincide con el de cabecera a 88 px. A 767 px se conservó el drawer con scrim. El borde de 768 px se verificó en `AppLayout` con `MediaQueryList` simulado; Responsive Design Mode no permitió confirmar el `innerWidth` efectivo exacto al píxel. Detalle y alcance en `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-rail-task-report.md`.

La captura histórica proporcionada por el usuario se conserva como [baseline-2026-09-27-user.png](./baseline-2026-09-27-user.png). Es una referencia cualitativa de 3420 × 1820: el material original no incluía capturas separadas a 1440 × 900 y 375 × 812.

La comparación final se realizó durante la sesión de CUA en Safari y quedó registrada en [matrix.csv](./matrix.csv). Las capturas de ventana completa no se conservaron porque incluían elementos del navegador ajenos a la aplicación; el informe mantiene únicamente la evidencia necesaria del producto y evita incorporar información de otras pestañas.

## Defectos reproducidos, corrección y repetición

1. **Colapso incompleto en Safari.** El estado y `aria-expanded` cambiaban, pero una transición de geometría podía dejar visibles la barra o sus 280 px reservados. Se retiraron las transiciones del ancho y margen. Al repetir en 1440 × 900, el contenido ocupó inmediatamente el espacio y la reapertura mantuvo el formulario.
2. **Historial comprimido en anchos bajos.** En la inspección del 16 de octubre a 900 px las celdas se partían y perdían jerarquía. La tabla ahora conserva un ancho mínimo de 1040 px, evita saltos dentro de celdas y limita el desplazamiento horizontal a `.table-wrap`, que además es una región enfocable con nombre accesible. El registro de modo modal a 900 px quedó supersedido por el breakpoint del 19 de octubre; la tabla no se volvió a inspeccionar en el viewport autenticado durante esta corrección.
3. **Cabecera saturada en el límite de escritorio.** A 901 px el título y los controles competían por espacio. Entre 901 y 1100 px se usa una cabecera compacta. La repetición a 901 y 1024 px dejó título y cierre alineados.
4. **Firma de Fase 1 nula.** Una respuesta con `signatures: null` provocaba `includes` sobre `null` al renderizar. La frontera del frontend normaliza a `[]` y el modelo vacío del backend también produce una lista vacía. Las regresiones Angular y backend cubren ambos lados.

## Interacciones verificadas

- El borrador real `QA-BORRADOR-SIN-GUARDAR` permaneció en «Referencia de línea base» al colapsar, reabrir y cambiar los viewports entre móvil, tablet y escritorio. La interfaz mantuvo «Cambios sin guardar».
- En móvil, abrir el menú ocultó el contenido de fondo del árbol accesible y seleccionar «Mapa» cerró el panel. Las pruebas de `AppLayout` cubren adicionalmente Escape, retorno de foco, Tab, Shift+Tab, navegación cancelada, almacenamiento bloqueado y cambios de breakpoint.
- El mapa mantuvo su contenedor, atribución y controles al colapsar y reabrir sin recargar. Safari no incluyó el canvas WebGL en la captura de la herramienta; la inicialización se acreditó por sus controles y árbol accesible, y el redimensionamiento por la prueba de `ResizeObserver`.
- A 200 % de zoom, la cabecera, menú, botón de nueva incidencia, listado y secuencia de fases conservaron acceso y desplazamiento. Después se restableció el tamaño real.
- El 19 de octubre, Safari confirmó en un harness temporal basado en el LESS del componente: a 767 px nominales, el panel abierto midió 280 px sobre el workspace y mostró scrim; a 1280 px nominales, el workspace midió 1000 px expandido y, al colapsar el rail a 72 px, empieza en x=72 y ofrece aproximadamente 1208 px de ancho útil (1280 − 72). Responsive Design Mode no confirmó el `innerWidth` efectivo al píxel exacto del borde 768; el estado push a 768 está acreditado por el spec `AppLayout` con `MediaQueryList` configurado exactamente a ese valor. La ruta autenticada local redirigió a login sin backend; la comprobación geométrica no usó una base de datos ni datos privados.
- La revisión del mapa de ese día usó el QA sintético autenticado (JSON temporal e InMemory) en puertos libres. A 768 × 1024, con sidebar expandido, el LESS anterior conservaba la grilla `310px 1fr` dentro de unos 440 px útiles, dejando alrededor de 130 px al canvas. Tras hacer `MapPage` contenedor de consultas, el listado y mapa quedaron apilados y el canvas tomó el ancho del panel; a 1440 × 1024 la lista de 310 px y el mapa conservaron dos columnas. No se consultó ni detuvo el `Notes.Api` preexistente en 5068; se usaron 5069/4318 con fixture sintético.
- La redirección de `/deteccion` y `/deteccion/qa-visual-001` terminó en el seguimiento de incidencias esperado.

## Cobertura automatizada histórica asociada

- `AppLayout`: 15/15 casos en el cierre inicial, incluido el formulario `NotesPage` real sin reinicialización.
- Angular: 50/50 casos en el cierre inicial.
- Backend Python: 99/99 casos.
- Compilación Angular y `dotnet build`: sin errores.

En el seguimiento del 19 de octubre, Angular pasó 54/54 y el build de producción terminó sin errores; `git diff --check` también pasó. Se retiró el contrato LESS estático antiguo, que no ejercitaba comportamiento; la evidencia del layout del mapa es la observación geométrica en Safari. El detalle del mapa a 768/1440 está en `sidebar-push-task-report.md`. El cierre del 20 de octubre añadió el contrato CSS verificable al spec de `AppLayout`: focal 17/17, Angular 56/56, build sin warnings y `git diff --check` aprobado.
