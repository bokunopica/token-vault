using System.Windows;
using System.Windows.Controls;
using TokenVault.ViewModels;

namespace TokenVault.Views;

public partial class TrashView : UserControl
{
    private readonly TrashViewModel _vm;

    public TrashView(TrashViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is TrashRow row) _vm.Restore(row);
    }

    private void Purge_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is TrashRow row) _vm.PurgeNow(row);
    }
}
