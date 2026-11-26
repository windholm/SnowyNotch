using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SnowyNotch.Services;

using Session = GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus;

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    BitmapSource? Artwork,
    Color Accent,
    bool IsPlaying,
    TimeSpan Position,
    DateTimeOffset PositionAt,
    TimeSpan Duration,
    string SourceAppId,
    bool CanShuffle,
    bool IsShuffle,
    bool CanRepeat,
    MediaPlaybackAutoRepeatMode Repeat,
    string SourceName = "")
{
    public TimeSpan CurrentPosition
    {
        get
        {
            var p = IsPlaying ? Position + (DateTimeOffset.Now - PositionAt) : Position;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p;
        }
    }
}

public sealed class MediaService : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly TypedEventHandler<Session, MediaPropertiesChangedEventArgs> _onProps;
    private readonly TypedEventHandler<Session, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> _onTimeline;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Session? _session;
    private int _propsVersion;

    private string _title = "", _artist = "";
    private BitmapSource? _art;
    private Color _accent = ColorExtractor.Fallback;
    private bool _playing;
    private bool _canShuffle, _shuffle, _canRepeat;
    private MediaPlaybackAutoRepeatMode _repeat;
    private TimeSpan _pos, _dur;
    private DateTimeOffset _posAt = DateTimeOffset.Now;

    // Часы трека. Приложения шлют позицию редко (Spotify — раз в ~4.5 с), а событие TimelinePropertiesChanged
    // приходит не всегда (смена трека, возобновление). Поэтому таймлайн опрашивается сам, а новое значение
    // принимается, только когда приложение действительно прислало другую пару «позиция + время замера».
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private (TimeSpan Pos, DateTimeOffset At, TimeSpan End) _lastTimeline;
    private DateTimeOffset _playSince = DateTimeOffset.UnixEpoch;
    private bool _noTimeline;
    private DateTimeOffset _trackChangedAt, _seekAt;
    /// <summary>Когда перемотали в известную точку (строка текста, полоса прогресса) — см. ReadTimeline.</summary>
    private DateTimeOffset _seekToAt;
    private (TimeSpan Pos, DateTimeOffset At)? _pendingBack;
    // Только музыка: решения «музыка или нет» по каждому треку и периодическая перепроверка
    // (в браузере SoundCloud может смениться YouTube в той же медиасессии).
    private readonly Dictionary<string, bool> _verdicts = new();
    private readonly DispatcherTimer _rescan = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _musicOnly = true;
    private bool _selecting, _selectAgain;

    public MediaService(Dispatcher ui)
    {
        _ui = ui;
        _onProps = (_, _) => OnUi(ReadPropertiesAsync);
        _onPlayback = (_, _) => OnUi(() => { ReadPlayback(); SelectSession(); Publish(); return Task.CompletedTask; });
        _onTimeline = (_, _) => OnUi(() => { ReadTimeline(force: true); Publish(); return Task.CompletedTask; });
        _rescan.Tick += (_, _) => { if (_musicOnly) SelectSession(); };
        _sync.Tick += (_, _) => { if (ReadTimeline() || AcceptPendingBack()) Publish(); };
    }

    public event Action<MediaSnapshot?>? Changed;

    private bool _positionPolling;

    /// <summary>
    /// Опрашивать позицию трека, только пока её кто-то показывает (раскрытый остров: прогресс, текст песни).
    /// События плеера приходят и без опроса; при включении позиция сразу перечитывается.
    /// </summary>
    public bool PositionPolling
    {
        get => _positionPolling;
        set
        {
            if (value == _positionPolling) return;
            _positionPolling = value;
            if (value && _session != null)
            {
                // Не публиковать прямо отсюда: сеттер вызывают посреди смены состояния острова.
                OnUi(() => { if (_positionPolling && ReadTimeline(force: true)) Publish(); return Task.CompletedTask; });
                _sync.Start();
            }
            else _sync.Stop();
        }
    }

    public MediaSnapshot? Current { get; private set; }

    /// <summary>Показывать только музыку (музыкальные приложения и музыкальные сайты), а не любой звук.</summary>
    public bool MusicOnly
    {
        get => _musicOnly;
        set
        {
            if (_musicOnly == value) return;
            _musicOnly = value;
            SelectSession();
        }
    }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => OnUi(() => { SelectSession(); return Task.CompletedTask; });
        _manager.SessionsChanged += (_, _) => OnUi(() => { SelectSession(); return Task.CompletedTask; });
        _rescan.Start();
        SelectSession();
    }

    /// <summary>
    /// Пауза / воспроизведение. Браузеры (SoundCloud, YouTube Music…) часто не поддерживают «переключить»
    /// или молча его игнорируют — тогда шлём явные «пауза» или «играть» по настоящему состоянию сессии.
    /// Интерфейс переключается сразу, не дожидаясь ответа плеера; настоящее состояние придёт событием.
    /// </summary>
    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s == null) return;
        bool ok = false;
        bool playing = _playing;
        try
        {
            var info = s.GetPlaybackInfo();
            if (info != null) playing = info.PlaybackStatus == PlaybackStatus.Playing;
            var c = info?.Controls;
            if (playing && c?.IsPauseEnabled == true) ok = await s.TryPauseAsync();
            else if (!playing && c?.IsPlayEnabled == true) ok = await s.TryPlayAsync();
            if (!ok) ok = await s.TryTogglePlayPauseAsync();
            if (!ok) ok = playing ? await s.TryPauseAsync() : await s.TryPlayAsync();
        }
        catch (Exception ex) { Debug.WriteLine($"[media] play/pause failed: {ex.Message}"); }

        if (ok && s == _session && _playing == playing)
        {
            var now = DateTimeOffset.Now;
            if (_playing) _pos = Extrapolate(now);
            _posAt = now;
            _playing = !playing;
            if (_playing) _playSince = now;
            Publish();
        }
    }

    public Task NextAsync() => Try(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync()
    {
        _seekAt = DateTimeOffset.Now; // «назад» может начать трек заново — это не сбой позиции
        return Try(s => s.TrySkipPreviousAsync().AsTask());
    }

    public Task ToggleShuffleAsync() => Try(s => s.TryChangeShuffleActiveAsync(!_shuffle).AsTask());

    public Task CycleRepeatAsync()
    {
        var next = _repeat switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return Try(s => s.TryChangeAutoRepeatModeAsync(next).AsTask());
    }

    public Task SeekAsync(TimeSpan position)
    {
        _pos = position;
        _posAt = _seekAt = _seekToAt = DateTimeOffset.Now;
        _pendingBack = null;
        Publish();
        return Try(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());
    }

    private async Task Try(Func<Session, Task> action)
    {
        var s = _session;
        if (s == null) return;
        try { await action(s); }
        catch (Exception ex) { Debug.WriteLine($"[media] control failed: {ex.Message}"); }
    }

    private void SelectSession()
    {
        if (_selecting) { _selectAgain = true; return; }
        OnUi(SelectSessionAsync);
    }

    private async Task SelectSessionAsync()
    {
        if (_manager == null || _selecting) return;
        _selecting = true;
        try
        {
            do
            {
                _selectAgain = false;
                var sessions = _manager.GetSessions().ToList();
                var current = _manager.GetCurrentSession();

                var allowed = new List<Session>();
                foreach (var s in sessions)
                    if (await IsAllowedAsync(s)) allowed.Add(s);

                bool Allowed(Session? s) => s != null && allowed.Any(a => a.SourceAppUserModelId == s.SourceAppUserModelId);
                var playing = allowed.FirstOrDefault(IsPlayingSession);
                Session? chosen;
                if (current != null && Allowed(current) && IsPlayingSession(current)) chosen = current;
                else if (playing != null) chosen = playing;
                // Ничего не играет — остаёмся на текущей музыке (пауза), если она всё ещё подходит.
                else if (Allowed(_session)) chosen = allowed.First(a => a.SourceAppUserModelId == _session!.SourceAppUserModelId);
                else chosen = Allowed(current) ? current : allowed.FirstOrDefault();

                if (chosen?.SourceAppUserModelId == _session?.SourceAppUserModelId && (chosen == null) == (_session == null))
                    continue;
                Attach(chosen);
            }
            while (_selectAgain);
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>Подходит ли сессия: при «только музыка» — музыкальное приложение или музыкальный сайт.</summary>
    private async Task<bool> IsAllowedAsync(Session s)
    {
        if (!_musicOnly) return true;
        string app = s.SourceAppUserModelId ?? "";
        var kind = MusicSources.Classify(app);
        if (kind == MusicSources.Kind.Video) return false;

        bool videoType = false, musicType = false;
        try
        {
            var type = s.GetPlaybackInfo()?.PlaybackType;
            videoType = type == MediaPlaybackType.Video;
            musicType = type == MediaPlaybackType.Music;
        }
        catch { }
        if (kind == MusicSources.Kind.Music) return !videoType;

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            string title = props?.Title ?? "", artist = props?.Artist ?? "", album = props?.AlbumTitle ?? "";
            if (title.Length == 0) return false;
            string key = $"{app}\u0001{title}\u0001{artist}\u0001{album}\u0001{videoType}";
            if (_verdicts.TryGetValue(key, out bool known)) return known;

            double? aspect = null;
            if (artist.Length > 0 && album.Length == 0 && props?.Thumbnail != null)
            {
                // Нужны только пропорции — читаем размер из заголовка, без декодирования картинки.
                var data = await ReadThumbnailBytesAsync(props.Thumbnail);
                if (data != null && ImageFx.Measure(data) is { H: > 0 } size) aspect = size.W / (double)size.H;
            }
            bool verdict = MusicSources.IsMusic(kind, videoType, musicType, artist, album, aspect);
            if (_verdicts.Count > 300) _verdicts.Clear();
            _verdicts[key] = verdict;
            return verdict;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[media] classify failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsPlayingSession(Session s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing; }
        catch { return false; }
    }

    private void Attach(Session? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= _onProps;
            _session.PlaybackInfoChanged -= _onPlayback;
            _session.TimelinePropertiesChanged -= _onTimeline;
        }

        _session = session;
        _propsVersion++;
        _title = _artist = "";
        _art = null;
        _accent = ColorExtractor.Fallback;
        _playing = false;
        _pos = _dur = TimeSpan.Zero;
        _posAt = DateTimeOffset.Now;
        _lastTimeline = default;
        _noTimeline = false;
        _pendingBack = null;
        _trackChangedAt = DateTimeOffset.Now;
        _playSince = DateTimeOffset.UnixEpoch;

        if (session == null)
        {
            _sync.Stop();
            Publish();
            return;
        }

        session.MediaPropertiesChanged += _onProps;
        session.PlaybackInfoChanged += _onPlayback;
        session.TimelinePropertiesChanged += _onTimeline;

        ReadPlayback(initial: true);
        ReadTimeline(force: true);
        if (_positionPolling) _sync.Start();
        OnUi(ReadPropertiesAsync);
    }

    private async Task ReadPropertiesAsync()
    {
        var s = _session;
        if (s == null) return;
        int version = ++_propsVersion;

        var props = await s.TryGetMediaPropertiesAsync();
        if (version != _propsVersion || s != _session) return;

        string title = props?.Title ?? "";
        string artist = props?.Artist ?? "";
        if (string.IsNullOrWhiteSpace(artist)) artist = props?.AlbumArtist ?? "";

        bool trackChanged = title != _title || artist != _artist;
        if (trackChanged) _trackChangedAt = DateTimeOffset.Now;
        _title = title;
        _artist = artist;
        if (trackChanged)
        {
            // Без таймлайна новый трек начинается с нуля по своим часам.
            if (_noTimeline)
            {
                _pos = TimeSpan.Zero;
                _posAt = DateTimeOffset.Now;
            }
            _art = null;
            _accent = ColorExtractor.Fallback;
            Publish();
            // Новый трек в браузере может оказаться видео — перепроверяем, подходит ли сессия.
            if (_musicOnly && MusicSources.Classify(s.SourceAppUserModelId ?? "") != MusicSources.Kind.Music) SelectSession();
        }

        if (props?.Thumbnail != null)
        {
            var thumb = await LoadThumbnailAsync(props.Thumbnail);
            if (version != _propsVersion || s != _session) return;
            if (thumb is { } t)
            {
                _accent = t.Accent;
                // Плеер прислал ту же картинку ещё раз (свойства меняются по нескольку раз за трек) — оставляем чёткую.
                if (_hiRes is { } hr && hr.Thumb == t.Hash) _art = hr.Art;
                else
                {
                    _art = t.Art;
                    if (HiResArtwork.Worth(t.Art)) _ = UpgradeArtAsync(version, s, title, artist, t);
                }
            }
        }

        Publish();
    }

    /// <summary>Чёткая версия текущей обложки (см. <see cref="HiResArtwork"/>) и отпечаток картинки, которую она заменяет.</summary>
    private (int Thumb, BitmapSource Art)? _hiRes;

    /// <summary>Найти обложку в высоком разрешении и тихо подменить ею текущую (цвета остаются от исходной).</summary>
    private async Task UpgradeArtAsync(int version, Session s, string title, string artist, Thumb thumb)
    {
        var hi = await HiResArtwork.FindAsync(title, artist, thumb.Art);
        if (hi == null || s != _session || !ReferenceEquals(_art, thumb.Art) || title != _title || artist != _artist) return;
        _hiRes = (thumb.Hash, hi);
        _art = hi;
        Publish();
    }

    private void ReadPlayback(bool initial = false)
    {
        if (_session == null) return;
        bool playing;
        try
        {
            var info = _session.GetPlaybackInfo();
            playing = info?.PlaybackStatus == PlaybackStatus.Playing;
            _canShuffle = info?.Controls?.IsShuffleEnabled == true;
            _canRepeat = info?.Controls?.IsRepeatEnabled == true;
            _shuffle = info?.IsShuffleActive == true;
            _repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
        }
        catch { return; }

        if (playing == _playing) return;
        var now = DateTimeOffset.Now;
        if (!playing) _pos = Extrapolate(now);
        _posAt = now;
        _playing = playing;
        // Первое чтение после подключения — это не «запуск»: трек уже шёл, замеру приложения можно верить.
        if (playing && !initial)
        {
            _playSince = now;
            // Приложение могло прислать свежую позицию ещё до события о воспроизведении — применяем её сразу.
            ReadTimeline(force: true);
        }
    }

    /// <summary>Считать таймлайн. Без <paramref name="force"/> — только если приложение прислало новый замер.</summary>
    private bool ReadTimeline(bool force = false)
    {
        if (_session == null) return false;
        try
        {
            var tl = _session.GetTimelineProperties();
            if (tl == null) return false;
            var sample = (Pos: tl.Position - tl.StartTime, At: tl.LastUpdatedTime, End: tl.EndTime - tl.StartTime);
            // Тот же замер, что уже применён, — ничего нового. Даже при force: иначе после паузы или по событию
            // от плеера старая позиция получала текущее время, и трек «откатывался» назад (текст песни
            // перескакивал на прошлые строки или весь становился серым).
            if (sample == _lastTimeline && _lastTimeline != default) return false;
            var previousSample = _lastTimeline;
            _lastTimeline = sample;

            var now = DateTimeOffset.Now;
            // Плеер без таймлайна (часть сайтов в браузере): позиции нет совсем. Тогда идут свои часы трека —
            // от его начала (см. ReadPropertiesAsync), а нулевой замер их не сбрасывает.
            _noTimeline = sample.End <= TimeSpan.Zero && sample.Pos <= TimeSpan.Zero;
            if (_noTimeline) return false;
            var at = sample.At;
            if (at.Year < 2000 || at > now) at = now;
            // Замер сделан ещё на паузе: с тех пор трек шёл только с момента запуска, а не с момента замера.
            if (at < _playSince - TimeSpan.FromMilliseconds(150)) at = _playSince;

            // Скачок назад посреди трека. Браузеры иногда присылают «0» или устаревший замер (буферизация,
            // обновление метаданных) — текст песни на секунду уходил в начало и серел. Настоящую перемотку назад
            // (в самом плеере) подтверждаем: принимаем, если через 1.2 с её не опроверг новый замер.
            var samplePos = sample.Pos + (_playing ? now - at : TimeSpan.Zero);
            var expected = Extrapolate(now);

            // Сразу после перемотки в известную точку плеер ещё какое-то время присылает старую позицию (до перемотки).
            // Раньше её принимали, и текст песни отскакивал от нажатой строки. Первые 3 с верим только замерам рядом
            // с целью; замер «мимо» не запоминаем — если плеер так и не перемотал, через 3 с он будет принят.
            if (now - _seekToAt < TimeSpan.FromSeconds(3) && Math.Abs((samplePos - expected).TotalSeconds) > 2)
            {
                _lastTimeline = previousSample;
                return false;
            }
            bool recentAction = now - _trackChangedAt < TimeSpan.FromSeconds(3) || now - _seekAt < TimeSpan.FromSeconds(3);
            if (!recentAction && expected > TimeSpan.FromSeconds(3) && samplePos < expected - TimeSpan.FromSeconds(2.5))
            {
                _dur = sample.End;
                // Ровно ноль посреди трека — почти всегда сбой: не ждём подтверждения, просто игнорируем.
                _pendingBack = sample.Pos <= TimeSpan.Zero ? null : (samplePos, now);
                return false;
            }
            _pendingBack = null;

            _dur = sample.End;
            _pos = sample.Pos;
            _posAt = _playing ? at : now;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[media] timeline failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Подозрительный скачок назад продержался 1.2 с без опровержения — значит, это настоящая перемотка.</summary>
    private bool AcceptPendingBack()
    {
        if (_pendingBack is not { } p) return false;
        var now = DateTimeOffset.Now;
        if (now - p.At < TimeSpan.FromSeconds(1.2)) return false;
        _pendingBack = null;
        _pos = p.Pos + (_playing ? now - p.At : TimeSpan.Zero);
        _posAt = now;
        return true;
    }

    private TimeSpan Extrapolate(DateTimeOffset now) =>
        _playing ? _pos + (now - _posAt) : _pos;

    private readonly record struct Thumb(BitmapSource Art, Color Accent, int Hash);

    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var ras = await reference.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[media] thumbnail failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Обложка в родном размере (раньше ужималась до 160 px и мылилась на больших видах). Декодирование и подбор
    /// цвета — в фоне, чтобы смена трека не подтормаживала интерфейс.
    /// </summary>
    private static async Task<Thumb?> LoadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        var data = await ReadThumbnailBytesAsync(reference);
        if (data == null || data.Length == 0) return null;
        return await Task.Run<Thumb?>(() =>
        {
            var art = ImageFx.Decode(data);
            if (art == null) return null;
            var hash = new HashCode();
            hash.AddBytes(data);
            return new Thumb(art, ColorExtractor.GetAccent(art), hash.ToHashCode());
        });
    }

    private void Publish()
    {
        Current = _session == null || string.IsNullOrWhiteSpace(_title)
            ? null
            : new MediaSnapshot(_title, _artist, _art, _accent, _playing, _pos, _posAt, _dur,
                _session.SourceAppUserModelId ?? "", _canShuffle, _shuffle, _canRepeat, _repeat,
                MusicSources.AppName(_session.SourceAppUserModelId ?? "") ?? "");
        Changed?.Invoke(Current);
    }

    private void OnUi(Func<Task> work) =>
        _ui.InvokeAsync(async () =>
        {
            try { await work(); }
            catch (Exception ex) { Debug.WriteLine($"[media] {ex}"); }
        });

    /// <summary>Вывести музыкальное приложение-источник на передний план (браузер не трогаем: вкладку не найти).</summary>
    public void OpenSource()
    {
        string app = _session?.SourceAppUserModelId ?? "";
        if (MusicSources.Classify(app) != MusicSources.Kind.Music) return;
        // AUMID задаёт само приложение-источник; в командную строку explorer попадают только безопасные символы.
        if (!System.Text.RegularExpressions.Regex.IsMatch(app, @"^[A-Za-z0-9._!\-]+$")) return;
        try
        {
            if (app.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            {
                // spotify: открывает Spotify или разворачивает уже запущенный (в том числе из трея).
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
                return;
            }
            string exe = Path.GetFileNameWithoutExtension(app.Split('!')[0]);
            var proc = Process.GetProcessesByName(exe).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (proc != null)
            {
                ShowWindow(proc.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(proc.MainWindowHandle);
            }
            else if (app.Contains('!'))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app}") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[media] open source failed: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public void Dispose()
    {
        _rescan.Stop();
        _sync.Stop();
        Attach(null);
    }
}
