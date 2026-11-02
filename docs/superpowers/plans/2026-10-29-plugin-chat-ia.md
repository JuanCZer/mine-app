# Plan de implementación: Plug In y chat con IA

> **Ejecución:** utilizar subagentes dedicados para implementación y revisión independiente. Marcar cada casilla solo después de ejecutar la comprobación y registrar su resultado.

**Objetivo:** incorporar «Plug In» al menú lateral, crear una interfaz de chat y preparar una conexión segura desde el backend a una IA alojada en otro servidor, con una etapa posterior para consultar datos autorizados de PostgreSQL.

**Arquitectura:** Angular llama a endpoints propios del backend autenticado; el backend valida permisos, controla la comunicación y llama al servicio de IA mediante un adaptador configurado en el servidor. La consulta futura de la base se hará a través de una frontera controlada por el backend y con credenciales de solo lectura y permisos mínimos. La IA no recibe credenciales de PostgreSQL ni acceso directo a la base por defecto.

**Tecnología:** Angular/TypeScript/LESS, ASP.NET Core, PostgreSQL y HTTP hacia el proveedor de IA.

**Estado:** plan; las tareas están pendientes. La integración real depende de contar con el contrato y el entorno de la IA.

## Decisiones y datos pendientes

- **Protocolo de la IA:** confirmar endpoint, documentación de API, modelo, autenticación, formato de mensajes/respuestas, límites y compatibilidad con streaming. No se presupone que sea compatible con OpenAI.
- **Historial:** decidir si se conserva solo en la sesión de pantalla o en PostgreSQL, con plazo de retención, eliminación y permisos. Como punto de partida no se almacenarán conversaciones permanentemente; si se requiere persistencia se ejecutará la etapa 4.
- **Acceso al chat:** confirmar qué roles pueden utilizarlo. El backend será la fuente de autorización; la opción del menú respetará la misma política.
- **Datos para la IA:** definir tablas o vistas, campos, filtros, permisos por usuario, clasificación y casos de uso antes de habilitar consultas. No se transferirán incidencias ni datos de la base automáticamente.
- **Red y despliegue:** confirmar dirección interna o DNS del servicio, puerto, TLS/CA y conectividad desde el backend alojado. No guardar credenciales en este plan ni en el frontend.

## Restricciones generales

- La IA no recibe instrucciones para ejecutar SQL arbitrario ni permisos de escritura. El modelo puede proponer una intención estructurada, pero el backend valida y ejecuta solo consultas predefinidas y autorizadas.
- Cualquier información enviada al servidor de IA sale del límite de la aplicación: aplicar permisos, minimización de datos y reglas de tratamiento antes de enviarla. El contenido de la base y las respuestas del modelo se tratan como datos no confiables, no como instrucciones de sistema.
- Configuración y secretos de IA exclusivamente en backend; usar ejemplos sin secretos en `backend/.env.example` y mantener `.env` ignorado por Git. No exponer claves, cadenas de conexión ni mensajes sensibles en logs o respuestas de error.
- No permitir que el usuario del chat suministre una URL, cabeceras o credenciales para que el backend visite destinos arbitrarios. Configurar destino fijo en servidor, validar esquema/host/puerto, desactivar redirecciones y restringir salida de red al servicio autorizado. Una dirección privada puede ser válida si está configurada explícitamente.
- Servicio de IA deshabilitado hasta que su configuración sea válida. Si no está disponible, la aplicación informa el estado y continúa funcionando; no inventa respuestas.
- La implementación inicial no incluye adjuntos, ejecución de herramientas, cambios en incidencias ni acciones sobre la base. Streaming y persistencia requieren definir contratos antes de implementarse.
- Las pruebas automatizadas usarán proveedor y base aislados o simulados. No ejecutar escrituras ni pruebas destructivas en la base remota actual. El usuario ejecutará manualmente cualquier SQL entregado para su servidor.

## Estructura prevista

- `src/app/layout/app-layout.*` y `src/app/app.routes.ts`: entrada «Plug In», ruta autenticada y navegación en modos normal, colapsado y móvil.
- `src/app/pages/plugin/`: pantalla de chat, estilos y pruebas.
- `src/app/core/models/plugin.model.ts` y `src/app/core/services/plugin.service.*`: contrato y cliente HTTP hacia el backend.
- `backend/Plugin/`: configuración tipada, endpoint, política, cliente y adaptador de proveedor.
- `backend/Program.cs`, `backend/.env.example`, `backend-tests/`: registro, ejemplos y pruebas de seguridad e integración.
- Si se autoriza persistencia o lectura de datos: un archivo SQL para copiar y ejecutar manualmente, documentación de roles/vistas y pruebas contra PostgreSQL aislado.
- `docs/runbooks/`: configuración, conectividad, rotación de secretos y comprobación operativa.

## Etapa 1 — Definir contrato y política de acceso

- [ ] Documentar proveedor, formato de petición/respuesta, autenticación, modelo, timeout, límites y condiciones de cancelación. Si el proveedor todavía no existe, definir una interfaz interna y mantener el adaptador real pendiente.
- [ ] Definir los roles con acceso al chat y a cada conjunto de datos; verificar que ningún permiso de administración se conceda implícitamente.
- [ ] Decidir historial, retención, streaming y límites de tamaño. Registrar las decisiones y sus implicaciones antes de cerrar las etapas que dependan de ellas.
- [ ] Identificar datos que nunca deben enviarse a la IA y documentar los usos permitidos de la información de incidencias.

**Aceptación:** contrato suficiente para probar el adaptador; decisiones aún no tomadas identificadas explícitamente y sin credenciales en documentos.

## Etapa 2 — Backend y conexión configurable

- [ ] Definir un contrato propio de chat, por ejemplo `POST /api/plugin/chat`, con texto de usuario y respuesta estructurada; determinar si requiere historial en cada solicitud según la decisión de la etapa 1.
- [ ] Crear pruebas de autenticación, autorización por rol, CSRF, cambio obligatorio de contraseña y `Cache-Control: no-store`; rechazar solicitudes vacías, demasiado grandes y duplicados en curso según la política acordada.
- [ ] Implementar opciones tipadas de IA: habilitación, URL fija, modelo, credencial, timeout y límites. Validar formato y destino sin imprimir valores sensibles; agregar ejemplos vacíos a `backend/.env.example`.
- [ ] Implementar el cliente HTTP y adaptador aislado para el protocolo confirmado, sin redirecciones automáticas y con cancelación, límites de respuesta y errores sanitizados. Sin protocolo confirmado, registrar adaptador deshabilitado y no simular una conexión funcional.
- [ ] Aplicar límites de frecuencia por usuario, tamaño, tiempo y concurrencia; definir su funcionamiento entre instancias antes de despliegue. No reutilizar sin revisión una cuota de autenticación para prompts.
- [ ] Probar con un servidor falso controlado: éxito, errores 4xx/5xx, timeout, cancelación, respuesta inválida o excesiva, intentos de redirección, ausencia de secretos en errores/logs y servicio no configurado.

**Aceptación:** el navegador nunca recibe la clave del proveedor; el backend responde con errores claros y no contacta destinos definidos por el usuario.

## Etapa 3 — Navegación y chat Angular

- [ ] Agregar «Plug In» a la barra lateral y ruta `/plug-in` dentro del shell autenticado, aplicando la política de acceso acordada; probar menú expandido, colapsado, móvil y acceso directo.
- [ ] Crear una pantalla de conversación con lista de mensajes y campo de texto; mostrar estados vacío, enviando, respuesta, error y servicio no configurado. Evitar envíos duplicados y permitir reintento o cancelación según el contrato.
- [ ] Renderizar la respuesta como texto seguro; no ejecutar HTML, scripts ni enlaces automáticos sin validación. Conservar foco, etiquetas, avisos accesibles y diseño adaptable.
- [ ] Crear servicio Angular que llame solo al endpoint propio del backend; nunca incluir host privado, claves o configuración sensible en el bundle.
- [ ] Probar límites, permisos, 401/403/429, errores, timeout, cancelación, sanitización y comportamiento de teclado; ejecutar `npm test -- --watch=false` y `npm run build:clean`.

**Aceptación:** el usuario autorizado puede enviar y recibir mensajes mediante el backend, con estados claros y sin exposición de credenciales.

## Etapa 4 — Historial persistente, solo si se solicita

- [ ] Definir propiedad de conversación, acceso por usuario/rol, plazo de retención, eliminación y alcance de auditoría. No registrar secretos ni contenido íntegro sin una decisión explícita.
- [ ] Diseñar tablas, índices y permisos mínimos; entregar **un solo archivo SQL** y guía para ejecución manual en el servidor, sin migraciones automáticas.
- [ ] Implementar lectura y escritura del historial con aislamiento entre usuarios, paginación y manejo de borrado; no almacenar contraseñas ni tokens.
- [ ] Probar aislamiento, roles, eliminación, límites y rollback en PostgreSQL aislado; registrar cualquier prueba real que no pueda ejecutarse.

**Aceptación:** historial accesible únicamente según la política definida y borrable conforme a la retención acordada.

## Etapa 5 — Lectura controlada de la base por la IA

- [ ] Acordar los casos de uso y documentar qué puede consultar cada rol, la clasificación de los datos y qué información puede enviarse al servidor de IA. Incluir qué ocurre con usuarios sin permiso.
- [ ] Diseñar operaciones concretas y parametrizadas o vistas restringidas; aplicar autorización por usuario en el backend, límites de filas y tiempo, y un contrato estructurado de resultados. Rechazar solicitudes fuera del catálogo y SQL arbitrario.
- [ ] Crear una cuenta PostgreSQL independiente de **solo lectura**, sin membresía administrativa ni permisos de escritura, y conceder acceso únicamente a las vistas aprobadas. Entregar un solo SQL para revisión y ejecución manual; validar permisos efectivos y conexiones seguras.
- [ ] Hacer que el backend valide cada consulta, filtre el resultado antes de enviarlo a la IA y mantenga trazabilidad de operación, actor y resultado sin guardar datos sensibles innecesarios.
- [ ] Probar intentos de acceso entre usuarios/roles, exfiltración, instrucciones incrustadas en datos, consultas fuera de catálogo, escritura, abuso de volumen y timeouts; ejecutar en una base de pruebas aislada.
- [ ] Definir cómo se presentan las fuentes o advertencias de datos no disponibles, y verificar que una respuesta del modelo no otorgue permisos ni modifique datos.

**Aceptación:** la IA solo recibe resultados explícitamente autorizados para el usuario; no obtiene credenciales ni acceso libre a PostgreSQL, y las consultas no alteran datos.

## Etapa 6 — Integración, seguridad y despliegue

- [ ] Documentar variables de configuración, ejemplo sin secretos, DNS/puerto, TLS/CA, permisos de red, rotación de credenciales y puesta en marcha del backend y la IA.
- [ ] Ejecutar revisión independiente de seguridad sobre autenticación, autorización, SSRF, límites, tratamiento de datos, prompts, secretos y privilegios PostgreSQL; corregir hallazgos y repetir pruebas.
- [ ] Ejecutar compilación y suites de frontend/backend, registrar resultados y cualquier verificación pendiente. Probar en navegador real y con un proveedor de prueba sin datos sensibles.
- [ ] Con contrato, credenciales y entorno autorizados, comprobar conectividad y flujo completo contra la IA real; probar lectura de base solo después de completar la etapa 5 y usando datos de prueba.
- [ ] Actualizar este archivo y los objetivos correspondientes con evidencias; diferenciar pruebas simuladas de integración real y no marcar conexión operativa sin haberla probado.

**Criterio final:** el chat está disponible para los roles acordados, la conexión y sus límites están verificados y cualquier acceso a datos cumple la política definida y tiene evidencia de pruebas.
