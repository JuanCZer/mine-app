# Base de datos PostgreSQL

La base completa se entrega en un único archivo autocontenido: `apply.sql`; no
depende de otros SQL ni usa `\ir`. Desde la raíz del repositorio, ejecútelo con:

```sh
npm run db:apply
```

El wrapper lee únicamente `POSTGRES_DEPLOY_URL` de `backend/.env`, permite que una
variable ya inyectada tenga precedencia y usa `util.parseEnv`; no usa `source` ni
`eval`. Entrega la conexión a `psql` mediante `PGDATABASE`, nunca en argumentos ni
logs del wrapper, e invoca `psql` con un arreglo de argumentos y `shell: false`.
Antes de iniciar `psql`, exige una URL `postgres://` o `postgresql://` con un único
`sslmode=verify-full`; modos ausentes, débiles, duplicados o ambiguos fallan sin
reproducir la cadena.
La variable de conexión runtime no se copia al proceso de despliegue. Se requiere
que `psql` esté disponible en `PATH`.

El archivo usa una sola transacción y ejecuta, en este orden, identidad, catálogos, incidencias y fases, evidencias y auditoría, soporte de importación, guardas de flujo, semillas de catálogos, roles SQL, validación de integridad y reporte de conciliación. Es repetible: las estructuras están protegidas con `IF NOT EXISTS` y las semillas usan `ON CONFLICT`.

Después de importar datos heredados, vuelva a ejecutar el mismo `apply.sql`. La última sección presenta los totales por lote y las discrepancias; salvo el resumen de lotes, los resultados de conciliación deben quedar vacíos antes del corte. No hay un segundo archivo SQL que copiar o ejecutar.

La conexión se proporciona al proceso y nunca se guarda en el repositorio. En la primera instalación use una conexión administrativa capaz de crear extensiones, esquemas, tablas, funciones, triggers y roles `NOLOGIN`; después use la conexión de despliegue propietaria de los objetos. No ejecute el archivo con la cuenta runtime. La API usa `ConnectionStrings__Postgres`; `ConnectionStrings__Catalogs` es opcional y, si se omite, los catálogos usan la conexión principal.

La API valida ambas cadenas antes de atender solicitudes. Fuera de `Development` y
`Testing` exige `Ssl Mode=VerifyFull`; cualquier entorno rechaza
`Trust Server Certificate=true`. Identity, catálogos, incidencias y auditoría usan
los `NpgsqlDataSource` compartidos que nacen de esa única validación. Cuando la
conexión de catálogos no se configura por separado, reutiliza el mismo pool.
`Development` y `Testing` permiten temporalmente otro modo y emiten una advertencia
fija que no contiene host, usuario ni cadena. Esta excepción no convierte una
sesión en segura: el servidor actual todavía no ofrece TLS y debe corregirse antes
de ejecutar la aplicación en Production/Staging.

El procedimiento completo de ensayo, importación, conciliación, corte y restauración está en [Runbook de corte](../../docs/runbooks/postgresql-cutover.md). Las pruebas estáticas del repositorio no sustituyen una instalación limpia y una segunda ejecución contra PostgreSQL aislado.
