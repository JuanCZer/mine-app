# Seguimiento de detección basado en el documento GDS

Este documento describe el panel de registros locales `/deteccion`. El flujo principal con validaciones obligatorias ahora está integrado en [Notas](note-phases.md), en `/notas`, y se guarda en la API.

Referencia de requisitos: **GDS SEDENA INTEGRACION .pdf**, 38 páginas, facilitado por el usuario. Las afirmaciones técnicas del documento se muestran como contexto atribuido al PDF; no se han validado capacidades de equipos ni estadísticas externas.

## Componentes y correspondencia con el documento

| Componente          | Función                                                                               | Páginas                                      |
| ------------------- | ------------------------------------------------------------------------------------- | -------------------------------------------- |
| `DetectionPage`     | Bandeja de expedientes, filtros, prioridades y exportación                            | 6, 13, 32                                    |
| `DetectionOverview` | Indicadores globales y resumen de las cuatro capas                                    | 6, 13, 32                                    |
| `CaseForm`          | Registro de anomalía con latitud, longitud, responsable, suelo y temporada            | 7–10, 32–35                                  |
| `LayerPipeline`     | Secuencia I&W → reconocimiento → vigilancia → mitigación                              | 6–26                                         |
| `CaseDetailPage`    | Revisión por etapa, referencias de levantamientos, dictamen y cierre                  | 10–19, 26                                    |
| `EvidencePanel`     | Referencias a evidencias LiDAR, RGB, IR, GPR, magnetometría, observaciones e informes | 9–17, 26                                     |
| `TerrainContext`    | Contexto de Vertisol, Andosol/Luvisol y Leptosol; limitaciones y falsos positivos     | 32–35                                        |
| `CaseMap`           | Ubicaciones con prioridad declarada y acceso al expediente; exportación GIS           | 13–14                                        |
| `CaseTimeline`      | Historial de creación, revisiones, evidencias y entregas                              | Trazabilidad añadida como decisión de diseño |

El PDF numera mitigación como capa 2 y vigilancia como capa 3, pero en la página 26 especifica que **la vigilancia precede a la mitigación**. Se conservan sus números y se aplica la secuencia 0 → 1 → 3 → 2.

## Reglas de avance

Cada etapa requiere una referencia de evidencia, revisor declarado y fundamento guardados. Los borradores se pueden guardar incompletos. No se permiten saltos de etapa ni editar etapas ya completadas.

- I&W: referencias al levantamiento base y al levantamiento comparativo.
- Reconocimiento: dictamen concluyente y evidencias de dos fuentes distintas entre RGB, IR, GPR y magnetometría. Exigir dos fuentes es una **decisión de diseño** para reflejar la correlación multisensorial del PDF; no constituye una regla técnica validada de detección. Un dictamen inconcluso permanece en esta etapa. Un falso positivo documentado cierra el expediente sin pasar a mitigación.
- Vigilancia: revisión del entorno documentada. Una revisión pendiente o bloqueada impide avanzar.
- Mitigación: referencias del informe especializado y de retroalimentación TECHINT. El módulo registra el cierre administrativo, sin instrucciones ni acciones de neutralización.

Los dictámenes, prioridades y responsables los captura el usuario. No se deducen amenazas por umbrales, no se calculan probabilidades con datos ficticios y ninguna transición certifica una zona segura.

## Uso

1. Ejecutar `npm start` y entrar a `/deteccion`, disponible como «Panel de registros locales» en el menú.
2. Crear un expediente; opcionalmente usar el botón de ejemplo ficticio cuando la bandeja esté vacía.
3. Completar la revisión de la etapa y guardar. Registrar referencias de evidencias por separado.
4. Completar la etapa cuando la lista de pendientes esté vacía. El servicio vuelve a verificar las reglas.
5. Consultar etapas previas, bitácora y contexto del terreno. Exportar el expediente completo en JSON o las ubicaciones de la lista filtrada en GeoJSON.

Los indicadores globales incluyen todos los expedientes locales, también los ejemplos señalados. El porcentaje de falsos positivos usa como denominador los dictámenes concluyentes de reconocimiento; muestra `—` cuando todavía no existen.

## Persistencia e integraciones

Esta entrega implementa los componentes y el flujo de revisión en Angular. Persiste en `localStorage`, clave `gds-detection-cases-v1`. Guardar revisiones y avanzar genera entradas de bitácora. Los fallos de lectura bloquean la sobrescritura; los fallos de escritura no se presentan como guardados. Se detectan modificaciones de otras pestañas antes de guardar, aunque localStorage no ofrece transacciones entre pestañas.

No incluye backend de expedientes, acceso multiusuario, sincronización entre dispositivos, firma del revisor, autorización por roles o auditoría inmutable. El login preexistente del proyecto sigue desactivado. El historial local no sustituye una auditoría de servidor. La paginación es de presentación, no paginación de servidor.

Las evidencias son **referencias**, no archivos cargados. No hay adquisición ni procesamiento de nubes LiDAR, telemetría, video, fusión de sensores, IA o actualización real de un catálogo TECHINT. El mapa usa la configuración Mapbox existente y necesita conectividad y un token válido; la tabla y las coordenadas permanecen disponibles si el mapa falla.

La vigilancia se representa mediante revisión documental del entorno. No se implementan captura de identificadores de telefonía, seguimiento de individuos, interferencia de señal ni control de hardware.

Para conectar datos reales se necesitan contratos y credenciales de las APIs de sensores, almacenamiento de evidencias, backend de expedientes con validación de transiciones, autenticación y permisos, y los requisitos de volumen, disponibilidad y retención. El PDF no especifica estos contratos ni una demanda de usuarios concurrentes; no se atribuye capacidad de producción a esta interfaz local.

## Validación

`npm run build` compila la aplicación de producción. `npm test -- --watch=false` ejecuta las pruebas de creación, persistencia, validación del flujo, bloqueos de revisión y manejo de falsos positivos.
