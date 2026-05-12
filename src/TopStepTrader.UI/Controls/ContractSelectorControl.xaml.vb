Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Threading
Imports Microsoft.Extensions.DependencyInjection
Imports TopStepTrader.Core.Trading

Namespace TopStepTrader.UI.Controls

    ''' <summary>
    ''' Reusable instrument selector for AI Trade and Test Trade pages.
    ''' Shows 6 TopStepX favourites at the top; supports live API search for other instruments.
    '''
    ''' ContractId  DP → ticker symbol string, e.g. "GOLD"  (bound to VM string properties)
    ''' InstrumentId DP → numeric instrument ID, e.g. 18         (bound to VM Integer properties)
    ''' </summary>
    Partial Public Class ContractSelectorControl
        Inherits UserControl

        ' ── Inner display type ───────────────────────────────────────────────
        Public Class InstrumentItem
            Public Property InstrumentId As Integer
            Public Property Symbol As String = String.Empty
            Public Property DisplayName As String = String.Empty
            Public Property IsFavourite As Boolean
            Public Property IsHeader As Boolean

            Public ReadOnly Property Display As String
                Get
                    If IsHeader Then Return DisplayName
                    If IsFavourite Then Return $"?  {DisplayName}"
                    Return $"     {DisplayName}"
                End Get
            End Property

            Public Overrides Function ToString() As String
                Return If(IsHeader, "", $"{DisplayName}  [{Symbol}]")
            End Function
        End Class

        ' ── Shared favourites (built once) ───────────────────────────────────
        Private Shared ReadOnly _favouriteItems As IReadOnlyList(Of InstrumentItem) =
            FavouriteContracts.GetDefaults() _
                .Select(Function(f) New InstrumentItem With {
                    .InstrumentId = 0,
                    .Symbol = f.PxContractId,
                    .DisplayName = f.Name,
                    .IsFavourite = True
                }).ToList()

        Private Shared ReadOnly _headerFavourites As New InstrumentItem With {
            .IsHeader = True, .DisplayName = "-- Favourites ------------------"}
        Private Shared ReadOnly _headerResults As New InstrumentItem With {
            .IsHeader = True, .DisplayName = "-- Search Results ---------------"}

        ' ── Instance state ───────────────────────────────────────────────────
        Private ReadOnly _items As New System.Collections.ObjectModel.ObservableCollection(Of InstrumentItem)
        Private ReadOnly _searchTimer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(320)}
        Private _isUpdating As Boolean = False
        Private _lastSearchText As String = String.Empty

        ' ── Constructor ─────────────────────────────────────────────────────
        Public Sub New()
            InitializeComponent()
            AddHandler _searchTimer.Tick, AddressOf OnSearchTimerTick

            ' Populate default list: header + 6 favourites
            _items.Add(_headerFavourites)
            For Each f In _favouriteItems
                _items.Add(f)
            Next

            ContractComboBox.ItemsSource = _items
        End Sub

        ' ── Loaded — hook into the editable TextBox ──────────────────────────
        Private Sub OnLoaded(sender As Object, e As RoutedEventArgs)
            ContractComboBox.ApplyTemplate()
            Dim editBox = TryCast(
                ContractComboBox.Template.FindName("PART_EditableTextBox", ContractComboBox),
                System.Windows.Controls.TextBox)
            If editBox IsNot Nothing Then
                AddHandler editBox.TextChanged, AddressOf OnEditTextChanged
            End If
        End Sub

        ' ── ContractId DP (String — symbol, e.g. "GOLD") ─────────────────────
        Public Property ContractId As String
            Get
                Return CStr(GetValue(ContractIdProperty))
            End Get
            Set(v As String)
                SetValue(ContractIdProperty, v)
            End Set
        End Property

        Public Shared ReadOnly ContractIdProperty As DependencyProperty =
            DependencyProperty.Register(NameOf(ContractId), GetType(String),
                GetType(ContractSelectorControl),
                New PropertyMetadata(String.Empty, AddressOf OnContractIdChanged))

        Private Shared Sub OnContractIdChanged(d As DependencyObject, e As DependencyPropertyChangedEventArgs)
            Dim ctl = TryCast(d, ContractSelectorControl)
            If ctl Is Nothing OrElse ctl._isUpdating Then Return
            ctl.SelectBySymbol(TryCast(e.NewValue, String))
        End Sub

        ' ── InstrumentId DP (Integer — numeric instrument ID, e.g. 18) ────────────
        Public Property InstrumentId As Integer
            Get
                Return CInt(GetValue(InstrumentIdProperty))
            End Get
            Set(v As Integer)
                SetValue(InstrumentIdProperty, v)
            End Set
        End Property

        Public Shared ReadOnly InstrumentIdProperty As DependencyProperty =
            DependencyProperty.Register(NameOf(InstrumentId), GetType(Integer),
                GetType(ContractSelectorControl), New PropertyMetadata(0))

        ' ── User typed in the search box ─────────────────────────────────────
        Private Sub OnEditTextChanged(sender As Object, e As TextChangedEventArgs)
            If _isUpdating Then Return
            _searchTimer.Stop()
            _searchTimer.Tag = TryCast(sender, System.Windows.Controls.TextBox)?.Text
            _searchTimer.Start()
        End Sub

        Private Sub OnSearchTimerTick(sender As Object, e As EventArgs)
            _searchTimer.Stop()
            Dim text = If(TryCast(_searchTimer.Tag, String)?.Trim(), String.Empty)
            If text = _lastSearchText Then Return
            _lastSearchText = text

            If String.IsNullOrWhiteSpace(text) OrElse text.Length < 2 Then
                ResetToFavourites()
                Return
            End If

            ' Check if the typed text exactly matches a favourite symbol
            Dim exactFav = _favouriteItems.FirstOrDefault(
                Function(f) String.Equals(f.Symbol, text, StringComparison.OrdinalIgnoreCase))
            If exactFav IsNot Nothing Then
                ApplySelection(exactFav)
                Return
            End If

        End Sub

        ' ── Selection changed (user clicked an item) ─────────────────────────
        Private Sub OnSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            If _isUpdating Then Return
            Dim item = TryCast(ContractComboBox.SelectedItem, InstrumentItem)
            If item Is Nothing OrElse item.IsHeader Then Return
            ApplySelection(item)
        End Sub

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Sub SelectBySymbol(symbol As String)
            If String.IsNullOrWhiteSpace(symbol) Then
                _isUpdating = True
                Try
                    ContractComboBox.SelectedItem = Nothing
                    ContractComboBox.Text = String.Empty
                Finally
                    _isUpdating = False
                End Try
                Return
            End If

            Dim match = _items.FirstOrDefault(
                Function(i) Not i.IsHeader AndAlso
                    String.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            If match IsNot Nothing Then
                _isUpdating = True
                Try
                    ContractComboBox.SelectedItem = match
                    ContractComboBox.Text = match.ToString()
                    _searchTimer.Stop()
                    _lastSearchText = match.ToString()
                Finally
                    _isUpdating = False
                End Try
            End If
        End Sub

        Private Sub ApplySelection(item As InstrumentItem)
            _isUpdating = True
            Try
                ContractComboBox.SelectedItem = item
                ContractComboBox.Text = item.ToString()
                ContractComboBox.IsDropDownOpen = False
                ContractId = item.Symbol
                InstrumentId = item.InstrumentId
                ' Stop the search timer and anchor _lastSearchText to the display text.
                ' WPF fires a deferred TextChanged after SelectedItem is set; without this
                ' the timer fires, treats the display text as a search query, finds nothing,
                ' calls RebuildList and blanks the combobox.
                _searchTimer.Stop()
                _lastSearchText = item.ToString()
            Finally
                _isUpdating = False
            End Try
        End Sub

        Private Sub ResetToFavourites()
            RebuildList(Nothing)
        End Sub

        Private Sub RebuildList(searchResults As List(Of InstrumentItem))
            _isUpdating = True
            Try
                Dim selected = TryCast(ContractComboBox.SelectedItem, InstrumentItem)
                _items.Clear()
                _items.Add(_headerFavourites)
                For Each f In _favouriteItems
                    _items.Add(f)
                Next
                If searchResults IsNot Nothing AndAlso searchResults.Count > 0 Then
                    _items.Add(_headerResults)
                    For Each r In searchResults
                        _items.Add(r)
                    Next
                End If
                If selected IsNot Nothing Then
                    Dim reselect = _items.FirstOrDefault(Function(i) i.Symbol = selected.Symbol)
                    If reselect IsNot Nothing Then ContractComboBox.SelectedItem = reselect
                End If
            Finally
                _isUpdating = False
            End Try
        End Sub

        End Class

End Namespace
