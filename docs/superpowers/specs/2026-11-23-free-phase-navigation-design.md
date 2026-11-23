# Navegación libre entre fases

## Objetivo y alcance

Permitir consultar, editar y guardar borradores de las cuatro fases de una incidencia registrada y abierta, aunque las anteriores estén incompletas o sin validar. Mostrar los requisitos pendientes sin impedir la navegación. La petición del usuario autoriza el plan y su implementación.

## Comportamiento

- Todas las fases de una incidencia registrada se pueden consultar.
- Una fase abierta y sin validar permite capturar y guardar información parcial.
- Cambiar de fase conserva los cambios locales, incluso evidencias a medio escribir. Los cambios siguen pendientes de guardar en el servidor; salir de la incidencia conserva la advertencia existente.
- El progreso muestra requisitos pendientes, información completa pendiente de validación y fases validadas con texto explícito.
- La validación formal conserva sus requisitos y orden. Navegar o guardar no valida ni cierra una incidencia. Los borradores locales de otras fases deben guardarse antes de validar para evitar perderlos al cerrar.
- Las fases validadas y las incidencias cerradas o descartadas siguen siendo de solo lectura. Los borradores de fases posteriores a un descarte pueden consultarse.
- Primero se registra la incidencia con sus datos generales mínimos. No se modifica la creación, permisos, control de revisiones, catálogos ni reglas de cierre.

## Diseño

Separar `EditablePhase` de `ActivePhase` en el dominio: guardar usa la primera; validar mantiene la segunda. Ambos repositorios aplican las mismas reglas. PostgreSQL ya permite borradores en fases posteriores; su restricción de orden solo afecta las validaciones, por lo que no se requiere migración.

La interfaz conserva los editores visitados montados y oculta los inactivos. Esto conserva formularios y evidencias sin duplicar sus datos en otra caché. El panel agrega el estado de cambios pendientes de todos los editores y usa los requisitos existentes para mostrar avisos. Los catálogos solo se cargan al visitar cada fase.

## Alternativas consideradas

Deshabilitar toda validación eliminaría controles de cierre que no forman parte de la petición. Guardar automáticamente al navegar impediría cambiar de fase ante errores de red o datos inválidos. La conservación local permite navegar en ambos casos y mantiene explícito el guardado.

## Aceptación

Entrar directamente a fase 4 desde una fase 1 vacía; volver sin perder datos; guardar borradores incompletos en fases 2, 3 y 4; ver pendientes; conservar revisión y solo lectura; validar secuencialmente y cerrar solo con los requisitos satisfechos.
