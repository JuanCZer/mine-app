# Reorganización de pruebas del servidor

**Objetivo:** Ubicar las pruebas dentro de `server/tests`, agrupadas por servicio o área funcional, y retirar `backend-tests` de la raíz.

**Alcance:** Conservar las pruebas y herramientas existentes, sus fixtures y los comandos de PostgreSQL. Las pruebas transversales se agrupan por área sin atribuirlas artificialmente a un servicio. La API excluye el código de pruebas de su compilación; no se modifica la base de datos.

- [x] Trasladar y organizar las pruebas y sus proyectos auxiliares; corregir rutas, referencias y descubrimiento de pruebas.
- [x] Actualizar comandos npm y documentación operativa a las rutas nuevas.
- [x] Compilar la API y proyectos auxiliares, ejecutar las pruebas existentes y comprobar que la carpeta anterior desapareció.
- [x] Documentar los resultados y cualquier verificación pendiente.

**Resultado:** 254 pruebas de servidor aprobadas con `unittest discover`; compilación Release sin advertencias ni errores; 38 pruebas de configuración aprobadas. Los runners PostgreSQL aislados no se ejecutaron en esta tarea.

## Solución .NET — 8 de noviembre de 2026

- [x] Crear `server/MineApp.sln` y agrupar la API, la herramienta de importación y seis proyectos auxiliares de pruebas.
- [x] Actualizar el comando de arranque y las pruebas para apuntar explícitamente a `Notes.Api.csproj`, evitando ambigüedad con la solución.
- [x] Documentar la compilación de la solución y aclarar que las pruebas del servidor se ejecutan con `npm run test:server`.
- [x] Verificación final: `dotnet build server/MineApp.sln` sin errores ni advertencias; 254/254 pruebas de servidor y 41/41 de configuración aprobadas. La primera suite detectó una ruta ambigua en CORS, corregida y verificada en la segunda ejecución.

## Organización de la API — 8 de noviembre de 2026

- [x] Mover `MapEndpoints.cs` a `server/Map/` conservando su espacio de nombres y rutas.
- [x] Renombrar el proyecto y su ensamblado a `MineApp.Api`, actualizando solución, referencias de proyectos, scripts, pruebas y documentación operativa.
- [x] Mantener los espacios de nombres `Notes.Api` existentes; no se cambiaron contratos HTTP ni base de datos.
- [x] Verificación: 254/254 pruebas del servidor y 41/41 de configuración aprobadas. La solución compiló con 0 errores y 3 advertencias CS8602 en `IdentityHarness/Program.cs` durante la compilación completa.
