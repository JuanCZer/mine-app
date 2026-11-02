# Matriz de QA — User Administration

## Objetivo y alcance

La entrega de user administration se valida con un combo de evidencia automatizada y revisión manual: navegación, permisos, listados, creación, reintentos idempotentes, cambios de rol/estado, contraseña temporal y accesibilidad responsive. La matriz consolidada captura el estado actual y las pruebas pendientes que requieren revisión del entorno real.

| Scenario | AutomatedEvidence | ManualEvidence | Result | Date |
| --- | --- | --- | --- | --- |
| Login and session guard | `npm run test:env`, Angular build, backend identity suite | flujo de acceso login/logout en navegador | Pass | 2026-10-28 |
| Authorization and admin route | `adminGuard` + route contract + Angular compile | intentar acceso sin permiso en desktop y móvil | Pass | 2026-10-28 |
| Directory list and filters | `ng build`, service contract + page tests | listado, filtros y vacíos en tablet/escritorio | Pass | 2026-10-28 |
| Create user and temporary password | idempotency contract + feature flow | copiar la contraseña temporal, cerrar la ventana y revisar el intento | Pass | 2026-10-28 |
| Role/status mutation | optimistic concurrency contract | cambiar estado y volver a recargar con conflicto | Pass | 2026-10-28 |
| Accessibility keyboard and focus | DOM semantics and route guard checks | Tab, Shift+Tab, focus return, announcements | Partial | 2026-10-28 |
| Responsive layout and sidebar | CSS tokens and breakpoint checks | 375/768/1024/1440 viewport verification | Partial | 2026-10-28 |
| Bootstrap and rollout order | `scripts/tests/environment.test.mjs` + `build:clean` evidence | revisar el orden de bootstrap y rollout del entorno real | Partial | 2026-10-28 |
| Final QA gate | `npm run build:clean` + documented evidence | smoke full path + release readiness | Pending | 2026-10-28 |

## Observaciones

- La automatización local cubre compilación, documentación y el gate de build limpio de Angular.
- La revisión humana de accesibilidad y responsive sigue siendo necesaria en un navegador real para cerrar la fase final del objetivo 19.
- El entorno real para Postgres y despliegue sigue sin estar disponible en este host; la evidencia del backend productivo debe cumplirse en la base aislada y en la ruta de rollout aprobada.
