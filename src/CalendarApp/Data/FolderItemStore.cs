using System.Globalization;
using System.IO;
using CalendarApp.Models;

namespace CalendarApp.Data;

/// <summary>
/// Item store backed by a plain folder of markdown files, following the rules in
/// 存储目录设计.md:
///
///   items/YYYYMMDD-HHmmss_YYYYMMDD-HHmmss_P&lt;级别&gt;_&lt;净化后的标题&gt;_&lt;标签&gt;.md
///
/// File name fields are separated by '_'; a field's internal separator is '-'.
/// Times are local wall-clock, formatted as the file name requires.
/// </summary>
public sealed class FolderItemStore : IDisposable
{
    /// <summary>Sub-directory that holds all item files (flat storage).</summary>
    public const string ItemsDirectoryName = "items";

    /// <summary>Sub-directory that holds deleted item files (soft-delete trash).</summary>
    public const string DeletedDirectoryName = "deleted";

    private readonly object _gate = new();
    private string? _rootPath;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _reloadDebounce;

    public string? RootPath => _rootPath;

    public string? ItemsPath => _rootPath is null ? null : Path.Combine(_rootPath, ItemsDirectoryName);

    public string? DeletedPath => _rootPath is null ? null : Path.Combine(_rootPath, DeletedDirectoryName);

    public bool IsOpen => _rootPath is not null;

    /// <summary>Raised when the storage folder is opened/changed; subscribers reload.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised (debounced, on a background thread) when the items/ folder contents
    /// change on disk, including edits made outside the app.
    /// </summary>
    public event Action? ItemsChanged;

    /// <summary>
    /// Opens (creating when necessary) the storage folder and its items/
    /// sub-directory. Safe to call again to switch folders at runtime.
    /// </summary>
    public void Open(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var itemsPath = Path.Combine(fullPath, ItemsDirectoryName);
        Directory.CreateDirectory(itemsPath);

        lock (_gate)
        {
            _rootPath = fullPath;

            // Re-point the watcher at the new items/ directory.
            _watcher?.Dispose();
            var watcher = new FileSystemWatcher(itemsPath, "*.md")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
            };
            watcher.Created += OnItemFileChanged;
            watcher.Changed += OnItemFileChanged;
            watcher.Deleted += OnItemFileChanged;
            watcher.Renamed += OnItemFileChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Non-deleted items whose start instant is in [from, to). Items are sorted
    /// by start time (then title) and limited to <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<ScheduleEvent> GetUpcoming(
        DateTimeOffset from, DateTimeOffset? to = null, int limit = 100)
    {
        var fromLocal = from.ToLocalTime();
        var toLocal = to?.ToLocalTime();

        var result = new List<ScheduleEvent>();
        foreach (var file in EnumerateItemFiles())
        {
            if (!TryParseItemFile(file, out var item))
            {
                continue;
            }

            var localStart = item.StartsAt.ToLocalTime();
            if (localStart < fromLocal) continue;
            if (toLocal is { } until && localStart >= until) continue;

            result.Add(item);
            if (result.Count >= limit) break;
        }

        result.Sort(static (a, b) =>
        {
            var byStart = a.StartsAt.CompareTo(b.StartsAt);
            return byStart != 0 ? byStart : string.Compare(a.Title, b.Title, StringComparison.Ordinal);
        });
        return result;
    }

    /// <summary>All non-deleted items on the given local calendar day.</summary>
    public IReadOnlyList<ScheduleEvent> GetEventsOnDay(DateTimeOffset day)
    {
        var local = day.ToLocalTime();
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return GetUpcoming(start, start.AddDays(1), limit: 500);
    }

    /// <summary>All non-deleted items, sorted by priority (P1 first) then start time (used for the tasks view).</summary>
    public IReadOnlyList<ScheduleEvent> GetItems()
    {
        var result = new List<ScheduleEvent>();
        foreach (var file in EnumerateItemFiles())
        {
            if (TryParseItemFile(file, out var item))
            {
                result.Add(item);
            }
        }

        result.Sort(static (a, b) =>
        {
            var byPriority = a.Priority.CompareTo(b.Priority);
            return byPriority != 0 ? byPriority : a.StartsAt.CompareTo(b.StartsAt);
        });
        return result;
    }

    /// <summary>Inserts a new item file. Returns the file name used.</summary>
    public string Insert(ScheduleEvent item)
    {
        ArgumentNullException.ThrowIfNull(_rootPath);

        var startsAt = item.StartsAt.ToLocalTime();
        var endsAt = (item.EndsAt ?? item.StartsAt).ToLocalTime();
        var priority = item.Priority is >= 1 and <= 4 ? item.Priority : 3;
        var title = SanitizeSegment(item.Title);
        var tags = SanitizeSegment(item.Tags);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("事项标题不能为空。");
        }

        var startPart = FormatTimePart(startsAt);
        var endPart = FormatTimePart(endsAt);
        var fileName = string.IsNullOrWhiteSpace(tags)
            ? $"{startPart}_{endPart}_P{priority}_{title}.md"
            : $"{startPart}_{endPart}_P{priority}_{title}_{tags}.md";

        var fullPath = Path.Combine(ItemsPath!, fileName);
        if (File.Exists(fullPath))
        {
            // Extremely unlikely (same second + same title + same tags): add a
            // short suffix so the file is never silently overwritten.
            fullPath = Path.Combine(
                ItemsPath!,
                $"{Path.GetFileNameWithoutExtension(fullPath)}_{DateTime.UtcNow:HHmmssfff}.md");
        }

        var content = item.Notes ?? string.Empty;
        File.WriteAllText(fullPath, content, System.Text.Encoding.UTF8);
        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Deletes an item. When the item is backed by a real file it is deleted
    /// from disk; items with only synthetic ids (legacy/unknown files) no-op.
    /// </summary>
    public void Delete(ScheduleEvent item)
    {
        ArgumentNullException.ThrowIfNull(_rootPath);
        if (item.FileName is null)
        {
            return;
        }

        var path = Path.Combine(ItemsPath!, item.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Soft-deletes an item by moving its file into the deleted/ folder next to
    /// items/ (creating the folder when missing). Items with only synthetic ids
    /// (legacy/unknown files) no-op. Returns the destination file name, or null
    /// when nothing was moved.
    /// </summary>
    public string? MoveToDeleted(ScheduleEvent item)
    {
        ArgumentNullException.ThrowIfNull(_rootPath);
        if (item.FileName is null)
        {
            return null;
        }

        var source = Path.Combine(ItemsPath!, item.FileName);
        if (!File.Exists(source))
        {
            return null;
        }

        var deletedPath = DeletedPath!;
        Directory.CreateDirectory(deletedPath);

        var destination = Path.Combine(deletedPath, item.FileName);
        if (File.Exists(destination))
        {
            // Extremely unlikely (same file already trashed): add a short suffix
            // so nothing is ever silently overwritten.
            destination = Path.Combine(
                deletedPath,
                $"{Path.GetFileNameWithoutExtension(item.FileName)}_{DateTime.UtcNow:HHmmssfff}.md");
        }

        File.Move(source, destination);
        return Path.GetFileName(destination);
    }

    public long Count()
    {
        long count = 0;
        foreach (var _ in EnumerateItemFiles())
        {
            count++;
        }
        return count;
    }

    private IEnumerable<string> EnumerateItemFiles()
    {
        var itemsPath = ItemsPath;
        if (itemsPath is null || !Directory.Exists(itemsPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(itemsPath, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            yield return file;
        }
    }

    /// <summary>Parses a filename and file content into a <see cref="ScheduleEvent"/>.</summary>
    internal static bool TryParseItemFile(string path, out ScheduleEvent item)
    {
        item = null!;
        var fileName = Path.GetFileName(path);

        // Format: start_end_P{n}_{title}[_{tags}].md
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var parts = baseName.Split('_');
        if (parts.Length < 4)
        {
            return false;
        }

        if (!TryParseTimePart(parts[0], out var start)
            || !TryParseTimePart(parts[1], out var end))
        {
            return false;
        }

        if (parts[2].Length < 2 || parts[2][0] is not 'P' and not 'p'
            || !int.TryParse(parts[2].AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        {
            priority = 3;
        }

        var title = parts[3];
        var tags = parts.Length >= 5
            ? string.Join('_', parts.Skip(4))
            : string.Empty;

        string notes;
        try
        {
            notes = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (IOException)
        {
            notes = string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            notes = string.Empty;
        }

        item = new ScheduleEvent
        {
            Id = CreateId(fileName),
            FileName = fileName,
            Title = title,
            Notes = notes,
            StartsAt = start,
            EndsAt = end,
            Priority = priority,
            Tags = tags,
            AllDay = false,
            Location = string.Empty,
            CreatedAt = start,
            UpdatedAt = end,
        };
        return true;
    }

    private static long CreateId(string fileName)
    {
        var hash = fileName.GetHashCode(StringComparison.OrdinalIgnoreCase);
        return (uint)hash;
    }

    private static string FormatTimePart(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    private static bool TryParseTimePart(string part, out DateTimeOffset value)
    {
        value = default;
        // The date and time halves are separated by the '-'.
        var hyphen = part.IndexOf('-');
        if (hyphen != 8 || part.Length != 15)
        {
            return false;
        }

        if (!DateTime.TryParseExact(part, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return false;
        }

        value = new DateTimeOffset(local);
        return true;
    }

    /// <summary>
    /// Replaces characters that are illegal in Windows file names with a space,
    /// collapses whitespace, and trims. Keeps '-' and '_' inside the segment so
    /// tags like "工作-生活" survive.
    /// </summary>
    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (Path.GetInvalidFileNameChars().Contains(c) || c == '_' || c == '|')
            {
                chars[i] = ' ';
            }
        }

        var collapsed = new System.Text.StringBuilder(chars.Length);
        var lastWasSpace = false;
        foreach (var c in chars)
        {
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            collapsed.Append(c);
        }

        return collapsed.ToString().Trim();
    }

    /// <summary>Called by the <see cref="FileSystemWatcher"/> for any .md change in items/.</summary>
    private void OnItemFileChanged(object sender, FileSystemEventArgs e) => ScheduleItemsChanged();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"存储文件夹监控出错: {e.GetException().Message}");
    }

    /// <summary>
    /// Coalesces bursts of watcher events (a single save raises several) into one
    /// <see cref="ItemsChanged"/> notification after 300 ms of quiet.
    /// </summary>
    private void ScheduleItemsChanged()
    {
        lock (_gate)
        {
            _reloadDebounce ??= new System.Threading.Timer(
                _ => ItemsChanged?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
            _reloadDebounce.Change(300, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
            _reloadDebounce?.Dispose();
            _reloadDebounce = null;
        }
    }
}
