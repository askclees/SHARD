using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SHARD.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> whose contents can be replaced in one go. Bound lists
/// handle every <see cref="ObservableCollection{T}.Add"/> as its own change on the UI thread, so
/// refilling a list of tens of thousands of pages one item at a time makes the window stop
/// responding; <see cref="ReplaceAll"/> raises a single reset instead.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
