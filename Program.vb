Option Strict On
Option Explicit On

Imports System
Imports System.Configuration
Imports System.Data
Imports System.Data.SqlClient
Imports System.Diagnostics
Imports System.Text
Imports System.Text.RegularExpressions

Module Program
    Private Const TestDatabase As String = "VVTecnics"
    Private Const WorkFactor As Integer = 12

    Function Main(ByVal args As String()) As Integer
        Try
            If args.Length = 1 AndAlso args(0) = "--self-test" Then
                Console.WriteLine("Ejecutando pruebas locales bcrypt (sin SQL); pueden tardar unos segundos...")
                Return SelfTest()
            End If
            If args.Length <> 1 OrElse (args(0) <> "--check" AndAlso args(0) <> "--apply") Then
                Console.WriteLine("MigrarPasswords --check | --apply | --self-test")
                Console.WriteLine("Sin argumentos no se abre ninguna conexion. Solo se permite VVTecnics.")
                Console.WriteLine("Use --check para revisar o --apply para migrar. No se ha iniciado ninguna operacion.")
                Return If(args.Length = 0, 0, 1)
            End If

            Dim apply As Boolean = args(0) = "--apply"
            Console.WriteLine("Modo: {0}. Leyendo MigrarPasswords.exe.config...", If(apply, "MIGRACION", "COMPROBACION SIN ESCRITURAS"))
            Dim connectionString As String = ReadConnection()
            Dim builder As New SqlConnectionStringBuilder(connectionString)
            Console.WriteLine("Servidor configurado: {0}; base: {1}", builder.DataSource, builder.InitialCatalog)
            If apply Then
                Console.WriteLine("Detenga la web TEST y todos sus escritores. Tenga una copia de seguridad protegida.")
                Console.WriteLine("Se bloqueara VV_USERS hasta verificar y confirmar toda la migracion.")
                Console.WriteLine("No se borra ni modifica [password]. Para continuar escriba: MIGRAR VVTecnics")
                If Console.ReadLine() <> "MIGRAR VVTecnics" Then
                    Console.WriteLine("Cancelado. No se ha abierto ninguna conexion.")
                    Return 1
                End If
            End If

            Using connection As New SqlConnection(connectionString)
                Console.WriteLine("Conectando a SQL Server (timeout configurado: {0} segundos)...", builder.ConnectTimeout)
                connection.Open()
                Console.WriteLine("Conexion abierta. Comprobando base de datos efectiva...")
                Using command As New SqlCommand("SELECT DB_NAME()", connection)
                    RequireTestDatabase(Convert.ToString(command.ExecuteScalar()))
                End Using
                ' A single transaction prevents partial migration and concurrent password changes.
                Using transaction As SqlTransaction = connection.BeginTransaction(IsolationLevel.Serializable)
                    Try
                        Dim result As Integer = Migrate(connection, transaction, apply)
                        If apply AndAlso result = 0 Then
                            Console.WriteLine("Confirmando la transaccion (COMMIT). Espere al mensaje de confirmacion...")
                            transaction.Commit()
                            Console.WriteLine("COMMIT correcto. Hashes guardados y verificados; [password] conservado.")
                        Else
                            Console.WriteLine("Cerrando la transaccion sin cambios (ROLLBACK)...")
                            transaction.Rollback()
                            Console.WriteLine("Sin cambios: transaccion revertida (comprobacion o incidencias).")
                        End If
                        Return result
                    Catch
                        Try
                            Console.WriteLine("Operacion interrumpida. Revirtiendo la transaccion; puede tardar...")
                            transaction.Rollback()
                            Console.WriteLine("ROLLBACK completado.")
                        Catch
                            ' The connection may already be closed; never print connection details.
                            Console.Error.WriteLine("No se pudo confirmar el ROLLBACK desde el cliente. Compruebe el estado con el administrador SQL.")
                        End Try
                        Throw
                    End Try
                End Using
            End Using
        Catch ex As SqlException
            Console.Error.WriteLine("Error SQL {0}. No se confirma la migracion. Revise conexion, esquema, permisos y bloqueos.", ex.Number)
            If ex.Number = -2 Then Console.Error.WriteLine("Tiempo de espera SQL agotado. Puede ser conectividad, una consulta lenta o un bloqueo; revise la ultima fase mostrada.")
            If ex.Number = 1205 OrElse ex.Number = 1222 Then Console.Error.WriteLine("SQL ha detectado un conflicto o espera de bloqueos. Compruebe que no haya otra migracion o escritor activo.")
            Return 1
        Catch ex As Exception
            Console.Error.WriteLine("Error ({0}). No se confirma la migracion. Revise README.md; no se muestran datos sensibles.", ex.GetType().Name)
            Return 1
        Finally
            Console.WriteLine()
            Console.WriteLine("Pulse INTRO para cerrar...")
            Console.ReadLine()
        End Try
    End Function

    Private Function ReadConnection() As String
        Dim configured As ConnectionStringSettings = ConfigurationManager.ConnectionStrings("Users_ConnectionString")
        If configured Is Nothing OrElse String.IsNullOrEmpty(configured.ConnectionString) Then
            Throw New InvalidOperationException("Falta la conexion en MigrarPasswords.exe.config.")
        End If
        Dim builder As New SqlConnectionStringBuilder(configured.ConnectionString)
        RequireTestDatabase(builder.InitialCatalog)
        If builder.AttachDBFilename.Length <> 0 OrElse builder.UserInstance Then
            Throw New InvalidOperationException("No se permiten bases adjuntas ni instancias de usuario.")
        End If
        builder.PersistSecurityInfo = False
        builder.ApplicationName = "MigrarPasswords"
        Return builder.ConnectionString
    End Function

    Private Sub RequireTestDatabase(ByVal database As String)
        If Not String.Equals(database, TestDatabase, StringComparison.Ordinal) Then
            Throw New InvalidOperationException("Solo se permite VVTecnics.")
        End If
    End Sub

    Private Sub ValidateSchema(ByVal connection As SqlConnection, ByVal transaction As SqlTransaction)
        Dim sql As String = "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.VV_USERS', N'U') " & _
            "AND name = N'PasswordHash' AND ((system_type_id = 167 AND (max_length = -1 OR max_length >= 60)) " & _
            "OR (system_type_id = 231 AND (max_length = -1 OR max_length >= 120)))"
        Using command As New SqlCommand(sql, connection, transaction)
            If Convert.ToInt32(command.ExecuteScalar()) <> 1 Then
                Console.Error.WriteLine("Falta PasswordHash o no admite bcrypt. Se espera VARCHAR(255) NULL.")
                Throw New InvalidOperationException("Esquema no valido.")
            End If
        End Using
        Using command As New SqlCommand("SELECT HAS_PERMS_BY_NAME(N'dbo.VV_USERS', N'OBJECT', N'VIEW DEFINITION')", connection, transaction)
            If Not Object.Equals(command.ExecuteScalar(), 1) Then
                Console.Error.WriteLine("Se requiere VIEW DEFINITION sobre VV_USERS para comprobar triggers.")
                Throw New InvalidOperationException("Metadatos no visibles.")
            End If
        End Using
        ' Triggers could copy plaintext or modify other databases, even in a TEST transaction.
        Using command As New SqlCommand("SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.VV_USERS') AND is_disabled = 0", connection, transaction)
            If Convert.ToInt32(command.ExecuteScalar()) <> 0 Then
                Console.Error.WriteLine("VV_USERS tiene triggers activos. Revisarlos antes de utilizar esta utilidad.")
                Throw New InvalidOperationException("Triggers activos.")
            End If
        End Using
    End Sub

    Private Function PasswordProblem(ByVal password As String) As String
        If String.IsNullOrEmpty(password) Then Return "NULL o vacia; requiere restablecimiento"
        If password.IndexOf(ChrW(0)) >= 0 Then Return "contiene NUL; requiere restablecimiento"
        Try
            Dim encoding As New UTF8Encoding(False, True)
            If encoding.GetByteCount(password) > 72 Then Return "supera 72 bytes UTF-8; requiere restablecimiento"
        Catch ex As EncoderFallbackException
            Return "Unicode no valido; requiere restablecimiento"
        End Try
        Return Nothing
    End Function

    Private Function Matches(ByVal password As String, ByVal hash As String) As Boolean
        ' Bound the cost before verifying pre-existing values, including malformed hashes.
        If hash Is Nothing OrElse Not Regex.IsMatch(hash, "\A\$2[aby]\$12\$[./A-Za-z0-9]{53}\z") Then Return False
        Try
            Return BCrypt.Net.BCrypt.Verify(password, hash)
        Catch ex As ArgumentException
            Return False
        Catch ex As BCrypt.Net.SaltParseException
            Return False
        End Try
    End Function

    Private Function HasPassword(ByVal user As DataRow) As Boolean
        Return Not user.IsNull("password") AndAlso CStr(user("password")).Length > 0
    End Function

    Private Function Migrate(ByVal connection As SqlConnection, ByVal transaction As SqlTransaction, ByVal apply As Boolean) As Integer
        Dim elapsed As Stopwatch = Stopwatch.StartNew()
        Dim hints As String = If(apply, " WITH (TABLOCKX, HOLDLOCK)", " WITH (HOLDLOCK)")
        Using users As New DataTable()
            Using command As New SqlCommand("SELECT id, [password], PasswordHash FROM dbo.VV_USERS" & hints & " ORDER BY id", connection, transaction)
                Console.WriteLine("Leyendo VV_USERS y adquiriendo bloqueo de {0} (timeout de consulta: {1} segundos)...", If(apply, "escritura", "lectura"), command.CommandTimeout)
                Using adapter As New SqlDataAdapter(command)
                    adapter.Fill(users)
                End Using
            End Using
            ' Validate triggers after acquiring the table lock, so concurrent DDL cannot slip in.
            Console.WriteLine("Usuarios leidos: {0}. Comprobando esquema, permisos y triggers...", users.Rows.Count)
            ValidateSchema(connection, transaction)
            Console.WriteLine("Revision previa: se verifican tambien los hashes existentes. Bcrypt es deliberadamente costoso.")
            Dim pending As Integer = 0
            Dim verified As Integer = 0
            Dim issues As Integer = 0
            Dim skipped As Integer = 0
            Dim reviewed As Integer = 0
            For Each user As DataRow In users.Rows
                reviewed += 1
                Console.WriteLine("Revision {0}/{1}, ID {2}; transcurridos {3:F1} s.", reviewed, users.Rows.Count, user("id"), elapsed.Elapsed.TotalSeconds)
                If Not HasPassword(user) Then
                    skipped += 1
                    Console.WriteLine("  Omitido: password NULL o vacia; sin cambios.")
                    Continue For
                End If
                Dim password As String = CStr(user("password"))
                Dim problem As String = PasswordProblem(password)
                If problem IsNot Nothing Then
                    Console.WriteLine("ID {0}: {1}.", user("id"), problem)
                    issues += 1
                ElseIf user.IsNull("PasswordHash") Then
                    pending += 1
                ElseIf Matches(password, CStr(user("PasswordHash"))) Then
                    verified += 1
                Else
                    Console.WriteLine("ID {0}: hash existente no valido, distinto o de coste diferente a 12. No se sobrescribe.", user("id"))
                    issues += 1
                End If
            Next
            Console.WriteLine("Total: {0}; pendientes: {1}; hashes ya verificados: {2}; incidencias: {3}; omitidos (password NULL o vacia): {4}.", users.Rows.Count, pending, verified, issues, skipped)
            If issues <> 0 Then Return 2
            If Not apply Then Return 0

            Console.WriteLine("Revision terminada. Usuarios a actualizar: {0}. Los cambios no seran definitivos hasta COMMIT.", pending)
            Dim migrated As Integer = 0
            Using update As New SqlCommand("UPDATE dbo.VV_USERS SET PasswordHash = @Hash WHERE id = @Id AND PasswordHash IS NULL; SELECT CONVERT(int, @@ROWCOUNT);", connection, transaction)
                update.Parameters.Add("@Hash", SqlDbType.VarChar, 255)
                update.Parameters.Add("@Id", SqlDbType.Int)
                For Each user As DataRow In users.Rows
                    If Not HasPassword(user) Then Continue For
                    If Not user.IsNull("PasswordHash") Then Continue For
                    Dim password As String = CStr(user("password"))
                    Console.WriteLine("Migracion {0}/{1}, ID {2}: generando y verificando bcrypt...", migrated + 1, pending, user("id"))
                    Dim hash As String = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor)
                    If Not Matches(password, hash) Then Throw New InvalidOperationException("Hash generado no valido.")
                    update.Parameters("@Hash").Value = hash
                    update.Parameters("@Id").Value = user("id")
                    Console.WriteLine("ID {0}: guardando y volviendo a leer el hash...", user("id"))
                    If Convert.ToInt32(update.ExecuteScalar()) <> 1 Then Throw New InvalidOperationException("Numero de filas inesperado.")

                    Using readBack As New SqlCommand("SELECT [password], PasswordHash FROM dbo.VV_USERS WHERE id = @Id", connection, transaction)
                        readBack.Parameters.Add("@Id", SqlDbType.Int).Value = user("id")
                        Using reader As SqlDataReader = readBack.ExecuteReader()
                            If Not reader.Read() Then Throw New InvalidOperationException("Usuario desaparecido.")
                            If reader.IsDBNull(0) OrElse Not String.Equals(reader.GetString(0), password, StringComparison.Ordinal) Then
                                Throw New InvalidOperationException("La contrasena original ha cambiado.")
                            End If
                            If reader.IsDBNull(1) OrElse Not String.Equals(reader.GetString(1), hash, StringComparison.Ordinal) OrElse Not Matches(password, reader.GetString(1)) Then
                                Throw New InvalidOperationException("Hash persistido no valido.")
                            End If
                        End Using
                    End Using
                    migrated += 1
                    Console.WriteLine("ID {0}: hash guardado y verificado ({1}/{2}); pendiente de COMMIT.", user("id"), migrated, pending)
                Next
            End Using
            Return 0
        End Using
    End Function

    Private Function SelfTest() As Integer
        RequireTestDatabase(TestDatabase)
        Dim rejected As Boolean = False
        Try
            RequireTestDatabase("INVALID_DB_NAME")
        Catch ex As InvalidOperationException
            rejected = True
        End Try
        Assert(rejected, "rechazo de base invalida")
        Using users As New DataTable()
            users.Columns.Add("password", GetType(String))
            Dim user As DataRow = users.NewRow()
            Assert(Not HasPassword(user), "omitir password NULL")
            user("password") = ""
            Assert(Not HasPassword(user), "omitir password vacia")
            user("password") = "   "
            Assert(HasPassword(user), "no omitir espacios")
            user("password") = "Abc123"
            Assert(HasPassword(user), "procesar password con contenido")
            user("password") = "ab" & ChrW(0) & "cd"
            Assert(HasPassword(user) AndAlso PasswordProblem(CStr(user("password"))) IsNot Nothing, "NUL sigue siendo incidencia")
        End Using
        Assert(PasswordProblem(Nothing) IsNot Nothing, "NULL")
        Assert(PasswordProblem("") IsNot Nothing, "vacia")
        Assert(PasswordProblem("ab" & ChrW(0) & "cd") IsNot Nothing, "NUL")
        Assert(PasswordProblem(New String("a"c, 72)) Is Nothing, "limite 72 bytes")
        Assert(PasswordProblem(New String("a"c, 73)) IsNot Nothing, "exceso 72 bytes")
        Assert(PasswordProblem(New String(ChrW(&H20AC), 25)) IsNot Nothing, "limite UTF-8")
        Assert(PasswordProblem(ChrW(&HD800).ToString()) IsNot Nothing, "Unicode invalido")
        Dim password As String = "  Abc'" & ChrW(&HF1) & "123  "
        Dim hash As String = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor)
        Assert(Matches(password, hash), "verificacion bcrypt y Unicode")
        Assert(Not Matches(password.ToLowerInvariant(), hash), "mayusculas")
        Assert(Not Matches(password.Trim(), hash), "espacios")
        Assert(Not Matches("incorrecta", hash), "clave incorrecta")
        Assert(Not Matches(password, "no-es-un-hash"), "hash malformado")
        Assert(Not Matches(password, hash.Replace("$12$", "$31$")), "coste no permitido")
        Dim otherHash As String = BCrypt.Net.BCrypt.HashPassword(password, WorkFactor)
        Assert(hash <> otherHash AndAlso Matches(password, otherHash), "salt aleatorio")
        ' jBCrypt TestBCrypt.java test vector: password abc, cost 12 (Damien Miller).
        Assert(Matches("abc", "$2a$12$EXRkfkdmXn2gzds2SSitu.MW9.gAVqa9eLS1//RYtYCmB1eLHg.9q"), "vector externo")
        Console.WriteLine("PASS: pruebas criptograficas y protecciones. Sin SQL, SMTP ni acceso a web.config.")
        Return 0
    End Function

    Private Sub Assert(ByVal condition As Boolean, ByVal name As String)
        If Not condition Then
            Console.Error.WriteLine("FALLO de prueba: {0}", name)
            Throw New InvalidOperationException("Prueba fallida.")
        End If
    End Sub
End Module
