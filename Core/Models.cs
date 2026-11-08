using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace UrukCardEditor.Core;

public sealed class CardIndexItem : INotifyPropertyChanged
{
    private string _displayName = "";
    private BitmapSource? _thumbnail;
    private int _thumbnailLoading;

    public required string Key { get; init; }
    public required string DisplayName
    {
        get => _displayName;
        set { if (_displayName == value) return; _displayName = value; OnPropertyChanged(); }
    }
    public required string ArchivePath { get; init; }
    public required ZedEntry Entry { get; init; }
    public required int ArchiveNumber { get; init; }
    public required CardSourceFormat Format { get; init; }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { if (ReferenceEquals(_thumbnail, value)) return; _thumbnail = value; OnPropertyChanged(); }
    }

    public string SourceLabel => $"DATA_{ArchiveNumber:000} · {Format.ToString().ToUpperInvariant()} · {Key}";
    public override string ToString() => DisplayName;

    public bool TryBeginThumbnailLoad() => Interlocked.CompareExchange(ref _thumbnailLoading, 1, 0) == 0;
    public void EndThumbnailLoad() => Interlocked.Exchange(ref _thumbnailLoading, 0);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum CardSourceFormat
{
    Lua,
    Xml
}

public sealed class LoadedCard
{
    public required CardIndexItem Index { get; init; }
    public required CardDocument Document { get; init; }
    public string? NewArtPath { get; set; }
}

public sealed record BundleLocation(string ArchivePath, ZedEntry Entry, int ArchiveNumber);
