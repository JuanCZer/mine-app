const LABELS: Record<string, string> = {
  stage: 'Fase', data: 'Datos', validatedAt: 'Validada el', validatedBy: 'Validada por', catalogSnapshot: 'Catálogos al validar',
  review: 'Revisión', soil: 'Suelo', season: 'Temporada', terrainNotes: 'Notas del terreno', evidence: 'Evidencias',
  indications: 'Indicios', reconnaissance: 'Reconocimiento', surveillance: 'Vigilancia', mitigation: 'Mitigación',
  intake: 'Registro', platform: 'Plataforma', blueUasNdaaCompliant: 'Cumple Blue UAS / NDAA', flightDurationMin: 'Duración del vuelo (min)',
  lidarPointRate: 'Tasa de puntos LiDAR', payloadLb: 'Carga (lb)', missionSpeedMph: 'Velocidad de misión (mph)', windKnots: 'Viento (nudos)',
  terrainModelReference: 'Referencia del modelo de terreno', differentialAnalysisCompleted: 'Análisis diferencial completado', surfaceVariationMm: 'Variación de superficie (mm)', actionableAlertGenerated: 'Alerta accionable generada', personnelNotExposed: 'Personal no expuesto', coordinateTransferredToVeles: 'Coordenada transferida a VELES', observedAt: 'Fecha de observación', collectionSource: 'Fuente de recolección', signatures: 'Firmas', alternativeCauses: 'Causas alternativas', depressionDepthCm: 'Profundidad de depresión (cm)',
  magneticReadingNt: 'Lectura magnética (nT)', gprDepthCm: 'Profundidad GPR (cm)', rtkAccuracyCm: 'Precisión RTK (cm)', localizationAccuracyCm: 'Precisión de localización (cm)', probability: 'Probabilidad', priority: 'Prioridad', georeferencedPointReference: 'Referencia del punto georreferenciado', riskMapReference: 'Referencia del mapa de riesgos', aiClassificationReference: 'Referencia de clasificación IA', fusionCompleted: 'Fusión completada',
  observationStartedAt: 'Inicio de observación', observationEndedAt: 'Fin de observación', radioTechnologies: 'Tecnologías de radio', methods: 'Métodos', deployment: 'Despliegue', coveredChannels: 'Canales cubiertos', duplicatedChannelConfiguration: 'Configuración de canales duplicada', activeDeviceCount: 'Dispositivos activos', humanActivity: 'Actividad humana', environmentDecision: 'Decisión del entorno',
  coordinateReceived: 'Coordenada recibida', protectionLevel: 'Nivel de protección', personnelProtectionConfirmed: 'Protección del personal confirmada', equipmentProtectionConfirmed: 'Protección del equipo confirmada', detectionAssets: 'Medios de detección', interrogationAssets: 'Medios de interrogación', gprCoverageOverlap: 'Superposición de cobertura GPR', safetyMarginM: 'Margen de seguridad (m)', remoteInterrogationCompleted: 'Interrogación remota completada', artifactType: 'Tipo de artefacto', orientation: 'Orientación', activationMechanism: 'Mecanismo de activación', eodHandoffReference: 'Referencia de entrega EOD', neutralizationResult: 'Resultado de neutralización', routeResult: 'Resultado de ruta', techintComposition: 'Composición TECHINT', techintManufacture: 'Fabricación TECHINT', techintSignature: 'Firma TECHINT', catalogFeedbackReference: 'Referencia de retroalimentación al catálogo',
  sensor: 'Sensor', reference: 'Referencia', summary: 'Resumen', recordedAt: 'Registrada el', id: 'Identificador', version: 'Versión', selections: 'Selecciones', field: 'Campo', catalogKey: 'Catálogo', code: 'Código', label: 'Etiqueta', history: 'Historial',
};
function label(key: string): string { return LABELS[key] ?? key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase()); }
export function incidentFields(value: unknown): { label: string; value: string }[] {
  const result: { label: string; value: string }[] = [];
  const visit = (item: unknown, path: string[]) => {
    if (Array.isArray(item)) {
      if (!item.length) { result.push({ label: path.join(' · '), value: 'Sin registros' }); return; }
      if (item.every(v => v === null || typeof v !== 'object')) { result.push({ label: path.join(' · '), value: item.map(String).join(', ') }); return; }
      item.forEach((entry, index) => visit(entry, [...path, String(index + 1)])); return;
    }
    if (item && typeof item === 'object') { for (const [key, entry] of Object.entries(item)) visit(entry, [...path, label(key)]); return; }
    result.push({ label: path.join(' · '), value: item === null || item === '' ? 'Sin dato' : typeof item === 'boolean' ? (item ? 'Sí' : 'No') : String(item) });
  };
  visit(value, []); return result;
}
