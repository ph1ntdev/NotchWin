using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace NotchWin;

public sealed record MediaState(string Title, string Artist, bool IsPlaying, BitmapImage? Cover);

/// <summary>Положение в треке. Position посчитан на момент UpdatedAt.</summary>
public sealed record MediaTimeline(TimeSpan Position, TimeSpan Duration, DateTimeOffset UpdatedAt, bool IsPlaying)
{
    /// <summary>Текущая позиция с учётом времени, прошедшего с последнего обновления от плеера.</summary>
    public TimeSpan Now()
    {
        var pos = IsPlaying ? Position + (DateTimeOffset.Now - UpdatedAt) : Position;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos > Duration ? Duration : pos;
    }
}

/// <summary>
/// Читает то, что сейчас играет в Windows (Spotify, браузер, Яндекс Музыка и т.д.)
/// через системный API GlobalSystemMediaTransportControls и управляет воспроизведением.
/// </summary>
internal sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Вызывается на фоновом потоке. null значит, что ничего не играет.</summary>
    public event Action<MediaState?>? Changed;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => AttachSession();
        AttachSession();
    }

    private void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, object e) => TimelineChanged?.Invoke();

    /// <summary>Плеер сообщил новое положение (перемотка, смена трека). Вызывается на фоновом потоке.</summary>
    public event Action? TimelineChanged;

    /// <summary>Положение в треке или null, если плеер его не сообщает (длительность 0).</summary>
    public MediaTimeline? GetTimeline()
    {
        try
        {
            var session = _session;
            if (session == null) return null;
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration <= TimeSpan.Zero) return null;
            var playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return new MediaTimeline(t.Position - t.StartTime, duration, t.LastUpdatedTime, playing);
        }
        catch { return null; }
    }

    /// <summary>Перематывает и пишет в media.log, что ответил плеер (для разбора, если перемотка не сработала).</summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session == null) return false;
        try
        {
            var t = session.GetTimelineProperties();
            var ok = await session.TryChangePlaybackPositionAsync((t.StartTime + position).Ticks);
            Log($"seek {position:mm\\:ss} app={session.SourceAppUserModelId} was={t.Position:mm\\:ss} ok={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Log($"seek {position:mm\\:ss} failed: {ex.Message}");
            return false;
        }
    }

    public static void Log(string text)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "media.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null)
        {
            Changed?.Invoke(null);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var cover = await LoadCoverAsync(props);
            Changed?.Invoke(new MediaState(props.Title ?? "", props.Artist ?? "", playing, cover));
        }
        catch
        {
            // Плееры иногда закрывают сессию посреди запроса. Просто ждём следующего события.
        }
    }

    private static async Task<BitmapImage?> LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var winrtStream = await props.Thumbnail.OpenReadAsync();
            using var stream = winrtStream.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze(); // чтобы отдать картинку в UI-поток
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public Task PlayPauseAsync() => _session?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;
    public Task NextAsync() => _session?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;
    public Task PreviousAsync() => _session?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;
}
