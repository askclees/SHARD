using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core;
using SHARD.Core.Enums;
using SHARD.Core.Recovery;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.ViewModels;

/// <summary>
/// Whether a candidate table is included in the next carve run. One shared instance per table
/// name, referenced by both that table's <see cref="StandardTableInfo"/> and
/// <see cref="CarvingTableGroup"/>, so deselecting it anywhere (currently: the Standard list)
/// hides its Focused ranges too and excludes it from both <c>RunStandardCandidates</c> and
/// <c>RunFocusedCandidates</c> — without needing to keep two separate checkboxes in sync by hand.
/// </summary>
public sealed class TableInclusion : ReactiveObject
{
    public string TableName { get; }

    private bool _isIncluded = true;
    public bool IsIncluded
    {
        get => _isIncluded;
        set => this.RaiseAndSetIfChanged(ref _isIncluded, value);
    }

    public TableInclusion(string tableName) => TableName = tableName;
}

/// <summary>One candidate table's schema, for the read-only Standard reference list.</summary>
public sealed class StandardTableInfo
{
    public string TableName { get; }
    public string ColumnsSummary { get; }
    public TableInclusion Inclusion { get; }

    public StandardTableInfo(string tableName, string columnsSummary, TableInclusion inclusion)
    {
        TableName = tableName;
        ColumnsSummary = columnsSummary;
        Inclusion = inclusion;
    }
}

/// <summary>One narrowable column's editable [Min, Max] content-length range, for the Focused review grid.</summary>
public sealed class CarvingColumnRow : ReactiveObject
{
    public string ColumnName { get; }
    public string AffinityLabel { get; }
    public int ColumnIndex { get; }

    // decimal, not int, to match Avalonia's NumericUpDown.Value (decimal?) directly — avoids a
    // converter for what's otherwise always a whole-number byte-length in practice.
    private decimal _minLength;
    public decimal MinLength
    {
        get => _minLength;
        set => this.RaiseAndSetIfChanged(ref _minLength, value);
    }

    private decimal _maxLength;
    public decimal MaxLength
    {
        get => _maxLength;
        set => this.RaiseAndSetIfChanged(ref _maxLength, value);
    }

    public CarvingColumnRow(int columnIndex, string columnName, string affinityLabel, int minLength, int maxLength)
    {
        ColumnIndex   = columnIndex;
        ColumnName    = columnName;
        AffinityLabel = affinityLabel;
        _minLength    = minLength;
        _maxLength    = maxLength;
    }
}

/// <summary>One candidate table's narrowable columns, for the Focused review grid.</summary>
public sealed class CarvingTableGroup
{
    public string TableName { get; }
    public ObservableCollection<CarvingColumnRow> Columns { get; }
    public TableInclusion Inclusion { get; }

    public CarvingTableGroup(string tableName, ObservableCollection<CarvingColumnRow> columns, TableInclusion inclusion)
    {
        TableName = tableName;
        Columns   = columns;
        Inclusion = inclusion;
    }
}

/// <summary>
/// Backs the "Carve Unknown Pages" tab. The Standard section is a read-only reference list of
/// every candidate table's schema (live tables plus dropped-table schemas recovered from
/// sqlite_master history — see <see cref="OrphanPageCarver.BuildCandidates"/>), runnable as-is via
/// <see cref="RunStandardCandidates"/>. The Focused section narrows every non-rowid-alias column
/// to its observed [min, max] content-length range (<see cref="RecordStructure.Tighten"/>) and lets
/// the user review and adjust those ranges — via <see cref="FocusedGroups"/> — before
/// <see cref="RunFocusedCandidates"/> applies them. This ViewModel is created once per open database
/// (see <c>MainWindowViewModel.CarveTab</c>) and stays alive for the session, so edits made here
/// survive switching away from and back to the tab.
/// </summary>
public sealed class CarveUnknownPagesViewModel : ReactiveObject
{
    private readonly IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> _standardCandidates;
    private readonly IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> _focusedCandidates;
    private readonly Dictionary<string, TableInclusion> _inclusionByTable = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<StandardTableInfo> StandardTables { get; } = [];
    public ObservableCollection<CarvingTableGroup> FocusedGroups { get; } = [];

    private bool _canRunFocused = true;
    public bool CanRunFocused
    {
        get => _canRunFocused;
        private set => this.RaiseAndSetIfChanged(ref _canRunFocused, value);
    }

    public CarveUnknownPagesViewModel(SqliteForensicDatabase database)
    {
        _standardCandidates = OrphanPageCarver.BuildCandidates(database, CarveMode.Loose);
        _focusedCandidates  = OrphanPageCarver.BuildCandidates(database, CarveMode.Tight);

        foreach (var (schema, _) in _standardCandidates)
        {
            var inclusion = GetOrAddInclusion(schema.TableName);
            string summary = string.Join(", ", schema.Columns.Select(c =>
                c.IsRowIdAlias ? $"{c.Name} (rowid)" : $"{c.Name} ({c.DeclaredType ?? c.Affinity.ToString()}{(c.IsNotNull ? " NOT NULL" : "")})"));
            StandardTables.Add(new StandardTableInfo(schema.TableName, summary, inclusion));
        }

        foreach (var (schema, structure) in _focusedCandidates)
        {
            var columnRows = new ObservableCollection<CarvingColumnRow>();
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                var col = schema.Columns[i];
                if (col.IsRowIdAlias) continue; // always exactly 0 bytes (NULL) — nothing to adjust

                // No observed data to narrow from (e.g. an empty table) — fall back to a sensible
                // default range rather than leaving Min=Max=0, which for Text/Blob would wrongly
                // require an empty value and for Integer/Real would wrongly forbid every valid width.
                var range = structure.AllowedContentLengthRangePerColumn[i];
                int min, max;
                if (range is not null)
                {
                    (min, max) = range.Value;
                }
                else if (col.Affinity is TypeAffinity.Integer or TypeAffinity.Real)
                {
                    (min, max) = (1, 8); // full span of valid SQLite integer/float serial-type widths
                }
                else
                {
                    (min, max) = (0, 1024); // Text/Blob: generous default, no observed data to bound it by
                }

                var row = new CarvingColumnRow(i, col.Name, col.Affinity.ToString(), min, max);
                row.PropertyChanged += (_, __) => RecomputeCanRunFocused();
                columnRows.Add(row);
            }

            if (columnRows.Count > 0)
                FocusedGroups.Add(new CarvingTableGroup(schema.TableName, columnRows, GetOrAddInclusion(schema.TableName)));
        }

        RecomputeCanRunFocused();
    }

    private TableInclusion GetOrAddInclusion(string tableName)
    {
        if (!_inclusionByTable.TryGetValue(tableName, out var inclusion))
            _inclusionByTable[tableName] = inclusion = new TableInclusion(tableName);
        return inclusion;
    }

    private void RecomputeCanRunFocused() =>
        CanRunFocused = FocusedGroups
            .Where(g => g.Inclusion.IsIncluded)
            .All(g => g.Columns.All(c => c.MinLength <= c.MaxLength));

    private bool IsIncluded(string tableName) =>
        !_inclusionByTable.TryGetValue(tableName, out var inclusion) || inclusion.IsIncluded;

    /// <summary>The schema-derived candidates for currently-included tables, unmodified — Standard mode never needs review.</summary>
    public IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> RunStandardCandidates() =>
        _standardCandidates.Where(c => IsIncluded(c.Schema.TableName)).ToList();

    /// <summary>
    /// The observed-data-tightened candidates for currently-included tables, with each reviewed
    /// column's current [MinLength, MaxLength] applied via <see cref="RecordStructure.NarrowColumn"/>.
    /// </summary>
    public IReadOnlyList<(TableSchema Schema, RecordStructure Structure)> RunFocusedCandidates()
    {
        var groupsByTable = FocusedGroups.ToDictionary(g => g.TableName, StringComparer.OrdinalIgnoreCase);
        var result = new List<(TableSchema Schema, RecordStructure Structure)>();
        foreach (var (schema, structure) in _focusedCandidates)
        {
            if (!IsIncluded(schema.TableName)) continue;
            if (groupsByTable.TryGetValue(schema.TableName, out var group))
                foreach (var row in group.Columns)
                    structure.NarrowColumn(row.ColumnIndex, allowedContentLengthRange: ((int)row.MinLength, (int)row.MaxLength));

            result.Add((schema, structure));
        }

        return result;
    }
}
