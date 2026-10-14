using System.Windows.Media.Imaging;
using SimilarPhotoCleaner.Core;

namespace SimilarPhotoCleaner.ViewModels;

/// <summary>크게 보기용 이미지를 백그라운드에서 읽고 최근 몇 장만 기억한다. UI 스레드에서만 사용한다.</summary>
public sealed class PreviewCache(int capacity, int minLongSide)
{
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _items = new(StringComparer.OrdinalIgnoreCase);

    public Task<BitmapSource?> GetAsync(string path)
    {
        if (_items.TryGetValue(path, out var task))
        {
            _order.Remove(path);
            _order.AddFirst(path);
            return task;
        }

        task = Task.Run(() =>
        {
            try { return ImageLoader.LoadForDisplay(path, minLongSide); }
            catch { return null; }
        });
        _items[path] = task;
        _order.AddFirst(path);
        while (_order.Count > capacity)
        {
            _items.Remove(_order.Last!.Value);
            _order.RemoveLast();
        }
        return task;
    }

    public void Remove(string path)
    {
        if (_items.Remove(path)) _order.Remove(path);
    }

    public void Clear()
    {
        _items.Clear();
        _order.Clear();
    }
}
