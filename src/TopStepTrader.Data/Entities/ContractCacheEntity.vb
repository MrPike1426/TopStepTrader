Imports System.ComponentModel.DataAnnotations
Imports System.ComponentModel.DataAnnotations.Schema

Namespace TopStepTrader.Data.Entities

    ''' <summary>
    ''' One row per root symbol — stores the live ProjectX contract ID resolved on the
    ''' last daily refresh.  Keyed on RootSymbol (e.g. "MES", "MCLE", "MGC").
    ''' </summary>
    <Table("ContractCache")>
    Public Class ContractCacheEntity

        ''' <summary>CME root symbol, e.g. "MES", "MCLE", "MGC". Primary key.</summary>
        <Key>
        <MaxLength(20)>
        Public Property RootSymbol As String = String.Empty

        ''' <summary>Live ProjectX contract ID, e.g. "CON.F.US.MES.U26".</summary>
        <Required>
        <MaxLength(60)>
        Public Property ContractId As String = String.Empty

        ''' <summary>ISO-8601 date (yyyy-MM-dd) when this row was last refreshed from the API.</summary>
        <Required>
        <MaxLength(10)>
        Public Property LastUpdated As String = String.Empty

    End Class

End Namespace