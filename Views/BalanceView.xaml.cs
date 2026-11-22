using System.Windows.Controls;

namespace TokenVault.Views;

public partial class BalanceView : UserControl
{
    private readonly ViewModels.BalanceViewModel _vm;
    private bool _autoQueried;

    public BalanceView(ViewModels.BalanceViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (_autoQueried) return;
            _autoQueried = true;
            await _vm.RefreshAllAsync();
        };
    }
}
