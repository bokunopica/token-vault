using System.Windows;

namespace TokenVault.Views;

/// <summary>通用二次确认对话框；可要求手动输入指定文本（用于不可恢复的危险操作）。</summary>
public partial class ConfirmWindow : Window
{
    private readonly string? _typedConfirmation;

    public ConfirmWindow(string title, string message, string confirmText, string? typedConfirmation = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        MessageText.Text = message;
        ConfirmBtn.Content = confirmText;
        _typedConfirmation = typedConfirmation;
        if (typedConfirmation != null)
        {
            TypedPanel.Visibility = Visibility.Visible;
            TypedHint.Text = $"此操作不可恢复。请手动输入「{typedConfirmation}」以确认：";
            ConfirmBtn.IsEnabled = false;
        }
    }

    private void TypedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ConfirmBtn.IsEnabled = TypedBox.Text == _typedConfirmation;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
