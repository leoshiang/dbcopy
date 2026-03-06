using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbCopy.Models;
using DbCopy.Services;
using Microsoft.Extensions.Logging;
using Serilog;
using DbType = DbCopy.Models.DbType;

namespace DbCopy.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly string ConnectionsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DbCopy", "connections.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] TypeLabels =
        ["Types", "Table Types", "Sequences", "Tables", "Views", "Procedures", "Functions"];

    private readonly SqlServerService _sqlServer;
    private readonly PostgreSqlService _pgSql;
    private List<SyncStatus> _syncItems = [];
    private List<DisplayRow> _allRows = [];
    private CancellationTokenSource? _syncCts;
    private bool _updatingDropdowns;

    // === Observable Properties ===

    [ObservableProperty] private ObservableCollection<DbConnectionInfo> _connections = [];
    [ObservableProperty] private ObservableCollection<DbConnectionInfo> _sourceOptions = [];
    [ObservableProperty] private ObservableCollection<DbConnectionInfo> _targetOptions = [];
    [ObservableProperty] private DbConnectionInfo? _selectedSource;
    [ObservableProperty] private DbConnectionInfo? _selectedTarget;
    [ObservableProperty] private int _batchSize = 1000;

    [ObservableProperty] private ObservableCollection<DisplayRow> _visibleRows = [];

    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _syncProgress;
    [ObservableProperty] private string _syncProgressText = "";
    [ObservableProperty] private bool _showResultBanner;
    [ObservableProperty] private string _resultBannerText = "";
    [ObservableProperty] private bool _isResultSuccess;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _filteredCount;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty] private int _filterTypeIndex;
    [ObservableProperty] private int _filterDestIndex;
    [ObservableProperty] private int _filterSyncIndex;

    [ObservableProperty] private string _placeholderText = "請選擇連線並點擊「分析與比較」以載入物件。";
    [ObservableProperty] private bool _showPlaceholder = true;

    public string VersionText
    {
        get
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "vunknown" : $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
    }

    // === Constructor ===

    public MainViewModel()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSerilog(Log.Logger));

        _sqlServer = new SqlServerService(loggerFactory.CreateLogger<SqlServerService>());
        _pgSql = new PostgreSqlService(loggerFactory.CreateLogger<PostgreSqlService>());

        LoadConnections();
    }

    private IDbService GetService(DbType type) => type switch
    {
        DbType.SqlServer => _sqlServer,
        DbType.PostgreSql => _pgSql,
        _ => throw new ArgumentException("Unsupported database type")
    };

    // === Property Change Handlers ===

    partial void OnSelectedSourceChanged(DbConnectionInfo? value)
    {
        if (!_updatingDropdowns) RefreshConnectionDropdowns();
    }

    partial void OnSelectedTargetChanged(DbConnectionInfo? value)
    {
        if (!_updatingDropdowns) RefreshConnectionDropdowns();
    }

    partial void OnFilterTypeIndexChanged(int value) => RefreshVisibleRows();
    partial void OnFilterDestIndexChanged(int value) => RefreshVisibleRows();
    partial void OnFilterSyncIndexChanged(int value) => RefreshVisibleRows();

    // === Connection Management ===

    private void LoadConnections()
    {
        try
        {
            if (File.Exists(ConnectionsFilePath))
            {
                var json = File.ReadAllText(ConnectionsFilePath);
                var loaded = JsonSerializer.Deserialize<List<DbConnectionInfo>>(json, JsonOptions);
                if (loaded != null)
                    Connections = new ObservableCollection<DbConnectionInfo>(loaded);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load connections");
        }

        RefreshConnectionDropdowns();
    }

    public void SaveConnections()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConnectionsFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConnectionsFilePath,
                JsonSerializer.Serialize(Connections.ToList(), JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save connections");
        }
    }

    public void RefreshConnectionDropdowns()
    {
        _updatingDropdowns = true;
        try
        {
            var prevSourceId = SelectedSource?.Id;
            var prevTargetId = SelectedTarget?.Id;

            SourceOptions = new ObservableCollection<DbConnectionInfo>(
                Connections.Where(c => c.Id != prevTargetId));
            TargetOptions = new ObservableCollection<DbConnectionInfo>(
                Connections.Where(c => c.Id != prevSourceId && !c.ReadOnly));

            SelectedSource = SourceOptions.FirstOrDefault(c => c.Id == prevSourceId);
            SelectedTarget = TargetOptions.FirstOrDefault(c => c.Id == prevTargetId);
        }
        finally
        {
            _updatingDropdowns = false;
        }
    }

    public async Task<bool> TestConnectionAsync(DbConnectionInfo conn)
    {
        var service = GetService(conn.Type);
        return await service.TestConnectionAsync(conn.ConnectionString);
    }

    public void ExportConnections(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(Connections.ToList(), JsonOptions));
    }

    public void ImportConnections(string path)
    {
        var json = File.ReadAllText(path);
        var imported = JsonSerializer.Deserialize<List<DbConnectionInfo>>(json, JsonOptions);
        if (imported == null) return;

        var map = new Dictionary<string, DbConnectionInfo>();
        foreach (var c in Connections)
            map[$"{(int)c.Type}|{c.Name.Trim()}"] = c;

        int added = 0, updated = 0;
        foreach (var c in imported)
        {
            if (string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.ConnectionString)) continue;
            var key = $"{(int)c.Type}|{c.Name.Trim()}";
            if (map.TryGetValue(key, out var existing))
            {
                existing.ConnectionString = c.ConnectionString;
                existing.Type = c.Type;
                existing.ReadOnly = c.ReadOnly;
                updated++;
            }
            else
            {
                if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString();
                Connections.Add(c);
                map[key] = c;
                added++;
            }
        }

        SaveConnections();
        RefreshConnectionDropdowns();
    }

    // === Compare ===

    [RelayCommand]
    private async Task CompareAsync()
    {
        if (SelectedSource == null || SelectedTarget == null) return;
        if (SelectedSource.Type != SelectedTarget.Type) return;

        IsComparing = true;
        ShowResultBanner = false;
        ShowProgress = false;
        ShowPlaceholder = false;

        try
        {
            var sourceService = GetService(SelectedSource.Type);
            var targetService = GetService(SelectedTarget.Type);

            var sourceObjects = await sourceService.GetDbObjectsAsync(SelectedSource.ConnectionString);
            var syncStatuses = new List<SyncStatus>();

            foreach (var obj in sourceObjects)
            {
                var existsTask = targetService.CheckObjectExistsAsync(SelectedTarget.ConnectionString, obj);
                var depsTask = sourceService.GetDependenciesAsync(SelectedSource.ConnectionString, obj);
                await Task.WhenAll(existsTask, depsTask);

                long? sourceRows = null;
                long? targetRows = null;

                if (obj.Type == DbObjectType.Table)
                {
                    var srcRowsTask = SafeGetRowCount(sourceService, SelectedSource.ConnectionString, obj);
                    var srcIdxTask = SafeGetIndexes(sourceService, SelectedSource.ConnectionString, obj);
                    var tgtRowsTask = existsTask.Result
                        ? SafeGetRowCount(targetService, SelectedTarget.ConnectionString, obj)
                        : Task.FromResult<long?>(null);
                    var tgtIdxTask = existsTask.Result
                        ? SafeGetIndexes(targetService, SelectedTarget.ConnectionString, obj)
                        : Task.FromResult(new List<DbIndex>());

                    await Task.WhenAll(srcRowsTask, srcIdxTask, tgtRowsTask, tgtIdxTask);

                    sourceRows = srcRowsTask.Result;
                    targetRows = tgtRowsTask.Result;

                    foreach (var sIdx in srcIdxTask.Result)
                    {
                        sIdx.ExistsInDestination = tgtIdxTask.Result.Any(t =>
                            t.Name.Equals(sIdx.Name, StringComparison.OrdinalIgnoreCase));
                    }

                    obj.Indexes = srcIdxTask.Result;
                }

                syncStatuses.Add(new SyncStatus
                {
                    SourceObject = obj,
                    ExistsInDestination = existsTask.Result,
                    Status = existsTask.Result ? "Exists" : "Pending",
                    Dependencies = depsTask.Result,
                    SourceRowCount = sourceRows,
                    TargetRowCount = targetRows
                });
            }

            _syncItems = syncStatuses;
            TotalCount = _syncItems.Count;
            BuildDisplayRows();
            RefreshVisibleRows();
        }
        catch (Exception ex)
        {
            ShowPlaceholder = true;
            PlaceholderText = $"連線或讀取資料失敗: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    // === Sync ===

    [RelayCommand]
    private async Task SyncAsync()
    {
        var selectedRows = _allRows.OfType<ObjectRow>().Where(r => r.IsChecked).ToList();
        if (selectedRows.Count == 0 || SelectedSource == null || SelectedTarget == null) return;

        IsSyncing = true;
        ShowResultBanner = false;
        ShowProgress = true;
        SyncProgress = 0;
        _syncCts = new CancellationTokenSource();

        var source = SelectedSource;
        var target = SelectedTarget;
        var sourceService = GetService(source.Type);
        var targetService = GetService(target.Type);
        var batchSize = BatchSize;

        try
        {
            var selectedIndices = selectedRows.Select(r => r.OriginalIndex).ToList();
            var sortedIndices = TopologicalSort(selectedIndices);

            var sequenceIndices = sortedIndices
                .Where(i => _syncItems[i].SourceObject.Type == DbObjectType.Sequence).ToList();
            var typeIndices = sortedIndices
                .Where(i => _syncItems[i].SourceObject.Type is DbObjectType.UserDefinedType
                    or DbObjectType.UserDefinedTableType).ToList();
            var tableIndices = sortedIndices
                .Where(i => _syncItems[i].SourceObject.Type == DbObjectType.Table).ToList();
            var otherIndices = sortedIndices
                .Where(i => (int)_syncItems[i].SourceObject.Type > (int)DbObjectType.Table).ToList();

            var queue = new List<(int Index, int Phase, string Label)>();

            foreach (var i in sequenceIndices) queue.Add((i, 1, "建立序列中..."));
            foreach (var i in typeIndices) queue.Add((i, 1, "建立類型中..."));
            foreach (var i in tableIndices) queue.Add((i, 1, "建立結構中..."));
            foreach (var i in tableIndices) queue.Add((i, 2, "複製資料中..."));
            foreach (var i in otherIndices) queue.Add((i, 1, "同步中..."));
            foreach (var i in tableIndices) queue.Add((i, 3, "建立索引中..."));
            foreach (var i in tableIndices) queue.Add((i, 4, "建立外鍵中..."));

            int completed = 0;
            int total = queue.Count;

            for (int qi = 0; qi < queue.Count; qi++)
            {
                if (_syncCts.IsCancellationRequested) break;

                var task = queue[qi];
                var item = _syncItems[task.Index];
                var row = _allRows.OfType<ObjectRow>().First(r => r.OriginalIndex == task.Index);
                var isLastTask = !queue.Skip(qi + 1).Any(t => t.Index == task.Index);

                row.IsSyncing = true;
                row.IsError = false;
                row.SyncStatusText = task.Label;

                try
                {
                    await targetService.EnsureSchemaExistsAsync(
                        target.ConnectionString, item.SourceObject.Schema);

                    if (task.Phase is 0 or 1)
                    {
                        var exists = await targetService.CheckObjectExistsAsync(
                            target.ConnectionString, item.SourceObject);
                        if (exists)
                        {
                            row.IsSyncing = false;
                            row.SyncStatusText = "已存在，跳過";
                            completed++;
                            UpdateProgress(completed, total);
                            continue;
                        }
                    }

                    await sourceService.CopyObjectAsync(
                        source.ConnectionString, target.ConnectionString,
                        item.SourceObject, task.Phase, batchSize);

                    if (isLastTask)
                    {
                        item.Status = "Success";
                        row.IsSuccess = true;
                        row.IsSyncing = false;
                        row.SyncStatusText = "成功";
                        row.SyncStatusColor = "Green";
                        row.IsChecked = false;

                        if (item.SourceObject.Type == DbObjectType.Table)
                        {
                            item.TargetRowCount = item.SourceRowCount;
                            row.DisplayTargetRowCount = item.SourceRowCount;

                            foreach (var idx in item.SourceObject.Indexes)
                                idx.ExistsInDestination = true;
                            foreach (var idxRow in _allRows.OfType<IndexRow>())
                            {
                                if (item.SourceObject.Indexes.Contains(idxRow.Index))
                                    idxRow.IsSynced = true;
                            }
                        }
                    }
                    else
                    {
                        row.SyncStatusText = task.Label.Replace("中...", "完成");
                    }
                }
                catch (Exception ex)
                {
                    item.Status = "Error";
                    item.Message = ex.Message;
                    row.IsError = true;
                    row.IsSyncing = false;
                    row.SyncStatusText = "錯誤";
                    row.SyncStatusColor = "Red";
                    row.ErrorMessage = ex.Message;
                }

                completed++;
                UpdateProgress(completed, total);
            }

            // Show result banner
            var succeeded = selectedIndices.Count(i => _syncItems[i].Status == "Success");
            var failed = selectedIndices.Count(i => _syncItems[i].Status == "Error");

            ShowResultBanner = true;
            if (_syncCts.IsCancellationRequested)
            {
                ResultBannerText = $"同步已中斷。成功: {succeeded} 個，錯誤: {failed} 個。";
                IsResultSuccess = false;
            }
            else if (failed == 0)
            {
                ResultBannerText = $"同步完成！共 {succeeded} 個物件同步成功。";
                IsResultSuccess = true;
            }
            else
            {
                ResultBannerText = $"同步完成。成功: {succeeded} 個，錯誤: {failed} 個。";
                IsResultSuccess = false;
            }
        }
        finally
        {
            IsSyncing = false;
            _syncCts = null;
            UpdateSelectedCount();
        }
    }

    [RelayCommand]
    private void StopSync()
    {
        _syncCts?.Cancel();
    }

    // === Display Tree ===

    private void BuildDisplayRows()
    {
        _allRows.Clear();

        var schemaGroups =
            new SortedDictionary<string,
                SortedDictionary<DbObjectType, List<(SyncStatus Item, int Index)>>>();

        for (int i = 0; i < _syncItems.Count; i++)
        {
            var item = _syncItems[i];
            var schema = item.SourceObject.Schema;
            var type = item.SourceObject.Type;

            if (!schemaGroups.ContainsKey(schema))
                schemaGroups[schema] = new SortedDictionary<DbObjectType, List<(SyncStatus, int)>>();
            if (!schemaGroups[schema].ContainsKey(type))
                schemaGroups[schema][type] = [];

            schemaGroups[schema][type].Add((item, i));
        }

        foreach (var (schema, types) in schemaGroups)
        {
            _allRows.Add(new SchemaRow
            {
                SchemaName = schema,
                CheckChanged = OnSchemaCheckChanged,
                ExpandToggled = _ => RefreshVisibleRows()
            });

            foreach (var (type, items) in types)
            {
                _allRows.Add(new TypeGroupRow
                {
                    SchemaName = schema,
                    ObjectType = type,
                    TypeLabel = TypeLabels[(int)type],
                    Count = items.Count,
                    CheckChanged = OnTypeCheckChanged,
                    ExpandToggled = _ => RefreshVisibleRows()
                });

                foreach (var (item, index) in items)
                {
                    var canCheck = !item.ExistsInDestination;
                    var statusText = item.Status switch
                    {
                        "Pending" => "準備就緒",
                        "Exists" => "不同步",
                        "Success" => "成功",
                        "Error" => "錯誤",
                        _ => item.Status
                    };

                    _allRows.Add(new ObjectRow
                    {
                        SchemaName = schema,
                        ObjectType = type,
                        OriginalIndex = index,
                        SyncItem = item,
                        CanCheck = canCheck,
                        SyncStatusText = statusText,
                        DisplayTargetRowCount = item.TargetRowCount,
                        SelectionChanged = UpdateSelectedCount
                    });

                    foreach (var idx in item.SourceObject.Indexes)
                    {
                        _allRows.Add(new IndexRow
                        {
                            Index = idx,
                            IsSynced = idx.ExistsInDestination
                        });
                    }
                }
            }
        }
    }

    public void RefreshVisibleRows()
    {
        var rows = new List<DisplayRow>();
        var collapsedSchemas = new HashSet<string>();
        var collapsedTypeKeys = new HashSet<string>();

        foreach (var row in _allRows)
        {
            switch (row)
            {
                case SchemaRow sr:
                    rows.Add(sr);
                    if (!sr.IsExpanded) collapsedSchemas.Add(sr.SchemaName);
                    break;

                case TypeGroupRow tg:
                    if (collapsedSchemas.Contains(tg.SchemaName)) continue;
                    if (FilterTypeIndex > 0 && !MatchesTypeFilter(tg.ObjectType)) continue;
                    rows.Add(tg);
                    if (!tg.IsExpanded) collapsedTypeKeys.Add($"{tg.SchemaName}|{tg.ObjectType}");
                    break;

                case ObjectRow or:
                    if (collapsedSchemas.Contains(or.SchemaName)) continue;
                    if (collapsedTypeKeys.Contains($"{or.SchemaName}|{or.ObjectType}")) continue;
                    if (FilterTypeIndex > 0 && !MatchesTypeFilter(or.ObjectType)) continue;
                    if (!MatchesDestFilter(or.SyncItem)) continue;
                    if (!MatchesSyncFilter(or.SyncItem)) continue;
                    rows.Add(or);
                    break;

                case IndexRow:
                    // Show index row only if the preceding row is an ObjectRow or IndexRow
                    if (rows.Count > 0 && rows[^1] is ObjectRow or IndexRow)
                        rows.Add(row);
                    break;
            }
        }

        FilteredCount = rows.OfType<ObjectRow>().Count();
        ShowPlaceholder = rows.Count == 0;
        if (ShowPlaceholder && _syncItems.Count > 0)
            PlaceholderText = "符合篩選條件的物件為零。";

        VisibleRows = new ObservableCollection<DisplayRow>(rows);
        UpdateSelectedCount();
    }

    // === Checkbox Cascading ===

    private void OnSchemaCheckChanged(SchemaRow sr)
    {
        foreach (var tg in _allRows.OfType<TypeGroupRow>().Where(t => t.SchemaName == sr.SchemaName))
        {
            tg.IsChecked = sr.IsChecked;
        }
    }

    private void OnTypeCheckChanged(TypeGroupRow tg)
    {
        foreach (var obj in _allRows.OfType<ObjectRow>()
                     .Where(o => o.SchemaName == tg.SchemaName && o.ObjectType == tg.ObjectType && o.CanCheck))
        {
            obj.IsChecked = tg.IsChecked;
        }
    }

    public void SelectAll(bool isChecked)
    {
        foreach (var row in _allRows)
        {
            switch (row)
            {
                case SchemaRow sr: sr.IsChecked = isChecked; break;
                case TypeGroupRow tg: tg.IsChecked = isChecked; break;
                case ObjectRow { CanCheck: true } or: or.IsChecked = isChecked; break;
            }
        }

        UpdateSelectedCount();
    }

    public void UpdateSelectedCount()
    {
        SelectedCount = _allRows.OfType<ObjectRow>().Count(r => r.IsChecked);
    }

    // === Filters ===

    private bool MatchesTypeFilter(DbObjectType type) => FilterTypeIndex switch
    {
        1 => type == DbObjectType.UserDefinedType,
        2 => type == DbObjectType.UserDefinedTableType,
        3 => type == DbObjectType.Sequence,
        4 => type == DbObjectType.Table,
        5 => type == DbObjectType.View,
        6 => type == DbObjectType.Procedure,
        7 => type == DbObjectType.Function,
        _ => true
    };

    private bool MatchesDestFilter(SyncStatus item) => FilterDestIndex switch
    {
        1 => !item.ExistsInDestination,
        2 => item.ExistsInDestination,
        _ => true
    };

    private bool MatchesSyncFilter(SyncStatus item) => FilterSyncIndex switch
    {
        1 => item.Status == "Pending",
        2 => item.Status == "Exists",
        3 => item.Status == "Success",
        4 => item.Status == "Error",
        _ => true
    };

    // === Helpers ===

    private static async Task<long?> SafeGetRowCount(IDbService svc, string cs, DbObject o)
    {
        try { return await svc.GetRowCountAsync(cs, o); }
        catch { return null; }
    }

    private static async Task<List<DbIndex>> SafeGetIndexes(IDbService svc, string cs, DbObject o)
    {
        try { return await svc.GetTableIndexesAsync(cs, o); }
        catch { return []; }
    }

    private void UpdateProgress(int completed, int total)
    {
        SyncProgress = total > 0 ? (double)completed / total * 100 : 0;
        SyncProgressText = $"{Math.Round(SyncProgress)}%";
    }

    private List<int> TopologicalSort(List<int> selectedIndices)
    {
        var sorted = new List<int>();
        var visited = new HashSet<int>();
        var temporary = new HashSet<int>();
        var selectedSet = new HashSet<int>(selectedIndices);

        var fullNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _syncItems.Count; i++)
            fullNameToIndex[_syncItems[i].SourceObject.FullName] = i;

        selectedIndices.Sort((a, b) =>
            _syncItems[a].SourceObject.Type.CompareTo(_syncItems[b].SourceObject.Type));

        void Visit(int idx)
        {
            if (temporary.Contains(idx) || visited.Contains(idx)) return;
            temporary.Add(idx);

            foreach (var dep in _syncItems[idx].Dependencies)
            {
                if (fullNameToIndex.TryGetValue(dep, out var depIdx) && selectedSet.Contains(depIdx))
                    Visit(depIdx);
            }

            temporary.Remove(idx);
            visited.Add(idx);
            sorted.Add(idx);
        }

        foreach (var idx in selectedIndices)
            Visit(idx);

        return sorted;
    }
}
