using System.Windows.Threading;

namespace NotchWin.Services;

public enum TimerMode { Timer, Work, ShortBreak, LongBreak }

/// <summary>Обычный таймер и помодоро (по умолчанию 25 минут работы, 5 отдыха, после 4 циклов 15 минут).</summary>
internal sealed class TimerService
{
    // Длительности помодоро задаются в настройках.
    public static TimeSpan WorkLength => TimeSpan.FromMinutes(Math.Clamp(AppSettings.Current.PomodoroWork, 1, 180));
    public static TimeSpan ShortBreakLength => TimeSpan.FromMinutes(Math.Clamp(AppSettings.Current.PomodoroShortBreak, 1, 60));
    public static TimeSpan LongBreakLength => TimeSpan.FromMinutes(Math.Clamp(AppSettings.Current.PomodoroLongBreak, 1, 90));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _endsAt;

    public TimerMode Mode { get; private set; } = TimerMode.Timer;
    public TimeSpan Length { get; private set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(5);
    public bool IsRunning { get; private set; }
    public bool IsActive => IsRunning || Remaining < Length;  // запущен или стоит на паузе
    public int PomodoroCycle { get; private set; } = 1;
    public bool IsPomodoro => Mode != TimerMode.Timer;

    public event Action? Changed;
    /// <summary>Отрезок закончился. Для помодоро следующий отрезок уже запущен.</summary>
    public event Action<TimerMode>? Finished;

    public TimerService()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public void StartTimer(TimeSpan length)
    {
        Mode = TimerMode.Timer;
        Begin(length);
    }

    public void StartPomodoro()
    {
        PomodoroCycle = 1;
        Mode = TimerMode.Work;
        Begin(WorkLength);
    }

    /// <summary>Задаёт длительность, пока таймер не запущен (шкала минут).</summary>
    public void SetLength(TimeSpan length)
    {
        if (IsRunning) return;
        Mode = TimerMode.Timer;
        Length = length;
        Remaining = length;
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (IsRunning)
        {
            IsRunning = false;
            _timer.Stop();
        }
        else
        {
            if (Remaining <= TimeSpan.Zero) Remaining = Length;
            _endsAt = DateTime.Now + Remaining;
            IsRunning = true;
            _timer.Start();
        }
        Changed?.Invoke();
    }

    public void Reset()
    {
        IsRunning = false;
        _timer.Stop();
        Mode = TimerMode.Timer;
        Remaining = Length;
        Changed?.Invoke();
    }

    public void Add(TimeSpan delta)
    {
        Length += delta;
        if (IsRunning) _endsAt += delta;
        Remaining += delta;
        Changed?.Invoke();
    }

    private void Begin(TimeSpan length)
    {
        Length = length;
        Remaining = length;
        _endsAt = DateTime.Now + length;
        IsRunning = true;
        _timer.Start();
        Changed?.Invoke();
    }

    private void Tick()
    {
        Remaining = _endsAt - DateTime.Now;
        if (Remaining > TimeSpan.Zero)
        {
            Changed?.Invoke();
            return;
        }

        var finished = Mode;
        Remaining = TimeSpan.Zero;
        switch (Mode)
        {
            case TimerMode.Work:
                Mode = PomodoroCycle % 4 == 0 ? TimerMode.LongBreak : TimerMode.ShortBreak;
                Begin(Mode == TimerMode.LongBreak ? LongBreakLength : ShortBreakLength);
                break;
            case TimerMode.ShortBreak:
            case TimerMode.LongBreak:
                PomodoroCycle++;
                Mode = TimerMode.Work;
                Begin(WorkLength);
                break;
            default:
                IsRunning = false;
                _timer.Stop();
                Remaining = Length; // готов к повторному запуску
                Changed?.Invoke();
                break;
        }
        Finished?.Invoke(finished);
    }
}
