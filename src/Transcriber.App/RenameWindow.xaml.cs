using System.Windows;
using Wpf.Ui.Controls;

namespace Transcriber.App;

public partial class RenameWindow : FluentWindow
{
    public RenameWindow(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>What was typed, after Rename. Empty clears the title.</summary>
    public string NewTitle { get; private set; } = "";

    private void OnSave(object sender, RoutedEventArgs e)
    {
        NewTitle = TitleBox.Text;
        DialogResult = true;
    }
}
