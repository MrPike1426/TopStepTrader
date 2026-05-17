Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite
Imports TopStepTrader.Data.Debug
Imports Xunit

Namespace TopStepTrader.Tests.Data.Debug

    ' BUG-85: AddColumnIfMissingAsync previously appended a literal "DEFAULT NULL"
    ' suffix to every ALTER TABLE, which clashed with NOT NULL DEFAULT callers
    ' (e.g. FEAT-56's "INTEGER NOT NULL DEFAULT 0") and produced
    ' SQLite Error 1: 'Cannot add a NOT NULL column with default value NULL'.
    Public Class DebugTradeDbContextTests

        Private Shared Async Function OpenInMemoryAsync() As Task(Of SqliteConnection)
            Dim conn As New SqliteConnection("Data Source=:memory:")
            Await conn.OpenAsync()
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "CREATE TABLE T (Id INTEGER PRIMARY KEY)"
                Await cmd.ExecuteNonQueryAsync()
            End Using
            Return conn
        End Function

        Private Structure ColumnInfo
            Public Name As String
            Public NotNull As Boolean
            Public DefaultValue As Object
            Public Exists As Boolean
        End Structure

        Private Shared Async Function GetColumnAsync(conn As SqliteConnection, table As String, column As String) As Task(Of ColumnInfo)
            Dim info As New ColumnInfo With {.Exists = False}
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"PRAGMA table_info({table})"
                Using reader = Await cmd.ExecuteReaderAsync()
                    While Await reader.ReadAsync()
                        Dim name = reader.GetString(1)
                        If String.Equals(name, column, StringComparison.OrdinalIgnoreCase) Then
                            info.Exists = True
                            info.Name = name
                            info.NotNull = reader.GetInt32(3) <> 0
                            info.DefaultValue = reader.GetValue(4)
                            Exit While
                        End If
                    End While
                End Using
            End Using
            Return info
        End Function

        <Fact>
        Public Async Function NullableColumn_AddsAsNullable() As Task
            Using conn = Await OpenInMemoryAsync()
                Await DebugTradeDbContext.AddColumnIfMissingAsync(conn, "T", "MaybeReal", "REAL")

                Dim info = Await GetColumnAsync(conn, "T", "MaybeReal")
                Assert.True(info.Exists)
                Assert.False(info.NotNull)
                Assert.True(info.DefaultValue Is DBNull.Value OrElse info.DefaultValue Is Nothing)

                Using ins = conn.CreateCommand()
                    ins.CommandText = "INSERT INTO T (Id) VALUES (1)"
                    Await ins.ExecuteNonQueryAsync()
                End Using
                Using sel = conn.CreateCommand()
                    sel.CommandText = "SELECT MaybeReal FROM T WHERE Id = 1"
                    Dim result = Await sel.ExecuteScalarAsync()
                    Assert.True(result Is DBNull.Value OrElse result Is Nothing)
                End Using
            End Using
        End Function

        <Fact>
        Public Async Function NotNullColumnWithDefault_AddsWithDefault() As Task
            Using conn = Await OpenInMemoryAsync()
                Await DebugTradeDbContext.AddColumnIfMissingAsync(conn, "T", "AcctId", "INTEGER NOT NULL DEFAULT 0")

                Dim info = Await GetColumnAsync(conn, "T", "AcctId")
                Assert.True(info.Exists)
                Assert.True(info.NotNull)
                Assert.Equal("0", Convert.ToString(info.DefaultValue))

                Using ins = conn.CreateCommand()
                    ins.CommandText = "INSERT INTO T (Id) VALUES (1)"
                    Await ins.ExecuteNonQueryAsync()
                End Using
                Using sel = conn.CreateCommand()
                    sel.CommandText = "SELECT AcctId FROM T WHERE Id = 1"
                    Dim result = Await sel.ExecuteScalarAsync()
                    Assert.Equal(0L, Convert.ToInt64(result))
                End Using
            End Using
        End Function

        <Fact>
        Public Async Function RepeatedCall_IsIdempotent() As Task
            Using conn = Await OpenInMemoryAsync()
                Await DebugTradeDbContext.AddColumnIfMissingAsync(conn, "T", "AcctId", "INTEGER NOT NULL DEFAULT 0")
                ' Second call should be a no-op (no duplicate-column exception).
                Await DebugTradeDbContext.AddColumnIfMissingAsync(conn, "T", "AcctId", "INTEGER NOT NULL DEFAULT 0")

                Dim count = 0
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "PRAGMA table_info(T)"
                    Using reader = Await cmd.ExecuteReaderAsync()
                        While Await reader.ReadAsync()
                            If String.Equals(reader.GetString(1), "AcctId", StringComparison.OrdinalIgnoreCase) Then
                                count += 1
                            End If
                        End While
                    End Using
                End Using
                Assert.Equal(1, count)
            End Using
        End Function

    End Class

End Namespace
