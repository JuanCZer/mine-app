> Estado actualizado: los workflows de este plan se conservaron como plantillas inactivas en `deploy/futuro/workflows/`; el flujo provisional se sigue en `2026-11-09-despliegue-provisional.md`.

# Integración en main y promoción release

**Objetivo:** preparar protección de `main`, integración por PR y despliegue de una rama `release/*` que coincida con `main`, manteniendo las nuevas automatizaciones desactivadas por defecto.

- [x] Preparar una plantilla de ruleset para `main` con enforcement `disabled`, PR, revisión y check; no se ha instalado ni activado en GitHub.
- [x] Preparar workflow de auto-merge nativo con variables ausentes por defecto, comprobación de reglas activas y checkout exclusivo de base confiable.
- [x] Ajustar el despliegue manual para aceptar solo `release/*` que coincida con `main` y ejecutar pruebas y build del SHA exacto.
- [x] Reconsultar ambas referencias antes de actualizar el contenedor; documentar la ventana concurrente y que igualdad de SHA no demuestra procedencia de PR.
- [x] Documentar restricciones y prerequisitos de revisión, checks, bypass, runner, configuración de variables y pasos de activación.
- [x] Verificar localmente reglas, referencias, sintaxis YAML y comandos. Ver el resultado en la guía.
- [ ] Instalar o activar el ruleset en GitHub cuando corresponda; verificar permisos, plan, bypass y el nombre real del check.
- [ ] Probar PR y despliegue en GitHub y Docker aislado antes de activar en producción.

**Nota:** los jobs de integración y producción no realizan su acción si las variables de repositorio no están establecidas en `true`. El CI de comprobaciones existente sigue activo. El ruleset local desactivado no altera la protección real del repositorio.

**Resultado local:** 49/49 pruebas de configuración aprobadas, incluidas pruebas de reglas de revisión, rama `release/*`, coincidencia con `main`, cambios de referencias remotas y exclusión del runner privado en PR. Los archivos YAML, los bloques shell y el JSON de ejemplo se analizaron sintácticamente. No se ejecutó ningún merge ni despliegue, ni se aplicaron reglas o variables en GitHub. La confirmación de ausencia de bypass es administrativa, no una prueba automática; la consulta de referencias antes del despliegue no es atómica con el despliegue.
