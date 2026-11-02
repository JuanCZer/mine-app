# Objetivo 1: plan de implementación de interfaz y navegación

> **Actualización del rail, 20 de octubre de 2026:** se conserva la navegación en el rail de 72 px: los tres enlaces mantienen nombre accesible y foco, ocultan su texto visual y muestran tooltip en hover/focus. Safari con fixture sintético confirmó iconos, ruta activa y los tres enlaces AX a 1440 px; el spec comprobó el contrato CSS del tooltip sin validar visualmente su estado abierto. La prueba focal quedó GREEN 17/17 y la suite 56/56. La verificación de contrato compila el LESS real y consulta CSSOM en jsdom; Safari AX confirmó los tres enlaces del fixture estático. El entorno no permitió activar hover/foco para validar visualmente el estado abierto, que permanece LIMITED. El budget de angular.json no se modificó; ese archivo ya tenía un cambio previo de proxyConfig. Ver `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-icons-task-report.md`.

> **Estado actualizado el 20 de octubre de 2026:** **19 de 19 actividades completadas**. El sidebar modal/push quedó alineado en 767/768; el rail conserva 72 px frente a 280 px expandido, con el control dentro del encabezado; y el mapa apila sus paneles según el ancho disponible de `MapPage`. La revisión de accesibilidad corrigió el foco móvil y los destinos `aria-controls`. La suite Angular aprobó con 56/56, el build terminó y Safari confirmó la geometría con fixture sintético; el borde exacto de 768 px está acreditado por el spec, no por una medición geométrica de Safari. Evidencia en `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-push-task-report.md` y `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-rail-task-report.md`.
> **Actualización del rail, 19 de octubre:** el sidebar abierto mide 280 px, el rail 72 px y el encabezado comparte 88 px con la cabecera principal. El control queda dentro del rail. La revisión posterior agregó dos regresiones de foco y dejó la suite en 56/56; build y diff check aprobados. Evidencia en `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-rail-task-report.md`.
> **Cierre histórico del 18 de octubre de 2026:** 15 de 15 actividades completadas; la regresión consolidada de ese día aprobó 52/52 pruebas Angular, 136/136 pruebas backend y 32/32 pruebas de entorno, además de los builds Angular/.NET y `git diff --check`.
> **Forma de ejecución:** las actividades se distribuyeron entre subagentes dedicados y la evidencia quedó registrada en las casillas, la matriz y el informe de QA.

**Objetivo:** corregir los elementos desplazados, retirar GDS de la navegación y permitir ocultar/reabrir el menú sin perder espacio útil ni accesibilidad.

**Arquitectura:** concentrar la corrección en `AppLayout`, compartido por las páginas funcionales. En pantallas de 768 px o más, conservar una preferencia de menú visible/oculto y desplazar el workspace 280 px al expandir; por debajo de 768 px, usar un panel modal superpuesto que inicia cerrado y cuyo estado es temporal. Los paneles de `MapPage` se apilan cuando su propio ancho útil es reducido, aunque el viewport sea mayor.

**Tecnología:** Angular 22, plantillas HTML, LESS, señales de Angular y pruebas existentes con Vitest/jsdom.

**Especificación:** objetivo 1 de la solicitud del usuario y captura `Screenshot 2026-09-27 at 10.56.16 a.m..png`. Se aplicaron UI/UX Pro Max y diagnóstico sistemático para esta revisión estática.

## Diagnóstico y alcance histórico

- **Confirmado en el diagnóstico inicial:** `src/app/layout/app-layout.less:3` definía `.sidebar` con `display: flex` y omitía la dirección de columna. Sus hijos marca, navegación y pie se distribuían en fila. Esto explicaba la marca centrada verticalmente y el bloque «Espacio de trabajo» desplazado sobre el contenido de la captura. La corrección final se comprobó en navegador y quedó registrada en el informe de QA.
- **Confirmado en la revisión correctiva del 19 de octubre:** el query anterior usaba `max-width: 900px`, por lo que 768 px entraba en modalidad modal aunque el requisito aprobado pide que empuje el workspace desde ese ancho.
- **Confirmado en la revisión geométrica del 19 de octubre:** con el sidebar abierto a 768 px, el workspace conserva 488 px y deja unos 440 px a `MapPage` después del padding. El breakpoint por viewport de 700 px no se activa, así que la cuadrícula 310 px + 1fr deja solo unos 130 px al mapa.
- **Confirmado:** «Espacio de trabajo / Registro y revisión» y el avatar «U» son texto fijo (`app-layout.html:23`). Se propone retirarlos, pues no aportan identidad ni una acción real. GDS se retira del bloque de marca de la navegación; las referencias documentales del módulo de detección no forman parte de esta corrección.
- **Confirmado:** el panel móvil se oculta solo mediante una transformación CSS (`app-layout.less:65`); la plantilla y el controlador no gestionan `aria-expanded`, foco, Escape ni la exclusión de controles ocultos del recorrido por teclado.
- **Confirmado:** la cabecera muestra «Operativo» y «ENTORNO CONTROLADO» como texto fijo (`app-layout.html:41`), sin una fuente de salud del servicio, sesión ni garantías del entorno. Debe utilizar texto neutral hasta disponer de señales reales con significado definido.
- **Riesgo identificado y resuelto:** al cambiar el ancho disponible, los mapas necesitaban actualizar su tamaño. Se añadió observación del contenedor y llamadas de redimensionamiento; la integración se comprobó visualmente y con pruebas unitarias.
- No se encontraron archivos `AGENTS.md` en el repositorio ni en los directorios antecesores revisados. El login tiene plantilla propia y actualmente `/login` redirige a incidencias (`src/app/app.routes.ts:44`); su activación pertenece al objetivo de autenticación.

## Restricciones y dependencias

- Mantener rutas, iconos y tema existentes; no cambiar reglas de negocio, fases ni datos de incidencias.
- No incorporar una nueva biblioteca de componentes para esta corrección.
- Este objetivo puede ejecutarse sin cambios de backend. Coordinar los ajustes de cabecera con la futura identidad/cierre de sesión del objetivo de autenticación.
- La cabecera no debe insinuar estado de salud, autenticación ni garantías que la aplicación no haya comprobado. Usar «Gestión de incidencias» o prescindir de la insignia mientras no exista una fuente real de estado.
- El menú oculto no debe reservar ancho. Solo por debajo de 768 px el panel modal se superpone; desde 768 px el sidebar empuja el workspace. Los componentes deben responder al espacio que realmente reciben cuando sea más estrecho que el viewport.
- La preferencia de escritorio será local al navegador; el panel móvil no persistirá abierto entre visitas. Si el almacenamiento falla, la navegación debe seguir funcionando.

## Archivos previstos

| Archivo                                                                              | Responsabilidad                                                                             |
| ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| `src/app/layout/app-layout.html`                                                     | Retirar marca y pie ficticio; controles de apertura/cierre, relaciones ARIA y foco.         |
| `src/app/layout/app-layout.less`                                                     | Columna del menú, anchos sincronizados, ocultación, panel móvil y scroll interno.           |
| `src/app/layout/app-layout.ts`                                                       | Estado de escritorio/móvil, persistencia, cambios de breakpoint y gestión del foco.         |
| `src/app/layout/app-layout.spec.ts` (nuevo)                                          | Verificar comportamiento del menú y persistencia, sin intentar validar geometría con jsdom. |
| `src/app/pages/notes/notes.less` (condicional)                                       | Ajustar distribución solo si la validación demuestra saturación al cambiar el ancho útil.   |
| `src/app/pages/map/map.ts`, `src/app/components/detection/case-map.ts` (condicional) | Sincronizar el canvas con el contenedor si la comprobación de colapso lo requiere.          |
| `src/app/pages/map/map.less` | Apilar el mapa según el ancho de su componente, manteniendo dos columnas en espacio amplio; validar la geometría en Safari. |

## Tareas

### 1. Corregir la estructura y retirar elementos solicitados

- [x] Registrar y conservar una captura inicial o referencia equivalente en `/incidencias`. Se preservó la captura histórica del usuario de 3420 × 1820 y se documentó explícitamente su alcance cualitativo; la matriz final incluye inspecciones a 1440 × 900 y 375 × 812.
- [x] Retirar el enlace de marca GDS y el bloque estático «Espacio de trabajo» de `app-layout.html`; limpiar únicamente sus estilos sin uso.
- [x] Sustituir «Operativo / ENTORNO CONTROLADO» y su indicador verde por texto neutral de contexto, sin representar una sesión ni salud del servicio inexistentes.
- [x] Organizar `.sidebar` en columna con navegación desplazable verticalmente y sin desbordamiento sobre el contenido; compartir una única medida entre el ancho del menú y el espacio reservado por `.workspace`.
- [x] Entregar la comparación visual final: enlaces dentro de la barra, título y contenido alineados, y ausencia del avatar o texto flotante. Evidencia en `docs/qa/objective-1/visual-qa-report.md` y `matrix.csv`.

### 2. Añadir ocultación de escritorio y panel móvil accesible

- [x] Definir en `app-layout.spec.ts` casos que fallen para ocultar/reabrir, recuperar preferencia al recrear el componente, almacenamiento no disponible y paso escritorio–móvil–escritorio.
- [x] Separar el estado persistente `sidebarCollapsed` del estado temporal `mobileMenuOpen`; persistir solo el primero bajo una clave propia de preferencias de interfaz. En la primera visita, escritorio visible y móvil cerrado.
- [x] Mostrar un botón de menú accesible en todos los tamaños y mantenerlo fuera del contenedor ocultable; al ocultar en escritorio, liberar los 280 px y mantener el botón visible y alcanzable para reabrir. Exponer nombre, `aria-expanded` y `aria-controls` coherentes.
- [x] En <768 px, abrir sobre un fondo de cierre y agregar cierre dentro del panel. Al abrir, llevar el foco al panel; Escape, fondo y navegación completada deben cerrarlo. Si un guard cancela la navegación, mantener la ruta y una posición de foco coherente.
- [x] Excluir enlaces del panel cerrado del foco y de la interacción. Durante el panel modal móvil, impedir foco en el contenido de fondo y devolverlo al botón al cerrar; restablecer estas condiciones al superar el breakpoint.
- [x] Corrección posterior: usar `<768 px` como modalidad modal y `>=768 px` como layout de empuje, conservar las aserciones de accesibilidad y probar ambos lados del límite. Ver `.superpowers/sdd/2026-09-27-01-ui-navigation/sidebar-push-task-report.md`.
- [x] Corrección posterior: hacer que `MapPage` apile el mapa por ancho de contenedor; preservar la cuadrícula de dos columnas en espacio amplio. Safari confirmó el fixture QA a 768 y 1440 px; la evidencia automatizada de esta corrección se limita a la suite Angular y al build.
- [x] Mejora del rail compacto: tokens compartidos 280/72/88 px; toggle de escritorio dentro de `.sidebar-heading`; solo navegación inerte/oculta al compactar; conservar drawer móvil, foco y preferencia. Evidencia de RED/GREEN y viewport en `sidebar-rail-task-report.md`.
- [x] Iconos del rail compacto: conservar `nav` y enlaces fuera de `inert`/`aria-hidden`; ocultar solo las etiquetas visuales y mantener sus nombres accesibles; centrar los iconos y marcar la ruta activa. Los tooltips se implementaron para hover/focus y quedan sin recorte por el rail. TDD focal RED/GREEN, suite y contrato CSS con LESS compilado/CSSOM en `sidebar-icons-task-report.md`; el build final no presenta advertencia de presupuesto. Safari confirmó rail/activo y los tres enlaces AX a 1440 px; el estado abierto del tooltip no se validó visualmente y permanece LIMITED. El budget de `angular.json` no se cambió en esta tarea; el archivo ya tenía el cambio previo de `proxyConfig`.
- [x] Ejecutar una prueba con un formulario real modificado y verificar que alternar el menú no lo reinicializa ni borra cambios sin guardar. La regresión monta `NotesPage`, modifica el control real de título y acredita la misma instancia de página, control e input, además del valor exacto y estado `dirty`, tras ocultar y reabrir. La navegación cancelada también tiene prueba automatizada.

### 3. Validar distribución e integración

- [x] Revisar `/incidencias`, `/historial`, `/mapa`, la redirección de `/deteccion` y un expediente existente en los tamaños de aceptación. En incidencias se comprobaron cabecera, buscador, lista y las cuatro fases.
- [x] Comprobar visualmente el contenedor y los controles del mapa al ocultar/reabrir sin redimensionar la ventana. Safari no expuso el canvas WebGL en la captura, pero sí los controles, atribución e inicialización; la sincronización y liberación del observador tienen pruebas unitarias.
- [x] Ejecutar `npm test -- --watch=false` y `npm run build`; registrar resultados y advertencias. La compilación y las pruebas unitarias no sustituyen la revisión en navegador.
- [x] Entregar la comparación visual realizada en sesión y persistir los resultados de la matriz en `docs/qa/objective-1/`.

## Aceptación y condiciones que deben comprobarse

| Condición                                    | Resultado esperado                                                                                                                                                                               |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Escritorio 1440 × 900 y 1920 × 1080          | Menú alineado en columna; GDS y pie ficticio ausentes de la barra; ocultar libera ancho; reapertura disponible.                                                                                  |
| Tablet 768 × 1024 y 1024 × 768               | Comportamiento correspondiente al breakpoint, orientación estable y contenido legible sin superposiciones.                                                                                       |
| Límite 767 y 768 px                         | A 767 px el panel modal usa scrim y foco contenido; a 768 px el workspace empieza después de los 280 px del sidebar y no usa scrim ni inert modal.                                               |
| Móvil 375 × 812 y 390 × 844                  | Panel inicia cerrado; controles accesibles, enlaces completos y ausencia de scroll horizontal de página. Scroll horizontal de tablas, si procede, limitado a su contenedor.                      |
| Teclado                                      | Tab y Shift+Tab recorren solo controles visibles; Enter/Espacio activan botones; Escape cierra móvil y devuelve foco. El foco tiene indicador visible.                                           |
| Persistencia                                 | La preferencia de escritorio sobrevive a navegación y recarga; visitar móvil no sobrescribe esa preferencia. Almacenamiento bloqueado o valor inválido usa un estado seguro sin romper la vista. |
| Zoom 200 %, contenido largo y ventana baja   | Sin controles tapados; menú desplazable y acceso al cierre; los títulos no invaden controles vecinos.                                                                                            |
| Movimiento reducido                          | El menú respeta la preferencia global sin animaciones imprescindibles para entender su estado.                                                                                                   |
| Formulario modificado y navegación cancelada | Ocultar/reabrir conserva campos y cambios; cancelar una salida mantiene página y foco utilizables.                                                                                               |
| Mapas al colapsar/reabrir                    | Canvas ajustado al contenedor, marcadores y controles visibles; sin recarga necesaria.                                                                                                           |
| Mapa a 768 px con sidebar abierto            | `MapPage` mide aproximadamente 440 px, apila listado y mapa y conserva el ancho útil del canvas; en escritorio ancho se mantienen las dos columnas.                                               |
| Cabecera sin telemetría ni sesión comprobada | Texto neutral; ausencia de insignias que afirmen salud, autenticación o garantías no verificadas.                                                                                                |

**Orden recomendado:** estructura → comportamiento/teclado → integración y capturas. Las incidencias detectadas fuera del layout se incorporarán solo si impiden cumplir esta aceptación.
