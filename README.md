# GDS · Incidencias y validación por fases

Aplicación Angular con API ASP.NET Core. Cada incidencia reúne el registro de indicios, las evidencias y las cuatro fases del proceso: **indicios → reconocimiento → vigilancia → mitigación**.

La ruta inicial `/incidencias` muestra una secuencia de cuatro puntos. Guardar un borrador no permite avanzar: cada fase debe validarse antes de habilitar la siguiente, con comprobaciones tanto en la interfaz como en la API.

[Campos, reglas de validación y contrato de la API](docs/note-phases.md).

## Ejecutar

Desde la raíz del proyecto, en dos terminales:

```bash
npm run backend
npm start
```

Abre `http://localhost:4200`. El login permanece desactivado y se usa el espacio demo. Las incidencias y sus fases se guardan en `backend/Data/notes.json`; las referencias de evidencias no cargan archivos a ese almacén.

El historial `/historial` muestra el avance de cada incidencia y permite abrir sus fases. `/mapa` muestra las coordenadas. La propiedad histórica `altitude` contiene la latitud y se mantiene por compatibilidad.

El [panel local anterior](docs/detection-module.md) sigue accesible en `/deteccion` y conserva su almacenamiento local independiente.

## Verificar

```bash
npm run build
npm test -- --watch=false
dotnet build backend
python3 backend-tests/test_note_phases.py
```
