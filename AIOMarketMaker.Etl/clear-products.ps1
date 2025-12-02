[System.Reflection.Assembly]::LoadFrom("$PSScriptRoot\bin\Debug\net8.0\Microsoft.Data.Sqlite.dll") | Out-Null
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$PSScriptRoot\etl.db")
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = "DELETE FROM Products"
$deleted = $cmd.ExecuteNonQuery()
Write-Host "Deleted $deleted products"
$conn.Close()
