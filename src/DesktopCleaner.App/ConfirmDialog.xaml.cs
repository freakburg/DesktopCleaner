using System.Windows;
using System.Windows.Media;

namespace DesktopCleaner.App;

public enum ConfirmKind
{
    /// <summary>Files go to the Recycle Bin and can be put back.</summary>
    Recycle,
    /// <summary>Gone for good (regenerable caches).</summary>
    Permanent,
    /// <summary>Not a deletion: files stay in the cloud.</summary>
    OnlineOnly,
}

/// <summary>
/// The confirmation shown before anything is removed. It says exactly what will happen, shows a preview of the files,
/// and makes Cancel the default button so pressing Enter by accident never deletes anything.
/// </summary>
public partial class ConfirmDialog : Window
{
    private const int PreviewCount = 6;

    private ConfirmDialog(string headline, long count, long bytes, IReadOnlyList<string> items, ConfirmKind kind, string confirmText)
    {
        InitializeComponent();
        HeadlineText.Text = headline;
        SummaryText.Text = bytes > 0 ? $"{count:N0} {(count == 1 ? "item" : "items")}  -  {Format.Bytes(bytes)}" : $"{count:N0} {(count == 1 ? "item" : "items")}";
        ConfirmBtn.Content = confirmText;

        var preview = items.Take(PreviewCount).Select(Describe).ToList();
        if (items.Count > PreviewCount) preview.Add(new Line($"... and {items.Count - PreviewCount:N0} more", "", ""));
        ItemList.ItemsSource = preview;
        ListBox.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var (accent, glyph, note) = kind switch
        {
            ConfirmKind.Permanent => ("#C0392B", "\uE7BA", "This cannot be undone. These files are deleted for good, not sent to the Recycle Bin. The apps rebuild them automatically, so nothing you made is lost."),
            ConfirmKind.OnlineOnly => ("#2E86C1", "\uE753", "Nothing is deleted. The files stay safe in OneDrive and download again when you open them. You need an internet connection to open them later."),
            _ => ("#C0392B", "\uE74D", "These go to the Recycle Bin, so you can get them back. Open the History tab and press Put back, or restore them from the Recycle Bin itself."),
        };
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(accent)!;
        IconCircle.Background = brush;
        IconText.Text = glyph;
        ConfirmBtn.Background = brush;
        ConfirmBtn.BorderBrush = brush;
        NoteText.Text = note;
        NoteBox.Background = new SolidColorBrush(Color.FromArgb(34, brush.Color.R, brush.Color.G, brush.Color.B));
        NoteBox.BorderThickness = new Thickness(0);
        CancelBtn.Focus();
    }

    /// <summary>One row of the preview: the file name first, with the folder it lives in beside it, and the full path as a tooltip.</summary>
    public sealed record Line(string Name, string Where, string Full);

    private static Line Describe(string item)
    {
        if (!item.Contains('\\')) return new Line(item, "", item);
        var name = System.IO.Path.GetFileName(item.TrimEnd('\\'));
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.TrimEnd('\\')) ?? "");
        return new Line(name.Length > 0 ? name : item, parent.Length > 0 ? $"   in {parent}" : "", item);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Shows the dialog over <paramref name="owner"/>. Returns true only if the user chose the confirm button.</summary>
    public static bool Ask(Window owner, string headline, long count, long bytes, IReadOnlyList<string> items, ConfirmKind kind, string confirmText) =>
        new ConfirmDialog(headline, count, bytes, items, kind, confirmText) { Owner = owner }.ShowDialog() == true;
}

