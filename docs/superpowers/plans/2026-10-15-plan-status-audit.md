# Auditoría de estado de los planes

**Fecha:** 18 de octubre de 2026  
**Alcance:** plan general, objetivos 1–6 y lista de despliegue.  
**Criterio:** una casilla se marca solo cuando existe evidencia en código, pruebas, documentación o una ejecución registrada. Las puertas dependientes del ambiente permanecen abiertas aunque el control ya exista en código. El Task 19 incorpora la matriz de QA de user administration y el wrapper `build:clean` como evidencia documental y operativa del cierre del objetivo.

## Resultado consolidado

| Plan                               | Completadas | Pendientes | Estado                                                                              |
| ---------------------------------- | ----------: | ---------: | ----------------------------------------------------------------------------------- |
| Objetivo 1 · Interfaz y navegación |          15 |          0 | Completado y verificado                                                             |
| Objetivo 2 · Catálogos             |          18 |          2 | Implementación funcional; faltan las dos puertas sobre PostgreSQL aislado           |
| Objetivo 3 · PostgreSQL            |          50 |         13 | Persistencia e importador reforzados; faltan ejecución aislada y puertas operativas |
| Objetivo 4 · Autenticación         |          24 |          3 | Implementación funcional; faltan PostgreSQL aislado y comprobación visual           |
| Objetivo 5 · Backend y seguridad   |          22 |          0 | Implementación completa; 13 puertas externas bloquean únicamente la liberación      |
| Objetivo 6 · Configuración         |          18 |          0 | Implementación completa; 6 puertas externas bloquean únicamente la liberación       |
| Plan general                       |          26 |          9 | Sincronizado con los planes específicos                                             |
| Lista de despliegue                |           0 |         35 | Puertas por ambiente; ninguna se da por aprobada desde evidencia local              |

Los ledgers SDD de los seis objetivos registran pasos verificables. Catálogos queda en 7 acciones completadas y 2 abiertas; PostgreSQL, en 8 y 2; seguridad tiene 21 de 21 acciones de implementación completas y cuatro grupos de puertas operativas registrados por separado. La diferencia entre un plan y su ledger es deliberada: el plan cuenta criterios de producto, mientras el ledger resume paquetes de ejecución y revisión.

## Objetivo 1 · Interfaz y navegación

El objetivo quedó completo el 16 de octubre de 2026. Se conservó la referencia histórica, se probó el colapso con un formulario real modificado, se ejecutó la matriz de rutas y breakpoints, se verificó el mapa al ocultar y reabrir, y se persistieron los resultados bajo `docs/qa/objective-1/`. La regresión final y la revisión independiente están enlazadas desde el plan específico y su ledger SDD.

## Objetivo 2 · Catálogos

Quedan dos tareas:

1. Ejecutar `CatalogApiTests` contra una instancia PostgreSQL real y aislada, incluida una opción nueva disponible sin recompilar Angular.
2. Ejecutar en esa base el recorrido autenticado de creación, borrador y cuatro validaciones, incluido el cierre temprano por falso positivo.

El inventario real de semillas, la atomicidad de vigencia, los snapshots de etiquetas/versiones, las respuestas tardías y la auditoría de cambios quedaron implementados. La regresión consolidada actual pasó con 52 pruebas Angular, 136 backend y 32 de entorno. Las dos puertas aisladas permanecen abiertas porque este host no dispone de PostgreSQL local ni Docker.

## Objetivo 3 · PostgreSQL

Quedan trece tareas, agrupadas así:

1. Añadir la prueba de validación duplicada y ejecutar instalación, actualización y reaplicación en PostgreSQL efímero aislado.
2. Ejecutar las suites de repositorio y workflow con usuario autenticado y esquema aislado, incluido el filtro `PostgresWorkflowTests` previsto por el plan.
3. Probar concurrencia de **validaciones** entre dos instancias y rollback total por fallo inyectado antes de auditoría.
4. Completar backup/restore y TLS con verificación completa.
5. Respaldar/exportar las fuentes legadas y registrar inventario, huellas y correspondencias aprobadas.
6. Ejecutar el sink ya implementado en PostgreSQL aislado: reaplicación doble del SQL, reintento, reanudación, hash distinto, concurrencia, rollback, conciliación y lectura campo por campo.
7. Ejecutar el corte controlado y el recorrido runtime autenticado completo: catálogos, lista, mapa, historial, borradores, falso positivo y cuatro fases.

Se cerraron la matriz PDF → UI → DTO → SQL, las políticas de identidad legada, la normalización, el sink transaccional y el comando separado aprobado. La revisión independiente llevó a exigir lote canónico, mapas inmutables, bloqueos sin NUL por origen y destino, `SAVEPOINT` posterior al archivo, propietarios y catálogos activos, evidencia legado sin actor moderno, membresía y disposición por ocurrencia, backfill normalizado, detección de identificadores cruzados, conteos acumulados, cierre estricto, rol deployer, aprobador administrador y `VerifyFull`. Las pruebas locales aprobaron importador 4/4, contrato SQL 16/16 y regresión backend 136/136; el recorrido de falso positivo, cierre e importación permanece abierto hasta ejecutarse contra PostgreSQL aislado.

## Objetivo 4 · Autenticación

Quedan tres tareas:

1. Ejecutar `npm run test:postgres:objective4` en un host con Docker y conservar la evidencia sanitizada del ciclo completo.
2. Completar la inspección visual autenticada de expiración, fallo de red y sincronización entre dos pestañas.
3. Ejecutar contra el contenedor desechable la adaptación ya preparada del workflow de cuatro fases, concurrencia, falso positivo y reinicio.

La identidad, cookies, XSRF, logout, ownership, roles y revocación ya cuentan con pruebas y evidencia registrada.

## Objetivo 5 · Backend y seguridad

No quedan tareas de implementación. Permanecen trece puertas de liberación externas:

1. Habilitar y verificar TLS PostgreSQL con `hostssl`, CA/SAN y `pg_stat_ssl`.
2. Rotar las cadenas runtime/deploy y comprobar `VerifyFull`/`verify-full` con Npgsql y `psql`.
3. Configurar CIDR real de proxy, orígenes CORS, HTTPS/HSTS y límites de cuerpo en el ingress.
4. Configurar un keyring persistente, protegido y compartido para Data Protection.
5. Exportar, conciliar o eliminar de forma aprobada `gds-detection-cases-v1` en los perfiles pertinentes.
6. Recopilar CSP en modo reporte y aplicar la política final.
7. Restringir el token Mapbox por orígenes y scopes desde la cuenta del proveedor.
8. Aplicar `apply.sql` dos veces en una base limpia aislada.
9. Probar backup/restore y documentar RPO/RTO.
10. Ejecutar el flujo autenticado real de cuatro fases en PostgreSQL.
11. Probar validaciones concurrentes entre instancias.
12. Inyectar un fallo antes de auditoría y confirmar rollback conjunto.
13. Probar el sink PostgreSQL con reaplicación, reanudación, concurrencia, rollback y conciliación.

El modelo de amenazas y el escaneo estándar ya están completos. El escaneo cubrió 10 superficies, reportó un hallazgo medio y cero altos/críticos; el hallazgo se corrigió en el wrapper de despliegue y quedó cubierto por la suite de entorno. CORS por ambiente también quedó corregido y cubierto por la regresión local; la configuración de los valores reales sigue incluida en la puerta de infraestructura.

## Objetivo 6 · Configuración de entorno

No quedan tareas de implementación. Permanecen seis puertas de liberación externas:

1. Comprobar visualmente el mapa activo con un token autorizado.
2. Ejecutar wrapper y backend con PostgreSQL efímero, y comprobar que UI/endpoints no exponen configuración privada.
3. Completar con PostgreSQL aislado y respuestas HTTP la validación de copia limpia que ya aprobó instalación, build y revisión de artefactos con valores sintéticos.
4. Confirmar reproducibilidad desde un checkout limpio ya versionado.
5. Verificar orígenes y scopes del token Mapbox en la cuenta real.
6. Completar el escaneo de respuestas HTTP con un marcador privado sintético.

Los archivos locales `.env` están ignorados, sus plantillas y validadores existen, y la conexión privada permanece en el proceso del backend. Las seis puertas se identifican como `ENV-01` a `ENV-06` en el plan específico.

## Plan general

Después de sincronizarlo con los planes específicos quedan nueve tareas:

1. Preparar respaldo e inventario del legado y resolver propietarios históricos.
2. Migrar `notes.json` mediante el importador PostgreSQL aprobado.
3. Ejecutar la exportación/importación opcional de expedientes locales.
4. Validar los datos migrados y su conservación.
5. Completar el corte de `notes.json` y la conciliación del almacenamiento del navegador.
6. Cumplir los seis entregables completos.
7. Demostrar persistencia, importación y recuperación de PostgreSQL.
8. Completar la operación de exportación/importación del módulo local.
9. Demostrar configuración reproducible desde checkout limpio sin exposición de secretos.

## Lista de despliegue

Las 35 casillas siguen abiertas. Esta lista exige evidencia vigente por ambiente, responsable y versión: instalación desde lockfile, compilación, escaneo estándar, TLS, roles, backup/restore, secretos, keyring, autenticación, proxy, CSP, Mapbox, conciliación de navegador, observabilidad, smoke test y reversión. Las pruebas locales no sustituyen esas aprobaciones de despliegue.

## Orden recomendado para continuar

1. Crear el harness PostgreSQL efímero y usarlo para catálogos, autenticación, workflow, `apply.sql` e importación idempotente.
2. Añadir concurrencia de validaciones y fallo inyectado con rollback.
3. Exportar fuentes locales, ejecutar el sink y conciliar cada fila en la base aislada.
4. Cerrar TLS, backup/restore, keyring, proxy/CSP y restricciones de Mapbox.
5. Ejecutar el recorrido autenticado completo y después la lista de despliegue para el ambiente elegido.

## Nota sobre subagentes

Se distribuyó esta auditoría entre tres subagentes por pares de objetivos. Los intentos iniciales fueron rechazados por un límite temporal del servicio; al reanudarlos, los tres completaron revisiones de solo lectura. Sus hallazgos se contrastaron con el código y las pruebas antes de corregir casillas, conteos y textos obsoletos.
