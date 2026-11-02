# Las cuatro fases de una incidencia

La entrada principal es `/incidencias`. Cada incidencia contiene su registro inicial de indicios, cuatro fases, referencias de evidencia y un historial de guardados y validaciones. La API autenticada guarda el proceso en PostgreSQL y aplica ownership por UUID de usuario.

| Fase de la aplicación  | Capa del PDF | Información necesaria para validar                                                                                                                                                  |
| ---------------------- | ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. Indicios tempranos  | 0            | Identificador, área o corredor, coordenadas, descripción del indicio, referencias de línea base y comparación, contexto del suelo y temporada, evidencia, responsable y fundamento. |
| 2. Reconocimiento      | 1            | Dictamen concluyente, referencias de dos sensores diferentes entre RGB, IR, GPR y magnetometría, responsable y fundamento.                                                          |
| 3. Vigilancia          | 3            | Revisión documentada del entorno, referencia de evidencia, responsable y fundamento.                                                                                                |
| 4. Mitigación y cierre | 2            | Referencias del informe especializado y TECHINT, evidencia, responsable y fundamento.                                                                                               |

La vigilancia precede a la mitigación según la página 26 del PDF. La numeración de fases 1–4 expresa el orden de trabajo; también se muestra la numeración original de capas. La condición de dos fuentes de reconocimiento es una decisión de diseño para representar la revisión multisensorial, no un umbral técnico de detección certificado.

## Guardar y validar son acciones distintas

1. Guardar el registro inicial de la incidencia habilita únicamente la fase 1.
2. Completar la información de la fase y agregar sus referencias de evidencia al borrador.
3. **Guardar borrador de fase** conserva lo capturado, incluso si está incompleto. Nunca habilita la siguiente fase.
4. **Validar fase y habilitar la siguiente** solicita a la API verificar todos los requisitos de la versión guardada. Solo una respuesta exitosa desbloquea la siguiente fase.

La secuencia visual usa cuatro puntos conectados: punto verde con ✓ para una fase validada, punto resaltado para la fase activa y punto gris para una fase bloqueada. Las fases futuras permanecen bloqueadas. Las fases validadas conservan fecha y responsable y se consultan en modo lectura. El registro inicial queda en modo lectura después de validar la fase 1 para evitar cambiar el lugar o contexto al que corresponden las validaciones.

Un reconocimiento inconcluso o una revisión de vigilancia bloqueada no pueden validarse. Un falso positivo concluyente cierra la incidencia en la fase 2, sin iniciar las fases 3 o 4. La fase 4 cierra el proceso documental; no certifica automáticamente una zona segura.

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
- `PUT /api/notes/{id}/phases/{stage}`: guarda un borrador de la fase activa con `{ expectedRevision, data }`.
- `POST /api/notes/{id}/phases/{stage}/validate`: valida exclusivamente la fase activa guardada con `{ expectedRevision }`.

Los identificadores de fase son `indications`, `reconnaissance`, `surveillance` y `mitigation`. La API rechaza con `409` los saltos, cambios a fases validadas, escrituras obsoletas y acciones sobre procesos cerrados; devuelve `422` con la lista de requisitos cuando la fase está incompleta, y `400` para datos mal formados. La comprobación de versión, vigencia de catálogos, transición, evento y auditoría se realiza en una transacción PostgreSQL con bloqueo de la incidencia propietaria y de las opciones seleccionadas.

Al validar, `phase_validations.snapshot` guarda los datos de la fase junto con el código, etiqueta y versión de cada opción utilizada. El DTO expone esta información como `catalogSnapshot`; las pantallas de solo lectura muestran la etiqueta congelada en la validación y no reinterpretan el registro con una etiqueta renombrada posteriormente.

El analizador de incidencias heredadas conserva identificador, descripción, coordenadas y original antes de cualquier importación aprobada. Se mantiene el nombre JSON histórico `altitude` en ese contrato; su significado y etiqueta visible son **latitud**.

Una validación importada con `origin = legacy` conserva el nombre y la fecha históricos, pero deja `validated_by_user_id = NULL`. Ese registro indica procedencia documental y no equivale a una firma autenticada ni atribuye la acción a una cuenta actual.

## Alcance

Son validaciones documentales de integridad y secuencia. La información de sensores se registra mediante referencias; no hay procesamiento de LiDAR, IA ni control de equipos. La identidad proviene de la sesión autenticada y PostgreSQL aplica ownership por UUID. El nombre de responsable capturado en el contenido sigue siendo un dato documental; no reemplaza la identidad que autoriza y audita la operación.

## Verificación

```bash
npm run build
npm test -- --watch=false
dotnet build backend
python3 backend-tests/test_note_phases.py
```

Las pruebas de la API levantan su propio servidor con puerto y archivo temporales, sin alterar las incidencias de trabajo. Cubren las cuatro fases, saltos por peticiones directas, borradores, requisitos, falsos positivos, concurrencia, edición de fases validadas y compatibilidad con incidencias anteriores.
