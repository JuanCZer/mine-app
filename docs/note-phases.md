# Las cuatro fases de una incidencia

La entrada principal es `/incidencias`. Cada incidencia contiene su registro inicial de indicios, cuatro fases, referencias de evidencia y un historial de guardados y validaciones. La API autenticada guarda el proceso en PostgreSQL y aplica ownership por UUID de usuario.

| Fase de la aplicación  | Capa del PDF | Información necesaria para validar                                                                                                                                                  |
| ---------------------- | ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. Indicios tempranos  | 0            | Identificador, área o corredor, coordenadas, descripción del indicio, referencias de línea base y comparación, contexto del suelo y temporada, evidencia, responsable y fundamento. |
| 2. Reconocimiento      | 1            | Dictamen concluyente, referencias de dos sensores diferentes entre RGB, IR, GPR y magnetometría, responsable y fundamento.                                                          |
| 3. Vigilancia          | 3            | Revisión documentada del entorno, referencia de evidencia, responsable y fundamento.                                                                                                |
| 4. Mitigación y cierre | 2            | Referencias del informe especializado y TECHINT, evidencia, responsable y fundamento.                                                                                               |

La vigilancia precede a la mitigación según la página 26 del PDF. La numeración de fases 1–4 expresa el orden de validación; la captura de borradores permite saltar entre fases y también se muestra la numeración original de capas. La condición de dos fuentes de reconocimiento es una decisión de diseño para representar la revisión multisensorial, no un umbral técnico de detección certificado.

## Guardar y validar son acciones distintas

1. Guardar el registro inicial permite entrar a cualquiera de las cuatro fases.
2. Capturar información y referencias de evidencia en cualquier orden. Los requisitos pendientes se muestran como avisos y no impiden cambiar de fase.
3. **Guardar borrador de fase** conserva lo capturado en el servidor, incluso si está incompleto. Los valores deben tener formato válido. Guardar no valida.
4. **Validar fase** solicita a la API verificar todos los requisitos de la versión guardada. La validación formal mantiene el orden de las cuatro fases y es independiente de la navegación.

La secuencia visual usa cuatro puntos conectados: ✓ para una fase validada y punto resaltado para la fase seleccionada. Cada fase sin validar muestra el número de requisitos pendientes o que su información está completa. Las fases validadas conservan fecha y responsable y se consultan en modo lectura. El registro inicial queda en modo lectura después de validar la fase 1 para evitar cambiar el lugar o contexto al que corresponden las validaciones.

Cambiar de fase conserva los campos y las evidencias a medio escribir en memoria, incluso si un valor todavía no tiene formato válido. No es un guardado automático: hay que guardar cada borrador para conservarlo al salir o recargar. La protección de cambios sin guardar considera todas las fases visitadas. Antes de validar, se deben guardar también los cambios pendientes de las otras fases para evitar perderlos si la validación cierra la incidencia.

Un reconocimiento inconcluso o una revisión de vigilancia bloqueada no pueden validarse. Un falso positivo concluyente cierra la incidencia en la fase 2: los borradores que ya existan en las fases 3 o 4 quedan disponibles en modo de consulta. La fase 4 cierra el proceso documental; no certifica automáticamente una zona segura.

## Componentes

- `NotesPage`: registro de incidencias, selección, búsqueda, progreso y protección de cambios sin guardar.
- `NoteWorkflowPanel`: navegación por las cuatro fases y comunicación con la API para guardar y validar.
- `NotePhaseEditor`: campos específicos de la fase, borrador, evidencias y lista de requisitos pendientes.
- `EvidencePanel`: referencias de evidencia; se pueden retirar del borrador antes de validar.
- `CaseTimeline`: historial persistido de guardados y validaciones, con el responsable declarado.

El módulo anterior `/deteccion` fue retirado porque su almacén compartido no podía aislar cuentas. Sus rutas redirigen a `/incidencias`; la clave local anterior se conserva intacta únicamente para exportación y conciliación controladas, y la aplicación no la lee ni la modifica. La ruta histórica `/notas` también redirige a `/incidencias`.

La ruta histórica `/record` redirige a `/historial`.

## Contrato y garantías de la API

- `GET /api/notes`: incidencias con `workflow` y `revision`.
- `POST /api/notes`: crea una incidencia con las cuatro fases pendientes; no acepta un estado de validación elegido por el cliente.
- `PUT /api/notes/{id}`: actualiza el registro inicial de indicios antes de la primera validación; exige `expectedRevision`.
- `PUT /api/notes/{id}/phases/{stage}`: guarda un borrador de cualquier fase sin validar de una incidencia abierta con `{ expectedRevision, data }`.
- `POST /api/notes/{id}/phases/{stage}/validate`: valida exclusivamente la fase activa guardada con `{ expectedRevision }`.

Los identificadores de fase son `indications`, `reconnaissance`, `surveillance` y `mitigation`. `currentStage` sigue indicando la primera fase pendiente de validación; no restringe la navegación ni el guardado. La API rechaza con `409` las validaciones fuera de orden, cambios a fases validadas, escrituras obsoletas y acciones sobre procesos cerrados; devuelve `422` con la lista de requisitos al intentar validar una fase incompleta, y `400` para datos mal formados. La comprobación de versión, vigencia de catálogos, transición, evento y auditoría se realiza en una transacción PostgreSQL con bloqueo de la incidencia propietaria y de las opciones seleccionadas.

Al validar, `phase_validations.snapshot` guarda los datos de la fase junto con el código, etiqueta y versión de cada opción utilizada. El DTO expone esta información como `catalogSnapshot`; las pantallas de solo lectura muestran la etiqueta congelada en la validación y no reinterpretan el registro con una etiqueta renombrada posteriormente.

El analizador de incidencias heredadas conserva identificador, descripción, coordenadas y original antes de cualquier importación aprobada. Se mantiene el nombre JSON histórico `altitude` en ese contrato; su significado y etiqueta visible son **latitud**.

Una validación importada con `origin = legacy` conserva el nombre y la fecha históricos, pero deja `validated_by_user_id = NULL`. Ese registro indica procedencia documental y no equivale a una firma autenticada ni atribuye la acción a una cuenta actual.

## Alcance

Son validaciones documentales de integridad y secuencia. La información de sensores se registra mediante referencias; no hay procesamiento de LiDAR, IA ni control de equipos. La identidad proviene de la sesión autenticada y PostgreSQL aplica ownership por UUID. El nombre de responsable capturado en el contenido sigue siendo un dato documental; no reemplaza la identidad que autoriza y audita la operación.

## Verificación

```bash
npm run build
npm test -- --watch=false
dotnet build server/MineApp.sln
python3 -m unittest discover -s server/tests -p test_note_phases.py
```

Las pruebas de la API levantan su propio servidor con puerto y archivo temporales, sin alterar las incidencias de trabajo. Cubren borradores parciales fuera de orden, validación secuencial, requisitos, falsos positivos, concurrencia, edición de fases validadas y compatibilidad con incidencias anteriores. Las pruebas del panel cubren navegación directa, conservación de campos y evidencias sin guardar, revisión vigente al guardar y consulta de incidencias descartadas. Este cambio no requiere migración SQL.
