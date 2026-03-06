using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCopy.Models;

namespace DbCopy.ViewModels;

public enum RowKind
{
    Schema,
    TypeGroup,
    Object,
    Index
}

public abstract class DisplayRow : ObservableObject
{
    public abstract RowKind Kind { get; }
}

public partial class SchemaRow : DisplayRow
{
    public override RowKind Kind => RowKind.Schema;
    public required string SchemaName { get; init; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded = true;

    public Action<SchemaRow>? CheckChanged { get; set; }
    public Action<SchemaRow>? ExpandToggled { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckChanged?.Invoke(this);

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        ExpandToggled?.Invoke(this);
    }

    public string ExpandIcon => IsExpanded ? "▾" : "▸";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));
}

public partial class TypeGroupRow : DisplayRow
{
    public override RowKind Kind => RowKind.TypeGroup;
    public required string SchemaName { get; init; }
    public required DbObjectType ObjectType { get; init; }
    public required string TypeLabel { get; init; }
    public required int Count { get; init; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isExpanded = true;

    public Action<TypeGroupRow>? CheckChanged { get; set; }
    public Action<TypeGroupRow>? ExpandToggled { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckChanged?.Invoke(this);

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        ExpandToggled?.Invoke(this);
    }

    public string ExpandIcon => IsExpanded ? "▾" : "▸";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));
}

public partial class ObjectRow : DisplayRow
{
    public override RowKind Kind => RowKind.Object;
    public required string SchemaName { get; init; }
    public required DbObjectType ObjectType { get; init; }
    public required int OriginalIndex { get; init; }
    public required SyncStatus SyncItem { get; init; }
    public required bool CanCheck { get; init; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _syncStatusText = "準備就緒";
    [ObservableProperty] private string _syncStatusColor = "Gray";
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isSuccess;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private long? _displayTargetRowCount;
    [ObservableProperty] private string? _errorMessage;

    public Action? SelectionChanged { get; set; }
    partial void OnIsCheckedChanged(bool value) => SelectionChanged?.Invoke();

    public string ObjectName => SyncItem.SourceObject.Name;
    public long? SourceRowCount => SyncItem.SourceRowCount;
    public bool IsTable => SyncItem.SourceObject.Type == DbObjectType.Table;

    public string DestStatusText => SyncItem.ExistsInDestination ? "已存在" : "不存在";
    public string DestStatusColor => SyncItem.ExistsInDestination ? "#92400e" : "#065f46";

    public string TypeBadgeText => SyncItem.SourceObject.Type switch
    {
        DbObjectType.UserDefinedType => "Type",
        DbObjectType.UserDefinedTableType => "Table Type",
        DbObjectType.Sequence => "Sequence",
        DbObjectType.Table => "Table",
        DbObjectType.View => "View",
        DbObjectType.Procedure => "Procedure",
        DbObjectType.Function => "Function",
        _ => "?"
    };

    public string TypeBadgeColor => SyncItem.SourceObject.Type switch
    {
        DbObjectType.UserDefinedType or DbObjectType.UserDefinedTableType => "#0891b2",
        DbObjectType.Sequence => "#ea580c",
        DbObjectType.Table => "#2563eb",
        DbObjectType.View => "#059669",
        DbObjectType.Procedure => "#7c3aed",
        DbObjectType.Function => "#db2777",
        _ => "#6b7280"
    };
}

public partial class IndexRow : DisplayRow
{
    public override RowKind Kind => RowKind.Index;
    public required DbIndex Index { get; init; }
    [ObservableProperty] private bool _isSynced;

    public string DisplayText => $"{Index.Name} ({Index.Columns}){(Index.IsUnique ? " [Unique]" : "")}";
    public string SyncedText => IsSynced ? "已同步" : "未同步";
    public string SyncedColor => IsSynced ? "#059669" : "#6b7280";

    partial void OnIsSyncedChanged(bool value)
    {
        OnPropertyChanged(nameof(SyncedText));
        OnPropertyChanged(nameof(SyncedColor));
    }
}
