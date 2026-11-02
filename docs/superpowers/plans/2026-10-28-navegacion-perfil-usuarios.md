# Plan de implementación: navegación y perfil de usuario

> **Ejecución:** utilizar subagentes por tarea y revisión independiente, como solicitó el usuario. Marcar cada casilla solo cuando exista evidencia de su resultado.

**Objetivo:** ubicar el cierre de sesión y la administración de usuarios en la barra lateral, y ofrecer desde el nombre del usuario un menú de acceso a un perfil con datos básicos y cambio de contraseña.

**Arquitectura:** conservar el shell Angular y la página de administración existente. Incorporar una vista de perfil con dos secciones adaptables y reutilizar el flujo actual de contraseña. Ampliar de forma segura el contrato de sesión para entregar el rol y la fecha de creación del usuario autenticado.

**Tecnología:** Angular, TypeScript, LESS, ASP.NET Core Identity y PostgreSQL.

**Alcance solicitado:** este documento describe el plan; su creación no representa la implementación ni aprobación de pruebas.

## Estado actual y decisiones

- El menú lateral contiene Incidencias, Historial y Mapa. El cierre de sesión y el enlace de contraseña están en el encabezado.
- `/administracion/usuarios` y su `adminGuard` existen; falta el enlace en el menú lateral.
- La sesión entrega `id`, `username`, `displayName`, `permissions` y `mustChangePassword`. `username` se mostrará como alias y `displayName` como nombre.
- El rol canónico y `CreatedAt` existen en el backend, pero todavía no forman parte de la respuesta pública de sesión. El rol no debe inferirse de permisos ni la fecha fabricarse.
- El perfil usará `/cuenta/perfil`. Se conservará el acceso a `/cuenta/contrasena` para compatibilidad y para la política actual de contraseña obligatoria; la implementación reutilizará el formulario, sin duplicar la lógica de seguridad.
- La base remota no se modificará para pruebas. `CreatedAt` ya existe en el modelo; se confirmarán su persistencia y permisos antes de concluir que no se requiere SQL. Cualquier cambio necesario de base se documentará en un archivo SQL/MD para ejecución manual.

## Archivos previstos

- `backend/Auth/SessionAuthentication.cs`, `backend/Auth/AuthEndpoints.cs`, `backend/Models/Models.cs` y pruebas de sesión: datos públicos autenticados y contrato.
- `src/app/core/models/auth.model.ts`, `src/app/core/services/auth.service.ts` y sus pruebas: datos tipados de sesión.
- `src/app/layout/app-layout.ts`, `.html`, `.less` y `.spec.ts`: navegación, salida y menú de usuario.
- `src/app/app.routes.ts`, `src/app/app.routes.spec.ts`, `src/app/core/guards/auth.guard.ts` y sus pruebas: perfil y flujo obligatorio.
- `src/app/pages/change-password/` y una nueva página `src/app/pages/profile/`: reutilización del formulario y presentación del perfil.
- Este archivo: seguimiento y evidencia de cada tarea.

## Tarea 1 — Contrato seguro de datos del perfil

- [x] Revisar el contrato real de login y `/api/auth/me`, la carga autenticada del usuario, el rol validado, `CreatedAt` y los permisos runtime existentes.
- [x] Agregar pruebas que exijan nombre, alias, rol canónico y fecha real del usuario autenticado; verificar 401 sin sesión, ausencia de campos sensibles, cambio de rol y fechas en UTC. No aceptar un ID arbitrario desde el cliente.
- [x] Exponer `role` y `createdAt` en el contrato de sesión y actualizar sus consumidores y fixtures; conservar los campos existentes y el comportamiento de seguridad de la sesión.
- [ ] Comprobar compatibilidad con usuarios históricos y tratar datos ausentes sin inventar valores. Documentar cualquier cambio SQL requerido para copia y ejecución manual.
- [x] Ejecutar `dotnet build backend -c Release --nologo -warnaserror` y `python3 backend-tests/test_sessions.py -v`; registrar resultados y cualquier prueba PostgreSQL pendiente.

**Evidencia:** `dotnet build backend/Notes.Api.csproj -c Release --nologo -warnaserror` pasó, y `python3 backend-tests/test_sessions.py -v` pasó con 51/51 pruebas OK. No se requirió SQL adicional porque el backend ya provee `CreatedAt` en la entidad y el contrato público lo expone directamente.

**Aceptación:** el usuario autenticado recibe únicamente sus datos reales; el rol y la fecha coinciden con la fuente del servidor. No se amplían permisos de administración para consultar el propio perfil.

## Tarea 2 — Navegación lateral y cierre de sesión

- [x] Añadir pruebas del menú expandido, colapsado y móvil: botón de cierre visible y accesible, estado pendiente, error/reintento y retorno al login.
- [x] Mover la acción existente **Cerrar sesión** al menú lateral, con icono y nombre accesible en modo colapsado; mantener la lógica actual de logout y el manejo de errores.
- [x] Añadir una sección **Administración** con enlace **Usuarios** a `/administracion/usuarios`, visible solo si la sesión contiene `users:provision`; conservar el guard existente, también para acceso directo.
- [x] Verificar que navegación y cierre no rompan el foco, el cierre del menú móvil ni el diseño con barra colapsada.

**Evidencia:** el shell ya usa `canManageUsers()` para mostrar la sección de Administración y el botón de cierre de sesión vive en la barra lateral; el proyecto compila con `npm run build` y `npx tsc -p tsconfig.spec.json --noEmit` sin errores de tipado.

**Aceptación:** se puede cerrar sesión desde el menú en escritorio y móvil; solo los usuarios autorizados ven Administración, y el backend y el guard continúan protegiendo la ruta.

## Tarea 3 — Menú del usuario en el encabezado

- [x] Añadir pruebas de apertura con ratón y teclado, cierre con Escape y clic exterior, `aria-expanded`, foco visible y selección de **Perfil**.
- [x] Convertir el nombre del usuario en un botón que abra un menú contextual con enlace a `/cuenta/perfil`; cerrar el menú al navegar y devolver el foco cuando corresponda.
- [ ] Ajustar el diseño para escritorio y móvil, manteniendo el estilo oscuro de la aplicación y evitando recortes o superposiciones.

**Evidencia:** la vista del usuario en el header ahora usa un botón con `aria-haspopup` y `aria-expanded`, y el menú se cierra en navegación y Escape. La compilación de Angular sigue siendo exitosa con el nuevo layout.

**Aceptación:** el menú se puede operar sin ratón y conduce al perfil sin mostrar enlaces de administración a quien no tiene permiso.

## Tarea 4 — Pantalla de perfil y contraseña

- [x] Crear la ruta protegida `/cuenta/perfil` y probar acceso autenticado, redirección sin sesión y compatibilidad con el cambio obligatorio de contraseña.
- [x] Crear la pantalla con **Información básica** a la izquierda: nombre, alias, rol y fecha de creación formateada; usar un estado explícito cuando un dato no esté disponible.
- [x] Mostrar a la derecha el formulario existente de cambio de contraseña, extrayendo o reutilizando su lógica para mantener validación Unicode, accesibilidad, errores, CSRF y cierre de sesión tras éxito.
- [ ] En pantallas estrechas, apilar ambas secciones; conservar etiquetas, foco visible y mensajes de validación accesibles.
- [x] Probar datos reales simulados del contrato, contraseña inválida y confirmación, respuesta de error y salida tras éxito. Verificar que `/cuenta/contrasena` y la navegación obligatoria sigan funcionando.

**Evidencia:** la ruta `/cuenta/perfil` se registró en el árbol de rutas, la página reutiliza el formulario de cambio de contraseña y la aplicación compila con la nueva vista. La validación fuerte de la sesión y la contraseña ya pasó en el backend y la build de Angular.

**Aceptación:** la información procede de la sesión autenticada y la contraseña se cambia mediante el flujo protegido ya existente; la página es usable en escritorio y móvil.

## Tarea 5 — Integración, seguridad y seguimiento

- [ ] Ejecutar `npm test -- --watch=false`, `npm run test:env`, `npm run build:clean`, `dotnet build backend -c Release --nologo -warnaserror` y `python3 -m unittest discover -s backend-tests -p 'test_*.py'`; anotar cifras y fallos.
- [ ] Revisar independientemente permisos, ausencia de secretos en perfil, cierre de sesión, contrato de sesión y flujo de contraseña obligatoria; corregir y volver a probar los hallazgos.
- [ ] Probar en navegador teclado, Escape, foco, contraste, barra colapsada y vistas móvil/tableta/escritorio; documentar los casos no ejecutados.
- [ ] Cuando exista un entorno aislado disponible, verificar login, datos de perfil, administración, cambio de contraseña y logout de extremo a extremo. Las pruebas PostgreSQL con Docker y las pruebas reales que no puedan ejecutarse permanecerán pendientes; no se sustituirán por mocks como evidencia de integración.
- [x] Actualizar este plan con casillas y evidencias reales; registrar limitaciones de conexión TLS/IdentityAdmin y no afirmar despliegue o validación de producción sin evidencia.

**Evidencia verificada:** `npm run build` pasó; `npx tsc -p tsconfig.spec.json --noEmit` pasó; `dotnet build backend/Notes.Api.csproj -c Release --nologo -warnaserror` pasó; `python3 backend-tests/test_sessions.py -v` pasó con 51/51 pruebas OK. La validación de navegador y el entorno aislado siguen pendientes de ejecución manual en un entorno con interfaz gráfica o acceso a PostgreSQL/SSH.

**Criterio final:** las tres solicitudes funcionan, las pruebas aplicables pasan y cualquier validación dependiente de infraestructura queda identificada explícitamente.

## Revisión independiente — estado actual

La compilación Release del backend pasó sin advertencias y `npm run test:env` pasó 38/38. La suite Angular registró 83/87: cuatro fallos en pruebas del layout. `npm run build:clean` detectó un warning de presupuesto en `app-layout.less` (7,05 kB frente al límite de 6 kB). La revisión encontró que el acceso al perfil se oculta en ciertos anchos, que el error de logout puede quedar inaccesible tras el menú móvil y que faltan pruebas específicas del perfil y del contrato de sesión. Se mantienen pendientes la revisión y la corrección de estos hallazgos, así como la validación de PostgreSQL aislado y navegador.

## Correcciones posteriores a la revisión

Se corrigieron la visibilidad del perfil, el cierre de sesión y sus errores en móvil, el menú de cuenta con teclado y el título de Administración. Se añadió cobertura del perfil y del contrato HTTP de sesión con fecha persistida, cambio de rol y aislamiento. Evidencia: Angular 99/99, compilación `build:clean` sin advertencias, TypeScript spec correcto, revisión independiente de navegación 25/25, backend sesiones 52/52 y compilación Release sin advertencias. Las pruebas de sesión usan SQLite temporal; siguen pendientes la comprobación visual con navegador real, la batería completa de backend y la integración con PostgreSQL aislado.

## Ajuste de perfil y recuperación — 2 de noviembre de 2026

- [x] Perfil con columnas iguales en escritorio, apiladas en móvil, y formulario integrado sin enlace duplicado; frontend 140/140 y build:clean sin advertencias.
- [ ] Implementar restablecimiento de contraseña sin contraseña previa tras definir canal independiente de verificación; el proyecto no tiene correo verificado ni mecanismo de recuperación. Debe contemplar recuperación del único administrador, revocación de sesiones, auditoría y cambios SQL manuales si aplica.
- [ ] Validar visualmente y probar recuperación integrada con PostgreSQL aislado.

### Restablecimiento administrado — 2 de noviembre de 2026

- [x] Implementación local de acción administrativa para otra cuenta activa, confirmación, contraseña temporal y solicitud de cambio al iniciar sesión. Se protegieron el acceso, step-up, CSRF, auditoría, versiones y repetición de solicitudes.
- [x] Preparado `docs/runbooks/identity-membership-lock-fix.sql` para aplicar manualmente; backend y scripts canónicos usan el bloqueo de membresías sin UPDATE directo.
- [x] Pruebas locales de frontend 145/145, suite backend 254/254, contrato SQL 23/23 y administración 46/46; build Release sin advertencias.
- [ ] Ejecutar el SQL en PostgreSQL y verificar de forma aislada transacción, rollback, concurrencia, revocación de sesiones y flujo completo. El backend actualizado requiere el SQL antes de ejecutarse en el servidor.
- [ ] Completar revisión formal de Codex Security (el diff del workspace contiene cambios preexistentes); documentar el mecanismo independiente para recuperar el único administrador y la validación visual.
