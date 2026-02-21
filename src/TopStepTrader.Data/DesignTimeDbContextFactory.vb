Imports Microsoft.EntityFrameworkCore
Imports Microsoft.EntityFrameworkCore.Design

Namespace TopStepTrader.Data

    ''' <summary>
    ''' Allows "dotnet ef migrations add" to instantiate AppDbContext at design-time
    ''' without needing the WPF startup project.
    ''' </summary>
    Public Class DesignTimeDbContextFactory
        Implements IDesignTimeDbContextFactory(Of AppDbContext)

        Public Function CreateDbContext(args As String()) As AppDbContext _
            Implements IDesignTimeDbContextFactory(Of AppDbContext).CreateDbContext

            Dim optionsBuilder As New DbContextOptionsBuilder(Of AppDbContext)()
            optionsBuilder.UseSqlServer(
                "Server=localhost;Database=TopStepTraderDb;" &
                "Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True")

            Return New AppDbContext(optionsBuilder.Options)
        End Function

    End Class

End Namespace
