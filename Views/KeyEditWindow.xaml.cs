using System.Collections.ObjectModel;
using System.Windows;
using TokenVault.Models;
using TokenVault.Services;

namespace TokenVault.Views;

public partial class KeyEditWindow : Window
{
    private readonly KeyEntry? _original;

    public KeyEntry? Result { get; private set; }

    private sealed record CategoryItem(string Display, string Value);

    private static readonly CategoryItem[] Categories =
    {
        new(KeyCategories.DisplayName(KeyCategories.Account), KeyCategories.Account),
        new(KeyCategories.DisplayName(KeyCategories.ApiKey), KeyCategories.ApiKey),
        new(KeyCategories.DisplayName(KeyCategories.Database), KeyCategories.Database),
        new(KeyCategories.DisplayName(KeyCategories.MimoPlan), KeyCategories.MimoPlan),
        new(KeyCategories.DisplayName(KeyCategories.MimoSk), KeyCategories.MimoSk),
        new(KeyCategories.DisplayName(KeyCategories.DeepSeek), KeyCategories.DeepSeek),
        new(KeyCategories.DisplayName(KeyCategories.Alibaba), KeyCategories.Alibaba),
        new(KeyCategories.DisplayName(KeyCategories.Other), KeyCategories.Other),
    };

    private readonly ObservableCollection<CustomField> _fields = new();

    public KeyEditWindow(KeyEntry? original)
    {
        InitializeComponent();
        _original = original;
        CategoryBox.ItemsSource = Categories;
        FieldsHost.ItemsSource = _fields;

        if (original != null)
        {
            Title = "修改凭据";
            TitleText.Text = "修改凭据";
            NameBox.Text = original.Name;
            KeyBox.Text = original.ApiKey;
            KeyPwBox.Password = original.ApiKey;
            PwdBox.Password = original.Password;
            PwdBoxVisible.Text = original.Password;
            UsernameBox.Text = original.Username;
            BaseUrlBox.Text = original.BaseUrl;
            NoteBox.Text = original.Note;
            foreach (var f in original.Fields) _fields.Add(new CustomField { Name = f.Name, Value = f.Value });
            CategoryBox.SelectedIndex = Math.Max(0, Array.FindIndex(Categories, c => c.Value == original.Category));
        }
        else
        {
            Title = "添加凭据";
            TitleText.Text = "添加凭据";
            CategoryBox.SelectedIndex = 0;
        }
    }

    private string KeyValue => ShowKeyCheck.IsChecked == true ? KeyBox.Text : KeyPwBox.Password;
    private string PwdValue => ShowKeyCheck.IsChecked == true ? PwdBoxVisible.Text : PwdBox.Password;

    private void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            KeyBox.Text = KeyPwBox.Password;
            PwdBoxVisible.Text = PwdBox.Password;
            KeyBox.Visibility = Visibility.Visible;
            KeyPwBox.Visibility = Visibility.Collapsed;
            PwdBoxVisible.Visibility = Visibility.Visible;
            PwdBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            KeyPwBox.Password = KeyBox.Text;
            PwdBox.Password = PwdBoxVisible.Text;
            KeyBox.Visibility = Visibility.Collapsed;
            KeyPwBox.Visibility = Visibility.Visible;
            PwdBoxVisible.Visibility = Visibility.Collapsed;
            PwdBox.Visibility = Visibility.Visible;
        }
    }

    private void AddField_Click(object sender, RoutedEventArgs e) => _fields.Add(new CustomField());

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is CustomField field) _fields.Remove(field);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyValue.Trim();
        if (key.Length == 0)
        {
            TestStatus.Text = "该类别需要填写 API Key 才能测试";
            return;
        }
        var cat = ((CategoryItem)CategoryBox.SelectedItem).Value;
        TestBtn.IsEnabled = false;
        TestStatus.Text = "测试中…";
        var (ok, message) = await new BalanceService().TestKeyAsync(cat, key, BaseUrlBox.Text.Trim());
        TestStatus.Text = message;
        TestStatus.Foreground = TryFindResource(ok ? "OkBrush" : "DangerBrush") as System.Windows.Media.Brush;
        TestBtn.IsEnabled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请填写名称。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var cat = ((CategoryItem)CategoryBox.SelectedItem).Value;
        var apiKey = KeyValue.Trim();
        var password = PwdValue;
        if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(password))
        {
            var r = MessageBox.Show(this, "该条目既没有密码也没有 API Key。\n\n仍要保存吗？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        if ((cat == KeyCategories.MimoPlan || cat == KeyCategories.DeepSeek
             || cat == KeyCategories.MimoSk || cat == KeyCategories.Alibaba)
            && apiKey.Length == 0)
        {
            MessageBox.Show(this, "该类别用于“余量查询”，必须填写 API Key。", "保存失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = _original?.Clone() ?? new KeyEntry();
        Result.Name = name;
        Result.Category = cat;
        Result.ApiKey = apiKey;
        Result.Password = password;
        Result.Username = UsernameBox.Text.Trim();
        Result.BaseUrl = BaseUrlBox.Text.Trim();
        Result.Note = NoteBox.Text.Trim();
        Result.Fields = _fields
            .Where(f => f.Name.Trim().Length > 0 || f.Value.Length > 0)
            .Select(f => new CustomField { Name = f.Name.Trim(), Value = f.Value })
            .ToList();
        Result.UpdatedAt = DateTime.Now;
        if (_original == null) Result.CreatedAt = DateTime.Now;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
