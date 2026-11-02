# Matriz de campos y catálogos

Esta matriz define qué valores obtiene la interfaz desde PostgreSQL y qué datos permanecen como texto, hechos registrados o reglas del flujo. Los códigos y etiquetas canónicas están en [Catálogos canónicos](catalogs.md).

## Incidencias y cuatro fases

| Superficie / campo | Propiedad enviada | Fase | Tratamiento | `catalogKey` | Conservación histórica |
| --- | --- | --- | --- | --- | --- |
| Tipo de suelo | `data.soil` | Todas | Código de catálogo | `soils` | La opción inactiva guardada se muestra como histórica y puede conservarse. |
| Temporada | `data.season` | Todas | Código de catálogo | `seasons` | Igual que suelo. |
| Fuente de evidencia | `data.evidence[].sensor` | Todas, filtrada por fase | Código de catálogo | `sensors` | Se conserva el sensor inactivo ya asociado; uno nuevo debe estar activo y permitido en la fase. |
| Plataforma I&W | `data.indications.platform` | 1 · Indicios | Código de catálogo con conducta protegida | `platforms` | Solo se conserva el código inactivo previo. La regla que exige `if1200a` sigue en código. |
| Fuente del levantamiento | `data.indications.collectionSource` | 1 · Indicios | Código de catálogo | `indicationSources` | Se conserva una selección previa inactiva. |
| Firmas de alteración | `data.indications.signatures[]` | 1 · Indicios | Códigos únicos de catálogo | `indicationSignatures` | Las firmas inactivas previas permanecen visibles; `surface-removal` conserva su regla de profundidad. |
| Probabilidad | `data.reconnaissance.probability` | 2 · Reconocimiento | Código de catálogo | `riskLevels` | Se conserva el código inactivo previo. |
| Prioridad | `data.reconnaissance.priority` | 2 · Reconocimiento | Código de catálogo | `riskLevels` | Igual que probabilidad. |
| Dictamen | `data.review.finding` | Revisión | Código con conducta protegida | `findings` | Se conserva el dictamen histórico; `confirmed` y `false-positive` siguen gobernando transiciones en código. |
| Tecnologías móviles | `data.surveillance.radioTechnologies[]` | 3 · Vigilancia | Códigos únicos de catálogo | `radioTechnologies` | Se conservan selecciones inactivas previas. |
| Métodos de vigilancia | `data.surveillance.methods[]` | 3 · Vigilancia | Códigos únicos con conducta protegida | `surveillanceMethods` | Se conservan selecciones previas; los métodos mínimos siguen definidos por la regla de fase. |
| Despliegue | `data.surveillance.deployment` | 3 · Vigilancia | Código con conducta protegida | `surveillanceDeployments` | Se conserva el código previo; las reglas de canales para UAV/combinado siguen en código. |
| Actividad humana | `data.surveillance.humanActivity` | 3 · Vigilancia | Código de catálogo | `humanActivities` | Se conserva el código inactivo previo. |
| Decisión del entorno | `data.surveillance.environmentDecision` | 3 · Vigilancia | Código con conducta protegida | `environmentDecisions` | `clear-for-mitigation` continúa siendo requisito de avance. |
| Plataforma de limpieza | `data.mitigation.platform` | 4 · Mitigación | Código con conducta protegida | `platforms` | Solo se conserva el código previo; la regla Husky sigue en código. |
| Nivel de protección | `data.mitigation.protectionLevel` | 4 · Mitigación | Código de catálogo | `protectionLevels` | Se conserva el código inactivo previo. |
| Resultado de neutralización | `data.mitigation.neutralizationResult` | 4 · Mitigación | Código con conducta protegida | `neutralizationResults` | `neutralized` continúa siendo requisito de cierre. |
| Resultado de ruta | `data.mitigation.routeResult` | 4 · Mitigación | Código de catálogo | `routeResults` | Se conserva el código inactivo previo. |

El backend valida pertenencia, vigencia y alcance de fase mediante `CatalogService`. Longitudes, rangos, unicidad y requisitos de avance permanecen en `WorkflowRules`. Guardar un borrador no cambia la fase; validar conserva la secuencia `indications → reconnaissance → surveillance → mitigation`.

## Trazabilidad integral PDF → UI → DTO → SQL

La revisión se realizó sobre las 38 páginas del PDF y mediante inspección visual de las páginas 7–14, 18–27 y 30–35. La clasificación evita convertir una especificación comercial o una explicación doctrinal en un dato que el operador deba inventar:

- **Explícito:** el PDF describe el dato o proceso y la fase lo registra o confirma.
- **Operativo:** fecha, folio, responsable o referencia que la aplicación necesita para trazabilidad, aunque el PDF no prescriba su forma.
- **Contexto:** capacidad fija mostrada como referencia en la interfaz; no se vuelve un campo editable por incidencia.

Un borrador admite valores parciales. La columna «Validación» describe lo que se exige únicamente al avanzar de fase. Los datos heredados conservan el valor original y los códigos desconocidos pasan a conciliación; no se reemplazan silenciosamente.

### Registro inicial y campos compartidos

| PDF | Campo de UI | Propiedad DTO | Tabla / columna SQL | Tipo / catálogo | Validación y origen |
| --- | --- | --- | --- | --- | --- |
| 7–10 | Identificador de alerta I&W | `Note.title` | `incidents.title` | texto ≤120 | Operativo; obligatorio para crear. |
| 7–10 | Descripción del indicio | `Note.content` | `incidents.content` | texto ≤10000 | Explícito; obligatorio para crear. |
| 7–10, 32–35 | Área o corredor de interés | `Note.state` | `incidents.area_of_interest` | texto ≤100 | Explícito; no se confunde con entidad federativa. |
| 10 | Latitud de la anomalía | `Note.altitude` | `incidents.latitude` | grados, −90…90 | Explícito; `altitude` es el nombre JSON legado de latitud. |
| 10 | Longitud de la anomalía | `Note.longitude` | `incidents.longitude` | grados, −180…180 | Explícito. |
| 32–35 | Tipo de suelo | `data.soil` | `incident_phases.soil_code` | catálogo `soils` | Explícito como contexto transversal; obligatorio al guardar. |
| 32–35 | Temporada | `data.season` | `incident_phases.season_code` | catálogo `seasons` | Explícito; el PDF destaca temporada seca o todo el año según suelo. |
| 10, 32–35 | Contexto del terreno | `data.terrainNotes` | `incident_phases.terrain_notes` | texto ≤2000 | Explícito; requerido donde la regla de fase lo utiliza. |
| 10 | Línea base | `data.review.baseline` | `phase_reviews.baseline` | texto ≤500 | Explícito y obligatorio en Fase 1. |
| 10 | Comparación subsecuente | `data.review.comparison` | `phase_reviews.comparison` | texto ≤500 | Explícito y obligatorio en Fase 1. |
| 11–13 | Dictamen | `data.review.finding` | `phase_reviews.finding_code` | catálogo `findings` | Explícito; Fase 2 exige `confirmed` o `false-positive`. |
| 25–26 | Revisión del entorno humano | `data.review.environment` | `phase_reviews.environment_code` | catálogo `environmentReviews` | Explícito; complementa la decisión de Fase 3. |
| 19 | Informe especializado | `data.review.report` | `phase_reviews.report` | texto ≤500 | Operativo; referencia EOD. |
| 19 | Informe TECHINT | `data.review.techint` | `phase_reviews.techint` | texto ≤500 | Operativo; referencia posterior a neutralización. |
| — | Responsable declarado | `data.review.reviewer` | `phase_reviews.reviewer_display_name` | texto ≤200 | Operativo; no reemplaza al actor autenticado. |
| — | Fundamento / notas | `data.review.notes` | `phase_reviews.notes` | texto ≤5000 | Operativo. |
| 9–31 | Fuente de evidencia | `data.evidence[].sensor` | `phase_evidence.sensor_code` | catálogo `sensors` | Explícito; el conjunto requerido depende de la fase. |
| — | Referencia y resumen | `reference`, `summary` | `phase_evidence.reference`, `summary` | texto | Operativo; documenta el producto, no almacena el archivo binario. |
| — | Fecha original de evidencia | `recordedAt` | `recorded_at`, `recorded_at_original` | instante + texto original | Operativo; una fecha ambigua conserva su representación. |

### Fase 1 · Capa Cero · I&W e indicios tempranos

| PDF | Campo de UI | Propiedad DTO | Tabla / columna SQL | Tipo / catálogo | Validación y origen |
| --- | --- | --- | --- | --- | --- |
| 9 | Plataforma IF1200A | `indications.platform` | `phase_indications.platform_code` | `platforms/if1200a` | Explícito y obligatorio. |
| 9 | Cumplimiento Blue UAS / NDAA | `blueUasNdaaCompliant` | `blue_uas_ndaa_compliant` | booleano | Explícito; debe confirmarse. |
| 9 | Duración de vuelo | `flightDurationMin` | `flight_duration_min` | minutos, >0…43 | Especificación de capacidad usada como límite superior. |
| 9 | Captura LiDAR | `lidarPointRate` | `lidar_point_rate` | puntos/s, ≥640000 | Especificación explícita del sistema. |
| 9 | Carga útil | `payloadLb` | `payload_lb` | lb, >0…19.1 | Especificación de capacidad. |
| 9 | Velocidad de misión | `missionSpeedMph` | `mission_speed_mph` | mph, >0…56 | Especificación de capacidad. |
| 9 | Resistencia al viento | `windKnots` | `wind_knots` | nudos, 0…23 | Especificación de capacidad. |
| 9–10 | Referencia del modelo 3D / nube de puntos | `terrainModelReference` | `terrain_model_reference` | texto ≤500 | Explícito y obligatorio. |
| 10 | Algoritmo diferencial ejecutado | `differentialAnalysisCompleted` | `differential_analysis_completed` | booleano | Explícito y obligatorio. |
| 9–10 | Variación milimétrica | `surfaceVariationMm` | `surface_variation_mm` | milímetros >0 | Explícito; el PDF no fija un máximo operativo. |
| 7–10 | Alerta procesable I&W | `actionableAlertGenerated` | `actionable_alert_generated` | booleano | Explícito y obligatorio. |
| 7–11 | Personal no expuesto | `personnelNotExposed` | `personnel_not_exposed` | booleano | Explícito y obligatorio. |
| 10 | Coordenada transferida a VELES | `coordinateTransferredToVeles` | `coordinate_transferred_to_veles` | booleano | Explícito y obligatorio para continuar. |
| 10 | Fecha/hora del levantamiento comparativo | `observedAt` | `observed_at`, `observed_at_original` | instante + original | Operativo; requerido para trazabilidad. |
| 9–10 | Fuente LiDAR / LiDAR+RGB+IR | `collectionSource` | `collection_source_code` | catálogo `indicationSources` | Explícito; debe seleccionarse. |
| 10 | Firmas de remoción, borde, vegetación y densidad | `signatures[]` | `phase_indication_signatures.signature_code` | catálogo múltiple | Explícito; requiere al menos una. |
| 10 | Causas naturales/civiles consideradas | `alternativeCauses` | `alternative_causes` | texto ≤2000 | Explícito por la exigencia de distinguir causas naturales; obligatorio. |
| 10 | Profundidad de depresión | `depressionDepthCm` | `depression_depth_cm` | cm, 5…15 | Explícito cuando se selecciona `surface-removal`. |
| 9–10 | Producto LiDAR | `evidence[]` con `LiDAR` | `phase_evidence` | evidencia | Explícito y obligatorio. RGB/IR son complementarios en esta fase. |

### Fase 2 · Capa Uno · Reconocimiento VELES

| PDF | Campo de UI | Propiedad DTO | Tabla / columna SQL | Tipo / catálogo | Validación y origen |
| --- | --- | --- | --- | --- | --- |
| 10, 12, 14 | Lectura magnetométrica | `reconnaissance.magneticReadingNt` | `phase_reconnaissance.magnetic_reading_nt` | nT, ≥10 | Explícito. |
| 12, 14 | Profundidad GPR | `gprDepthCm` | `gpr_depth_cm` | cm, >0…30 | Explícito. |
| 14 | Precisión RTK | `rtkAccuracyCm` | `rtk_accuracy_cm` | cm, 1…3 | Explícito. |
| 11, 14 | Precisión de localización | `localizationAccuracyCm` | `localization_accuracy_cm` | cm, >0…5 | Explícito. |
| 13 | Probabilidad | `probability` | `probability_code` | catálogo `riskLevels` | Explícito y obligatorio. |
| 13 | Prioridad | `priority` | `priority_code` | catálogo `riskLevels` | Explícito y obligatorio. |
| 11, 13 | Punto georreferenciado | `georeferencedPointReference` | `georeferenced_point_reference` | texto ≤500 | Explícito y obligatorio. |
| 13 | Mapa de riesgo | `riskMapReference` | `risk_map_reference` | texto ≤500 | Explícito y obligatorio. |
| 13–14 | Clasificación IA | `aiClassificationReference` | `ai_classification_reference` | texto ≤500 | Explícito y obligatorio. |
| 11–14 | Fusión GPR, magnetometría, RGB e IR | `fusionCompleted` | `fusion_completed` | booleano | Explícito; debe confirmarse. |
| 11–14 | Evidencias VELES | `evidence[]` | `phase_evidence` | GPR, Magnetometría, RGB, IR | Las cuatro referencias son obligatorias; no basta una señal aislada. |

VELES es contexto fijo de la fase y se muestra en la interfaz. No se agregó un selector que pudiera afirmar otra plataforma. Las especificaciones de página 14 —DJI Matrice 400 en integración, RGB 0.2 cm/px, IR 0.5 cm/px, cobertura 100,000 m²/día, 59 minutos, 6 kg, 25 m/s, tamaños de datasets y catálogo de 461 objetos— permanecen visibles en el panel de contexto; son capacidades declaradas, no resultados que el operador deba volver a capturar.

### Fase 3 · Capa Tres · Vigilancia previa a mitigación

| PDF | Campo de UI | Propiedad DTO | Tabla / columna SQL | Tipo / catálogo | Validación y origen |
| --- | --- | --- | --- | --- | --- |
| 25–26 | Inicio y cierre de observación | `observationStartedAt`, `observationEndedAt` | `observation_started_at*`, `observation_ended_at*` | instante + original | Operativo; ambos requeridos y el cierre no precede al inicio. |
| 31 | Tecnologías cubiertas | `radioTechnologies[]` | `phase_radio_technologies.technology_code` | `radioTechnologies` | Explícito: 2G/3G/4G/5G; al menos una. |
| 26–31 | Capacidades ejecutadas | `methods[]` | `phase_surveillance_methods.method_code` | `surveillanceMethods` | Explícito; 14 procesos de páginas 27, 30 y 31. |
| 26–30 | Despliegue | `deployment` | `phase_surveillance.deployment_code` | UAV, mochila terrestre o combinado | Explícito; obligatorio. |
| 30 | Canales simultáneos | `coveredChannels` | `covered_channels` | entero | UAV exige ≥2; una configuración duplicada exige ≥4. |
| 30 | Configuración duplicada | `duplicatedChannelConfiguration` | `duplicated_channel_configuration` | booleano | Explícito. |
| 25–26 | Dispositivos activos | `activeDeviceCount` | `active_device_count` | entero ≥0 | Explícito como resultado de captura; cero es válido. |
| 25–26 | Actividad humana | `humanActivity` | `human_activity_code` | `humanActivities` | Explícito; clasifica presencia, movimiento o riesgo. |
| 25–26 | Decisión sobre entorno humano | `environmentDecision` | `environment_decision_code` | `environmentDecisions` | Explícito; solo `clear-for-mitigation` habilita Fase 4. |
| 26 | Evidencias IMSI/IMEI/TMSI y EO/IR | `evidence[]` | `phase_evidence` | sensores de Fase 3 | Ambas referencias son obligatorias. |

Los 14 procesos seleccionables son: captura IMSI/IMEI/TMSI, importar/exportar objetivos, mapeo preciso, llamada silenciosa con DF, transmisión SMS masiva, alerta IMSI/IMEI, acceso desde centro de operaciones, bloqueo selectivo, mapeo en línea/fuera de línea, feed UAV, GSMA TAC, API de objetivo, refinamiento terrestre en tiempo real y configuración de pilas 2G/3G/4G/5G. Los formatos generales móvil, vehicular, UAV y especiales de la página 31 son capacidades del proveedor; el flujo de esta arquitectura registra los despliegues descritos en la página 30 sin inventar que todos se usaron.

### Fase 4 · Capa Dos · Mitigación, EOD y TECHINT

| PDF | Campo de UI | Propiedad DTO | Tabla / columna SQL | Tipo / catálogo | Validación y origen |
| --- | --- | --- | --- | --- | --- |
| 19 | Coordenada exacta recibida | `mitigation.coordinateReceived` | `phase_mitigation.coordinate_received` | booleano | Explícito; obligatorio. |
| 20–24 | Plataforma Husky | `platform` | `platform_code` | `platforms/husky-route-clearance` | Explícito; obligatorio. |
| 22 | Nivel STANAG 4569 | `protectionLevel` | `protection_level_code` | nivel 4 o 5 | Explícito; se registra el nivel utilizado. |
| 21–23 | Protección de personal | `personnelProtectionConfirmed` | `personnel_protection_confirmed` | booleano | Explícito; obligatorio. |
| 21–23 | Protección de equipo | `equipmentProtectionConfirmed` | `equipment_protection_confirmed` | booleano | Explícito; obligatorio. |
| 24 | Medios de detección | `detectionAssets` | `detection_assets` | entero ≥2 | Explícito. |
| 24 | Medios de interrogación | `interrogationAssets` | `interrogation_assets` | entero ≥1 | Explícito. |
| 24 | Traslape GPR | `gprCoverageOverlap` | `gpr_coverage_overlap` | booleano | Explícito; obligatorio. |
| 24 | Margen de seguridad | `safetyMarginM` | `safety_margin_m` | metros >0 | Explícito como proceso; el PDF no fija una distancia numérica. |
| 19, 24 | Interrogación remota completada | `remoteInterrogationCompleted` | `remote_interrogation_completed` | booleano | Explícito; obligatorio antes de contacto físico. |
| 19 | Tipo de artefacto | `artifactType` | `artifact_type` | texto ≤500 | Explícito y obligatorio. |
| 19 | Orientación | `orientation` | `orientation` | texto ≤500 | Explícito y obligatorio. |
| 19 | Mecanismo de activación | `activationMechanism` | `activation_mechanism` | texto ≤500 | Explícito y obligatorio. |
| 19 | Entrega al operador EOD | `eodHandoffReference` | `eod_handoff_reference` | texto ≤500 | Explícito; referencia operativa obligatoria. |
| 18–19 | Neutralización controlada | `neutralizationResult` | `neutralization_result_code` | `neutralizationResults` | Debe quedar `neutralized` para cerrar. |
| 18 | Apertura de ruta segura | `routeResult` | `route_result_code` | `routeResults` | Explícito; obligatorio. |
| 19 | Composición TECHINT | `techintComposition` | `techint_composition` | texto ≤2000 | Explícito y obligatorio. |
| 19 | Manufactura TECHINT | `techintManufacture` | `techint_manufacture` | texto ≤2000 | Explícito y obligatorio. |
| 19 | Firma TECHINT | `techintSignature` | `techint_signature` | texto ≤2000 | Explícito y obligatorio. |
| 19 | Retroalimentación al catálogo IA VELES | `catalogFeedbackReference` | `catalog_feedback_reference` | texto ≤500 | Explícito; referencia obligatoria. |
| 18–24 | Evidencias GPR, EOD y TECHINT | `evidence[]` | `phase_evidence` | sensores de Fase 4 | Las tres referencias son obligatorias. |

### Decisiones y discrepancias resueltas

1. El orden operativo es Capa 0 → Capa 1 → Capa 3 → Capa 2. La página 26 declara que vigilancia ocurre antes de mitigación; por eso las fases de la aplicación son 1, 2, 3, 4 aunque los números de capa sean 0, 1, 3, 2.
2. El nombre JSON `altitude` se conserva solo por compatibilidad; UI y SQL lo tratan como latitud.
3. La página 27 enumera transmisión de mensajes SMS masivos. La auditoría encontró que faltaba en las semillas; se agregó `mass-sms-transmission` como proceso 5 y se renumeró únicamente `sort_order`, sin cambiar los demás códigos.
4. Los valores de capacidad de IF1200A y VELES se muestran como contexto y, cuando representan un límite de una captura reportada, validan la unidad/rango correspondiente. No se crean campos para obligar al operador a copiar especificaciones fijas como tamaños de datasets o historia de despliegue.
5. El PDF describe margen/buffer de seguridad, pero no entrega una distancia universal. La aplicación exige registrar un valor positivo y no inventa un mínimo doctrinal.
6. Municipios, porcentajes edáficos y sistema lead de páginas 33–35 permanecen como contexto de planeación. El expediente registra corredor, suelo, temporada y notas del terreno; no deduce automáticamente un sistema a partir del municipio.

## Archivo local de detección retirado

La clave histórica `gds-detection-cases-v1` se conserva byte por byte para recuperación, pero ya no se presenta ni se modifica desde la aplicación. Sus campos de catálogo, revisiones, evidencias e historial permanecen dentro del original protegido durante el análisis. Una correspondencia de propietario aprobada puede preparar la creación de una incidencia normal con cuatro fases pendientes; el estado local nunca se convierte en una validación autenticada.

## Datos que no son catálogos

- Identificador, área o corredor, municipio, responsable declarado, descripción, referencias, folios, observaciones y caracterización EOD/TECHINT son texto libre con sus límites de longitud.
- Fechas, coordenadas, mediciones, profundidades, precisiones, velocidades, conteos y márgenes son valores tipados y validados por rango.
- Confirmaciones como Blue UAS/NDAA, análisis diferencial, transferencia a VELES, fusión, protección, traslape GPR e interrogación remota son hechos booleanos del proceso.
- La secuencia de las cuatro fases, los cierres `closed`/`discarded`, los códigos que habilitan transiciones y los mínimos de evidencia son invariantes de dominio. Una edición de etiqueta u orden en PostgreSQL no cambia estas reglas.
- «Selecciona», «Todos», «Sin clasificar», «Sin decisión» y «Sin resultado» son texto de presentación y no filas del catálogo. `unknown` sí es un código persistido cuando el catálogo lo define.

## Límites de esta etapa

Las incidencias usan PostgreSQL por defecto y el almacén JSON solo se permite explícitamente en `Testing`. La pertenencia y vigencia de catálogos se comprueban dentro de la misma transacción que guarda la fase. La autenticación, ownership y permisos provienen de la sesión del Objetivo 4. El sistema registra referencias y decisiones documentales; no procesa señales LiDAR/GPR, opera equipos, transmite SMS ni ejecuta acciones físicas.
