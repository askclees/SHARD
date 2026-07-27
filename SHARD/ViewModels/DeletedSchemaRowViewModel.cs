using Avalonia.Media;
using SHARD.Core;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

public sealed class DeletedSchemaRowViewModel
{
    private readonly DeletedSqliteMasterRow _source;

    public SqliteMasterObjectType ObjectType => _source.Row.ObjectType;
    public string? Name          => _source.Row.Name;
    public string? TableName     => _source.Row.TableName;
    public uint?   RootPage      => _source.Row.RootPage;
    public string? Sql           => _source.Row.Sql;
    public uint    PageNumber    => _source.Row.PageNumber;
    public int     CellOffset    => _source.Row.CellOffset;
    public int     CellLength    => _source.Row.CellLength;

    public string          RecoveryMethod  => _source.RecoveryMethod;
    public RootPageStatus  RootPageStatus  => _source.RootPageStatus;

    public string StatusLabel => _source.RootPageStatus switch
    {
        RootPageStatus.Valid   => "Recoverable",
        RootPageStatus.Reused  => "Overwritten",
        RootPageStatus.Freed   => "Freed",
        RootPageStatus.Invalid => "Invalid",
        _                      => "Unknown",
    };

    public IBrush StatusBrush => _source.RootPageStatus switch
    {
        RootPageStatus.Valid   => new SolidColorBrush(Color.FromRgb( 78, 201, 176)),
        RootPageStatus.Reused  => new SolidColorBrush(Color.FromRgb(220, 140,  40)),
        RootPageStatus.Freed   => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
        RootPageStatus.Invalid => new SolidColorBrush(Color.FromRgb(224,  80,  80)),
        _                      => Brushes.Gray,
    };

    public DeletedSchemaRowViewModel(DeletedSqliteMasterRow source)
    {
        _source = source;
    }
}
