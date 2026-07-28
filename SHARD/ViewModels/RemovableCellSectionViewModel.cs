using System.Collections.Generic;
using System.Windows.Input;
using ReactiveUI;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class RemovableCellSectionViewModel
{
    private readonly CellSectionViewModel _inner;

    public string Header     => _inner.Header;
    public IReadOnlyList<InfoRow> Rows => _inner.Rows;
    public int ByteOffset    => _inner.ByteOffset;
    public ICommand RemoveCommand { get; }

    public RemovableCellSectionViewModel(BTreeLeafCell cell, int index, int byteOffset, Action onRemove)
    {
        _inner        = new CellSectionViewModel(cell, index, byteOffset);
        RemoveCommand = ReactiveCommand.Create(onRemove);
    }
}
