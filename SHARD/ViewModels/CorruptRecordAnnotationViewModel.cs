using System.Collections.ObjectModel;
using ReactiveUI;
using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Recovery;
using SHARD.Core.Schema;
using SHARD.Core.Shadow;

namespace SHARD.ViewModels;

public sealed class CorruptRecordAnnotationViewModel : ViewModelBase
{
    public int AnchorOffset { get; }
    public string WindowTitle { get; }

    private readonly byte[] _pageBytes;
    private readonly TextEncoding _encoding;
    private readonly TableSchema _schema;

    public ObservableCollection<CorruptColumnEntryViewModel> Columns { get; } = [];
    public IReadOnlyList<string> AnchorColumnLabels { get; }

    private int _anchorColumnIndex;
    public int AnchorColumnIndex
    {
        get => _anchorColumnIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _anchorColumnIndex, value);
            RebuildColumnStates();
        }
    }

    private string _rowIdText = "-1";
    public string RowIdText
    {
        get => _rowIdText;
        set => this.RaiseAndSetIfChanged(ref _rowIdText, value);
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    private bool _hasErrors;
    public bool HasErrors
    {
        get => _hasErrors;
        private set => this.RaiseAndSetIfChanged(ref _hasErrors, value);
    }

    private bool _canSave;
    public bool CanSave
    {
        get => _canSave;
        private set => this.RaiseAndSetIfChanged(ref _canSave, value);
    }

    // ── Extracted table schema ────────────────────────────────────────────

    private TableSchema? _extractedSchema;
    public TableSchema? ExtractedSchema
    {
        get => _extractedSchema;
        private set
        {
            _extractedSchema = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasExtractedSchema));
        }
    }

    public bool HasExtractedSchema => _extractedSchema is not null;

    private string _schemaDescription = "";
    public string SchemaDescription
    {
        get => _schemaDescription;
        private set => this.RaiseAndSetIfChanged(ref _schemaDescription, value);
    }

    private long? _extractedRootPage;
    public long? ExtractedRootPage => _extractedRootPage;

    /// <summary>The raw CREATE TABLE SQL that produced <see cref="ExtractedSchema"/>, if any.</summary>
    public string? ExtractedSql { get; private set; }

    /// <summary>Set to true by the view when the user clicks "Register schema for carving".</summary>
    public bool WantToRegisterSchema { get; set; }

    // ─────────────────────────────────────────────────────────────────────

    private BTreeLeafCell? _decodedCell;
    public BTreeLeafCell? DecodedCell => _decodedCell;

    public CorruptRecordAnnotationViewModel(int anchorOffset, byte[] pageBytes, TextEncoding encoding, TableSchema schema)
    {
        AnchorOffset = anchorOffset;
        _pageBytes   = pageBytes;
        _encoding    = encoding;
        _schema      = schema;
        WindowTitle  = $"Annotate corrupt record at offset 0x{anchorOffset:X} ({anchorOffset})";

        AnchorColumnLabels = schema.Columns
            .Select((c, i) => $"Col {i}: {c.Name} ({c.Affinity})")
            .ToList();

        BuildColumns(0, preservedLengths: null);
    }

    private void BuildColumns(int anchorIndex, IReadOnlyList<string>? preservedLengths)
    {
        Columns.Clear();
        for (int i = 0; i < _schema.Columns.Count; i++)
        {
            var col = _schema.Columns[i];
            var entry = new CorruptColumnEntryViewModel(
                index: i,
                columnName: col.Name,
                affinity: col.Affinity,
                isBeforeAnchor: i < anchorIndex,
                isAnchor: i == anchorIndex);

            if (preservedLengths is not null && i < preservedLengths.Count)
                entry.ManualLength = preservedLengths[i];

            Columns.Add(entry);
        }
    }

    private void RebuildColumnStates()
    {
        var existingLengths = Columns.Select(c => c.ManualLength).ToList();
        BuildColumns(_anchorColumnIndex, existingLengths);
        _decodedCell     = null;
        CanSave          = false;
        StatusMessage    = "";
        HasErrors        = false;
        ExtractedSchema  = null;
        ExtractedSql     = null;
        SchemaDescription = "";
    }

    public void Decode()
    {
        if (!long.TryParse(RowIdText.Trim(), out long rowId))
        {
            StatusMessage = "Row ID must be a valid integer (use -1 for unknown).";
            HasErrors     = true;
            return;
        }

        var preAnchorLengths = new List<int>();
        for (int i = 0; i < _anchorColumnIndex; i++)
        {
            var entry = Columns[i];
            if (!int.TryParse(entry.ManualLength.Trim(), out int len) || len < 0)
            {
                StatusMessage = $"Column {i} ({entry.ColumnName}): length must be a non-negative integer.";
                HasErrors     = true;
                return;
            }
            preAnchorLengths.Add(len);
        }

        HasErrors = false;

        var result = CorruptRecordDecoder.Decode(
            _pageBytes,
            AnchorOffset,
            _anchorColumnIndex,
            preAnchorLengths,
            rowId,
            _schema,
            _encoding);

        _decodedCell = result.Cell;

        if (result.Errors.Count > 0)
        {
            StatusMessage = string.Join("  |  ", result.Errors);
            HasErrors     = !result.IsValid;
        }
        else
        {
            StatusMessage = "Decoded successfully.";
        }

        if (result.Cell is not null)
        {
            for (int i = 0; i < Columns.Count; i++)
            {
                var col = Columns[i];
                if (i < result.Cell.HeaderEntries.Count)
                {
                    var h = result.Cell.HeaderEntries[i];
                    col.SerialTypeLabel    = h.RawValue.Value.ToString();
                    col.ContentLengthLabel = h.ContentLength.ToString();
                    col.DecodedValue       = i < result.Cell.FieldValues.Count
                        ? (result.Cell.FieldValues[i]?.Value?.ToString() ?? "NULL")
                        : "NULL";
                }
                else
                {
                    col.SerialTypeLabel    = "—";
                    col.ContentLengthLabel = "—";
                    col.DecodedValue       = "—";
                }
            }

            CanSave = true;
            TryExtractTableSchema(result.Cell);
        }
        else
        {
            CanSave          = false;
            ExtractedSchema  = null;
            SchemaDescription = "";
        }
    }

    private void TryExtractTableSchema(BTreeLeafCell cell)
    {
        ExtractedSchema  = null;
        ExtractedSql     = null;
        SchemaDescription = "";
        _extractedRootPage = null;

        // Scan text fields for a parseable CREATE TABLE statement
        for (int i = 0; i < cell.HeaderEntries.Count; i++)
        {
            if (cell.HeaderEntries[i].Kind != SerialTypeKind.Text) continue;
            if (i >= cell.FieldValues.Count) continue;
            if (cell.FieldValues[i]?.Value is not string sql) continue;

            var schema = CreateTableParser.ExtractTableSchema(sql);
            if (schema is null) continue;

            ExtractedSql    = sql;
            ExtractedSchema = schema;
            SchemaDescription = $"Table '{schema.TableName}' — " +
                $"{schema.Columns.Count} column{(schema.Columns.Count == 1 ? "" : "s")}: " +
                string.Join(", ", schema.Columns.Select(c => $"{c.Name} ({c.Affinity})"));

            // Try to find the root page from an integer field > 1
            for (int j = 0; j < cell.HeaderEntries.Count; j++)
            {
                var kind = cell.HeaderEntries[j].Kind;
                if (kind is not (SerialTypeKind.Integer or SerialTypeKind.Int0 or SerialTypeKind.Int1)) continue;
                if (j >= cell.FieldValues.Count) continue;
                long v = cell.FieldValues[j]?.Value is long lv ? lv
                       : kind == SerialTypeKind.Int1 ? 1L : 0L;
                if (v > 1) { _extractedRootPage = v; break; }
            }

            return;
        }
    }

    public void SaveToProject(ShadowProject project, uint pageNumber)
    {
        if (_decodedCell is null)
            throw new InvalidOperationException("No decoded cell to save. Call Decode() first.");

        project.SaveRecoveredRecord(_schema, _decodedCell, pageNumber, AnchorOffset);
    }
}
