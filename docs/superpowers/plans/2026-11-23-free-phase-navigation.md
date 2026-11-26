# Plan: navegación libre entre fases

## Corrección del 25 de noviembre: borradores nuevos

La captura del dominio muestra el código actualizado, cero incidencias y botones con «Registra la incidencia». La causa es el camino separado de creación en `notes.html`, que entrega `note=null` al panel y solo muestra un editor de fase 1. La primera implementación no cubrió este recorrido.

- [x] Reproducir con una prueba de página completa: lista vacía, datos generales vacíos y clic en fase 2.
- [x] Unificar la pantalla nueva/guardada en `NoteWorkflowPanel`, manteniendo captura libre y guardado explícito de datos generales. Trabajar directamente en `main`, conforme a la preferencia del usuario.
- [x] Preservar fase seleccionada, campos y evidencias al crear; impedir peticiones de fases con ID provisional; reiniciar los borradores al cambiar/descartar/recargar.
- [x] Ejecutar pruebas de regresión, compilación y revisión final. Actualizar documentación de uso.

La especificación se amplía para cubrir navegación antes de crear la incidencia. Se mantienen los requisitos de la API al guardar y validar; no se requiere cambio del servidor ni migración SQL.

Verificación de esta corrección:

- Las cuatro pruebas nuevas de página fallaron inicialmente porque los círculos estaban deshabilitados con cero incidencias. Tras unificar el panel, permiten navegar desde el estado vacío, conservan campos y evidencia al registrar, conservan datos tras fallo HTTP y reinician borradores al descartar/cambiar de incidencia.
- La suite completa detectó que los datos generales no se proyectaban a través del panel. Se agregó una prueba DOM y se corrigió la proyección con `ngProjectAs`; la prueba ahora pulsa el botón real **Guardar incidencia**. La guía oficial utilizada fue [proyección de contenido de Angular](https://angular.dev/guide/components/content-projection).
- `npm test -- --watch=false`: 154/154 pruebas, 21 archivos, sin fallos. `npm run build:clean`: compilación de producción correcta, sin advertencias. `git diff --check`: sin problemas.
- Revisión independiente: detectó dos problemas y se corrigieron con regresiones RED→GREEN. (1) Las fases editadas antes de registrar conservaban suelo/temporada vacíos, rechazados por `CheckDraft`; se alinearon los valores iniciales con `unknown` de la API y se comprobó el payload enviado. (2) Seleccionar solo la fuente de evidencia no marcaba cambios; ahora la fuente participa en el estado pendiente y se conserva tras crear.
- Verificación final después de esas correcciones: **155/155 pruebas Angular**, **34/34 pruebas HTTP de fases**, build .NET sin errores/advertencias y **build de producción correcto**. Las pruebas HTTP usan el proveedor JSON aislado. No se necesita una migración.
- Alcance confirmado durante la revisión: el registro inicial no guarda automáticamente todas las fases; los borradores son locales hasta guardarlos explícitamente. Persistencia tras terminar el navegador y recuperación idempotente ante una respuesta de creación perdida siguen fuera de este cambio.
- Trabajo local en `main`. No se cambió servidor, contrato ni SQL; no se ha publicado esta corrección al dominio.


**Objetivo:** permitir cambiar de fase y capturar borradores sin completar o validar fases anteriores, manteniendo los avisos de pendientes.

**Arquitectura:** separar edición y validación en la API; mantener editores visitados en Angular para conservar cambios locales. Sin migración de datos.

**Tecnologías:** Angular, formularios reactivos y señales; ASP.NET Core; PostgreSQL y proveedor JSON de pruebas; Vitest y unittest HTTP.

**Diseño:** [Comportamiento y alcance](../specs/2026-11-23-free-phase-navigation-design.md).

## Restricciones y revisión

No eliminar requisitos de validación, permisos, control de revisión ni protección de fases validadas. No perder cambios de otras fases al guardar una. Distinguir navegación, guardado y validación en todos los mensajes. Trabajar en la rama local `codex/free-phase-navigation`, dejando cambios revisables en el espacio compartido; no publicar ni desplegar.

## Tarea 1: separar guardado y validación

- [x] Cambiar la prueba HTTP que rechaza borradores futuros por casos de guardado parcial fuera de orden y leerlos de nuevo; verificar que validar fuera de orden siga rechazado.
- [x] Ejecutar la prueba y observar el rechazo actual (409).
- [x] Añadir `WorkflowRules.EditablePhase`; conservar `ActivePhase` para validación ordenada. Usar edición en `NoteStore.SavePhase` y `PostgresNoteRepository.SavePhase`.
- [x] Actualizar mensajes de historial de ambos proveedores.
- [x] Ejecutar `python3 -m unittest discover -s server/tests -p 'test_note_phases.py' -v`. Esperado: guardado libre y controles de validación/revisión en verde.

**Interfaz:** `PUT /api/notes/{id}/phases/{stage}` acepta borradores parciales de cualquier fase no validada de una incidencia abierta. El contrato JSON y la semántica de `currentStage` (primera pendiente de validación) no cambian.

## Tarea 2: navegación, conservación de borradores y avisos

- [x] Añadir pruebas de componente para saltos directos, formularios y evidencias sin guardar, avisos, guardado parcial y estado agregado de cambios.
- [x] Ejecutar las pruebas antes del cambio; esperado: navegación bloqueada.
- [x] Actualizar `note-workflow.ts/html` para consultar cualquier fase y mantener editores visitados; agregar pendientes por fase y estado de cambios de todos los editores.
- [x] Ajustar `note-phase-editor.ts/html` para explicar la validación formal y mostrar su motivo de bloqueo sin bloquear edición o navegación.
- [x] Corregir mensajes de `notes.ts` y documentación vigente que describan fases bloqueadas.
- [x] Ejecutar pruebas enfocadas, luego `npm test -- --watch=false`. Esperado: campos y evidencias conservados, guardados usando la revisión vigente, fases validadas/cerradas solo lectura.

**Interfaz:** el panel entrega la fase y motivo de bloqueo exclusivo de validación al editor; los eventos de guardado/validación conservan su contrato.

## Tarea 3: verificación y revisión final

- [x] Ejecutar build de producción `npm run build:clean` y pruebas de campos por fase.
- [x] Revisar el diff completo, especialmente cambios en fases ocultas, recarga explícita, cambio de incidencia y cierre por falso positivo.
- [x] Solicitar una revisión independiente del cambio completo y corregir problemas importantes con pruebas.
- [x] Registrar resultados y limitaciones aquí; entregar el plan y cambio implementado.

## Registro de ejecución

- Diagnóstico: el bloqueo existe en los botones del panel, `editable`, el guardado con cambios pendientes y `WorkflowRules.ActivePhase`. La base de datos separa mutación de borrador y orden de validación.
- Preflight: las tareas comparten la semántica de `currentStage`; se conserva como primera fase pendiente de validación, sin usarla para bloquear borradores.
- Decisión: trabajar en una rama local dentro del espacio compartido para entregar los cambios donde el usuario está trabajando; no mover ni duplicar el proyecto.

- Tarea 1 completada: 4 casos HTTP fallaron primero con 409 en los guardados futuros; después del cambio, 34/34 pruebas de fases pasaron (`python3 -m unittest discover -s server/tests -p 'test_note_phases.py' -v`). El build .NET pasó con 0 errores y 0 advertencias.
- Tarea 2 completada: las 6 pruebas nuevas de navegación fallaron primero con botones deshabilitados. Después del cambio, 150/150 pruebas Angular pasaron (`npm test -- --watch=false`). Incluyen evidencia sin terminar, campos fuera de rango, guardado parcial, rechazo por revisión obsoleta, recarga explícita y consulta de borradores tras descarte.
- Tarea 3: `npm run build:clean` pasó sin advertencias de presupuesto; 2/2 pruebas de mapeo de campos pasaron. `git diff --check` no reportó problemas.
- Límite de verificación: las pruebas HTTP usan el proveedor JSON aislado. Se revisó el código de PostgreSQL y sus guardas SQL, pero no se ejecutó una integración con PostgreSQL real: este entorno no dispone de Docker ni psql. No se realizó despliegue al dominio.

- Revisión independiente final: sin defectos críticos, importantes o menores. Se comprobaron navegación, datos generales proyectados, borradores ocultos, revisiones, recarga/cambio de incidencia, cierre y guardas PostgreSQL. El revisor no repitió pruebas; inspeccionó el diff y sus contratos.
- Confirmación de alcance: se conservan los campos mínimos del registro inicial y el orden de validación formal; la solicitud elimina el requisito de completar/validar para navegar y capturar, sin redefinir el cierre documental. No se necesita una decisión adicional para implementar este alcance.
- Mejora opcional de pruebas para trabajo posterior: ejercicio explícito de validación exitosa con siguiente fase ya visitada y bloqueo por borrador oculto. No se identificó un defecto asociado; ambos comportamientos están implementados.
- Entrega: implementación y documentación terminadas en la rama local indicada, sin commit, push ni despliegue. Integración real con PostgreSQL y publicación quedan fuera de la verificación local ejecutada.
