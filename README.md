# MigrarPasswords (PRODUCCION)

## Qué hace esta herramienta (explicación sencilla)

**MigrarPasswords** es una herramienta que **convierte las contraseñas guardadas en texto plano** (legibles por cualquiera que acceda a la base de datos) **en versiones "hash" seguras** (códigos ilegibles e irreversibles).

### Qué hace exactamente (paso a paso):

1. **Lee** la tabla de usuarios y busca las contraseñas que están en texto plano (columna `[password]`).

2. **Convierte** cada contraseña en un **hash bcrypt** (costo 12):
   - La contraseña original se mezcla con una "sal" aleatoria única por usuario.
   - Se aplica una función matemática lenta y costosa (bcrypt coste 12).
   - El resultado es un código tipo `$2a$12$AbCdEfGh...` que **no se puede revertir**.

3. **Guarda** ese hash en la columna nueva `PasswordHash` (que es la que usa el login nuevo).

4. **Verifica** que el hash generado funciona: lo lee de vuelta y comprueba que la contraseña original sigue coincidiendo.

5. **No toca** la columna antigua `[password]` (la deja intacta por si hace falta volver atrás).

6. **Solo procesa** usuarios que:
   - Tienen contraseña en texto plano (no vacía).
   - **No** tienen ya un hash en `PasswordHash`.

7. **Si algo falla** (contraseña con caracteres raros, hash raro, cambio inesperado): **cancela todo** y no guarda nada (transacción completa: todo o nada).

### Lo que NO hace:
- ❌ No borra la columna `[password]` (eso lo haces tú después de validar).
- ❌ No envía emails ni avisa a usuarios.
- ❌ No cambia la contraseña del usuario (solo la codifica).
- ❌ No modifica nada si hay **cualquier problema** (transacción completa: todo o nada).

### Para qué sirve:
- **Elimina el riesgo** de que alguien con acceso a la base de datos vea las contraseñas reales.
- **Cumple normativas** (RGPD, PCI, etc.) que exigen no guardar contraseñas en claro.
- **Permite el nuevo login seguro** (bcrypt + rate limiting + tokens de un uso).

---

Utilidad de consola VB.NET Framework 3.5. Genera bcrypt de coste 12 en
`dbo.VV_USERS.PasswordHash`, conserva exactamente `[password]` y no modifica
el login de la web. Es autonoma: no necesita IIS, web.config ni archivos de la web.
No es una pagina ASPX y no se ejecuta desde el navegador.

## Preparacion

1. Utilizar la carpeta del proyecto para compilar, o distribuir los archivos de `bin`
   juntos en una carpeta independiente fuera de la web.
2. Confirmar que existe `PasswordHash VARCHAR(255) NULL` en la copia de la tabla.
3. Revisar `Users_ConnectionString` de `App.config` antes de compilar o de
   `MigrarPasswords.exe.config` junto al ejecutable antes de utilizarlo.
   Rechaza cualquier catalogo distinto de `VVTecnics` antes de conectar y
   vuelve a comprobar `DB_NAME()` tras abrir la conexion.
4. Utilizar una cuenta restringida a TEST con SELECT sobre la tabla y UPDATE sobre
   PasswordHash, y visibilidad del esquema/triggers (VIEW DEFINITION sobre la tabla).
   Revisar con el DBA las dependencias. No utilizar sa ni una cuenta con permisos
   de escritura en produccion. Tener una copia de seguridad con acceso restringido.
5. Parar la web TEST y otros procesos que cambien usuarios. La migracion conserva
   texto plano: no cierra la vulnerabilidad hasta adaptar todos los consumidores
    y eliminar la columna antigua en una fase posterior.

## Configuracion independiente

- `App.config`: configuracion fuente. Build.ps1 la copia a
  `bin\MigrarPasswords.exe.config` en cada compilacion, sobrescribiendo esa copia.
- `MigrarPasswords.exe.config`: configuracion que lee el ejecutable, siempre junto
  a el, independientemente del directorio desde el que se invoque. Se puede editar
  sin recompilar; se aplicara al iniciar de nuevo la utilidad.
- Cambiar Data Source, User ID y Password para la conexion SQL; conservar
  Initial Catalog=VVTecnics. No hay ninguna alternativa ni fallback a web.config.
- Para autenticacion Windows, eliminar User ID y Password y utilizar
  Integrated Security=True. Se usara la identidad Windows que ejecute la utilidad.
- La configuracion inicial contiene la conexion TEST existente, ahora independiente
  de la web. Cambiar un archivo no actualiza el otro. Usar una cuenta exclusiva TEST.
- Con autenticacion SQL, el archivo contiene una credencial recuperable. Restringir
  permisos NTFS, no publicarlo ni incorporarlo a repositorios. En XML, escapar
  caracteres especiales (por ejemplo, & como &amp; y comillas dobles como &quot;).

Para distribuir, mantener juntos `MigrarPasswords.exe`, `MigrarPasswords.exe.config`,
`BCrypt.Net-Next.dll` y `THIRD-PARTY.txt` (contenido de bin). El equipo de destino
necesita .NET Framework 3.5, pero no Visual Studio ni la web. No cambiar el nombre
del ejecutable sin cambiar tambien el nombre de su archivo .exe.config.

## Compilar

Desde PowerShell en esta carpeta:

```powershell
powershell -NoProfile -File .\Build.ps1
```

Requiere el compilador de .NET 3.5 y PowerShell 5.1 para el script de compilacion.
El ejecutable utiliza CLR 2.0/.NET 3.5, no necesita PowerShell para funcionar.
Build.ps1 descarga BCrypt.Net-Next 4.0.2 desde NuGet oficial, comprueba su SHA-512
contra el catalogo NuGet y extrae solo la DLL net35. Es una version fijada por
compatibilidad, no la version mas reciente. El paquete queda en `packages` para
recompilar sin red. No se instala nada en el bin de la web ni se cambia su framework.
Se comprueba tambien que la DLL utiliza CLR 2.0. La DLL net35 de 4.0.3 se descarto
porque su metadata requiere CLR 4 y falla al ejecutarse con el runtime de .NET 3.5.

Dependencia y licencia:
https://www.nuget.org/packages/BCrypt.Net-Next/4.0.2
https://github.com/BcryptNet/bcrypt.net/blob/4.0.2/licence.txt
Metadatos del checksum:
https://api.nuget.org/v3/catalog0/data/2020.11.19.10.37.31/bcrypt.net-next.4.0.2.json

## Ejecutar

Pruebas locales con claves ficticias, SIN conexion SQL ni lectura de web.config:

```powershell
.\bin\MigrarPasswords.exe --self-test
```

Prueba de distribucion fuera de la web, usando configuraciones ficticias y sin SQL:

```powershell
powershell -NoProfile -File .\Verify-Standalone.ps1 -ScratchRoot C:\Ruta\Temporal
```

ScratchRoot debe ser una carpeta temporal existente. El script crea un subdirectorio
unico, no copia credenciales reales y siempre cancela --apply antes de conectar.

Revision de la tabla, CON conexion SQL pero SIN escrituras:

```powershell
.\bin\MigrarPasswords.exe --check
```

Migracion real de la copia PRODUCCION, CON escrituras y confirmacion interactiva:

```powershell
.\bin\MigrarPasswords.exe --apply
```

Comprobar el servidor mostrado y escribir exactamente `MIGRAR VVTecnics`.
Sin argumentos solo muestra ayuda, sin conectar. No hay modo automatico de aceptar
la confirmacion. No poner contrasenas ni cadenas de conexion en los argumentos.
Al finalizar, incluso si hay un error, espera a que se pulse INTRO antes de cerrar.
Si la entrada esta redirigida y llega a fin de archivo, termina sin esperar.

## Comportamiento

- Muestra la fase activa: configuracion, conexion, lectura/bloqueo SQL, revision
  previa, generacion, guardado y COMMIT/ROLLBACK. Durante la revision muestra ID,
  contador de usuarios y tiempo transcurrido; nunca claves ni hashes.
- Verificar hashes existentes tambien consume CPU por el coste de bcrypt, aunque
  no haya filas nuevas que actualizar. No reducir el coste para acelerar la prueba.
- Si se detiene en lectura/bloqueo SQL, comprobar sesiones/escritores con el DBA.
  Un timeout no demuestra por si solo que exista un bloqueo. COMMIT y ROLLBACK
  pueden tardar; no cerrar por la fuerza una migracion en curso.
- Sin argumentos muestra ayuda y espera INTRO; no abre SQL ni migra usuarios.

- Revisa todos los usuarios antes de escribir. No cambia mayusculas, espacios ni
  caracteres Unicode. En --check y --apply omite usuarios con [password] NULL o
  exactamente vacia, sin verificar ni modificar su PasswordHash y sin cancelar la
  migracion. El resumen muestra el total de omitidos. Los espacios no se omiten.
- Las claves con NUL, Unicode invalido o mas de 72 bytes UTF-8 siguen siendo
  incidencias: requieren tratamiento/restablecimiento, nunca truncamiento.
- Solo migra `PasswordHash IS NULL`. Valores vacios, hashes malformados o de coste
  diferente a 12 se consideran incidencias; no se sobrescriben automaticamente.
- Para usuarios con password no vacia, los hashes existentes se verifican contra
  la clave actual antes de omitir su actualizacion.
  Si el login antiguo ha permitido un cambio de password posterior, la utilidad
  detecta la divergencia y cancela. No usarla despues de retirar `[password]`.
- Si hay incidencias, NO se modifica ningun usuario. Se muestran solo ID y motivo.
- En --apply mantiene un bloqueo exclusivo de VV_USERS y una unica transaccion.
  Lee la tabla completa en memoria. Para tablas muy grandes, medir primero el coste
  y revisar memoria, log de transacciones y ventana de mantenimiento con el DBA.
- Genera salt aleatorio por hash. Verifica el generado y vuelve a leer y verificar
  el persistido, comprobando tambien que la clave original no ha cambiado.
- Solo hace COMMIT si todo termina correctamente. Ante error revierte la
  transaccion. Una interrupcion puede necesitar tiempo para completar el rollback.
  Solo el mensaje COMMIT correcto confirma el resultado; en caso de duda ejecutar
  --check. Puede repetirse sin regenerar hashes existentes validos.
- Incluso --check mantiene bloqueos de lectura durante la revision: usar en
  mantenimiento, no como monitor de una web en uso.
- Rechaza triggers activos de la tabla para no ejecutar efectos secundarios.
  No los deshabilita. No modifica esquema, password, usuarios, roles ni correos.
- No imprime claves, hashes, cadenas de conexion ni detalles completos de excepcion.
  El proceso maneja texto plano en memoria: restringir acceso al equipo y a dumps.
- Codigos de salida: 0 correcto, 1 error/cancelacion/argumentos, 2 incidencias de datos.

## Despues

Ejecutar --check, adaptar/probar login, altas, cambios y restablecimiento antes de
eliminar `[password]`. No poner PasswordHash NOT NULL si quedan cuentas pendientes.
Un --check correcto puede incluir usuarios omitidos: no certifica sus hashes.
Si no tienen hash, necesitaran restablecer la clave para acceder al nuevo login.
No publicar esta utilidad, configuracion ni paquetes por HTTP. Es preferible
ejecutarla desde una carpeta independiente fuera del sitio web, con permisos NTFS
restringidos. Retirarla del despliegue final; si se mantiene en App_Data, IIS debe
bloquear el acceso HTTP a esa carpeta.
