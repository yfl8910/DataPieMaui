using System.Data;
using System.Globalization;
using System.Text;

namespace DataPieMaui.Controls;

// A bounded preview: CollectionView virtualizes rows; the outer scroll synchronizes columns and headers.
public sealed class QueryResultsView : ContentView
{
    private readonly CollectionView rows = new() { SelectionMode = SelectionMode.Single };
    private readonly Grid grid = new() { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
    private DataTable? table;
    private int sortColumn = -1;
    private bool descending;
    private List<object?[]> displayed = new();

    public QueryResultsView()
    {
        Content = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = grid };
        AutomationId = "QueryResults";
    }

    public void SetTable(DataTable? value)
    {
        table = value;
        sortColumn = -1;
        descending = false;
        rows.ItemsSource = null;
        grid.Children.Clear();
        displayed.Clear();
        if (value == null || value.Columns.Count == 0) return;
        int count = value.Columns.Count;
        double width = count * 180;
        var header = new HorizontalStackLayout { Spacing = 0, WidthRequest = width };
        for (int i = 0; i < count; i++)
        {
            int column = i;
            var button = new Button { Text = value.Columns[i].ColumnName, WidthRequest = 180, CornerRadius = 0, BackgroundColor = Color.FromArgb("#E3EBEF"), TextColor = Color.FromArgb("#233043") };
            button.Clicked += (_, _) => Sort(column);
            header.Add(button);
        }
        rows.WidthRequest = width;
        rows.ItemTemplate = new DataTemplate(() =>
        {
            var line = new HorizontalStackLayout { Spacing = 0, HeightRequest = 34 };
            for (int i = 0; i < count; i++)
            {
                var label = new Label { WidthRequest = 180, Padding = new Thickness(8, 5), MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, FontSize = 13 };
                label.SetBinding(Label.TextProperty, new Binding($"[{i}]", converter: new CellConverter()));
                line.Add(label);
            }
            return line;
        });
        grid.WidthRequest = width;
        grid.Add(header);
        grid.Add(rows, 0, 1);
        displayed = value.Rows.Cast<DataRow>().Select(row => row.ItemArray).ToList();
        rows.ItemsSource = displayed;
    }

    private void Sort(int column)
    {
        if (table == null) return;
        descending = sortColumn == column && !descending;
        sortColumn = column;
        // DataView performs numeric/date sorting using the original column types.
        var name = table.Columns[column].ColumnName.Replace("\\", "\\\\").Replace("]", "\\]");
        try
        {
            using var view = new DataView(table) { Sort = $"[{name}] {(descending ? "DESC" : "ASC")}" };
            displayed = view.Cast<DataRowView>().Select(row => row.Row.ItemArray).ToList();
            rows.ItemsSource = displayed;
        }
        catch (Exception) when (table.Columns[column].DataType == typeof(byte[])) { }
    }

    public string CopyText(bool all)
    {
        if (table == null) return "";
        var output = new StringBuilder();
        output.AppendLine(string.Join('\t', table.Columns.Cast<DataColumn>().Select(c => Escape(c.ColumnName))));
        IEnumerable<object?[]> chosen = all ? displayed : rows.SelectedItem is object?[] selected ? new[] { selected } : Array.Empty<object?[]>();
        foreach (var row in chosen) output.AppendLine(string.Join('\t', row.Select(cell => Escape(CellConverter.Format(cell)))));
        return output.ToString();
    }

    private static string Escape(string text) => text.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    private sealed class CellConverter : IValueConverter
    {
        public static string Format(object? value) => value switch { null or DBNull => "NULL", byte[] bytes => System.Convert.ToHexString(bytes), _ => System.Convert.ToString(value, CultureInfo.CurrentCulture) ?? "" };
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Format(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}


