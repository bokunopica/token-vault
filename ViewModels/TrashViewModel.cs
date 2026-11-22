using System.Collections.ObjectModel;
using System.Windows;
using TokenVault.Infrastructure;
using TokenVault.Models;
using TokenVault.Services;

namespace TokenVault.ViewModels;

public class TrashRow : ObservableObject
{
    public TrashRow(TrashItem item) => Item = item;

    public TrashItem Item { get; }
    public string Name => Item.Entry.Name;
    public string KindDisplay => Item.KindDisplay;
    public string CategoryDisplay => KeyCategories.DisplayName(Item.Entry.Category);
    public string EnteredAtDisplay => Item.EnteredAt.ToString("yyyy-MM-dd HH:mm");
    public string PurgeAtDisplay => Item.PurgeAt.ToString("yyyy-MM-dd HH:mm");
    public string DaysLeftDisplay => Item.IsExpired ? "已到期" : $"剩 {Math.Ceiling(Item.DaysLeft)} 天";
}

public class TrashViewModel : ObservableObject
{
    private readonly VaultStore _vault;
    private TrashRow? _selected;

    public TrashViewModel(VaultStore vault)
    {
        _vault = vault;
        Reload();
    }

    public ObservableCollection<TrashRow> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public string Hint => "临时区条目在进入 7 天后自动彻底删除；也可手动提前彻底删除（需二次确认）。";

    public TrashRow? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public void Reload()
    {
        _vault.PurgeExpired();
        Rows.Clear();
        foreach (var t in _vault.Data.Trash.OrderBy(t => t.PurgeAt))
            Rows.Add(new TrashRow(t));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>恢复：回到密钥列表（同 Id 当前版本会作为旧版本进临时区）。</summary>
    public void Restore(TrashRow row)
    {
        var confirm = new Views.ConfirmWindow(
            "确认恢复",
            $"将密钥「{row.Name}」（{row.KindDisplay}）恢复到密钥列表？\n\n若列表中已有同一条密钥，其当前版本会作为旧版本放入临时区。",
            "确认恢复") { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;

        _vault.Restore(row.Item.TrashId);
        Reload();
    }

    /// <summary>立即彻底删除 — 二次确认且需手动输入名称。</summary>
    public void PurgeNow(TrashRow row)
    {
        var confirm = new Views.ConfirmWindow(
            "彻底删除（不可恢复）",
            $"将彻底删除「{row.Name}」（{row.KindDisplay}），此操作不可恢复！\n\n原本自动删除时间：{row.PurgeAtDisplay}",
            "彻底删除",
            typedConfirmation: row.Name) { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;

        _vault.PurgeNow(row.Item.TrashId);
        Reload();
    }
}
