using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DynamicIsland
{
    /// <summary>Temporizador y cronómetro compartidos por todas las islas.</summary>
    public sealed class TimerTools : INotifyPropertyChanged
    {
        public event Action? TimerFinished;

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };

        // ---------- Temporizador ----------
        private TimeSpan _total = TimeSpan.FromMinutes(5);
        private TimeSpan _remaining = TimeSpan.FromMinutes(5);
        private DateTime _endsAt;
        public bool TimerRunning { get; private set; }
        public bool TimerActive => TimerRunning || _remaining != _total;

        // ---------- Cronómetro ----------
        private readonly Stopwatch _sw = new();
        public bool StopwatchRunning => _sw.IsRunning;
        public bool StopwatchActive => _sw.IsRunning || _sw.Elapsed > TimeSpan.Zero;
        public ObservableCollection<string> Laps { get; } = new();

        public TimerTools()
        {
            _tick.Tick += (_, _) => Update();
        }

        public string TimerText => Format(_remaining, tenths: false);
        public double TimerProgress => _total.TotalSeconds <= 0 ? 0 : 1 - _remaining.TotalSeconds / _total.TotalSeconds;
        public string TimerButton => TimerRunning ? "Pausar" : (_remaining != _total ? "Reanudar" : "Iniciar");
        public string StopwatchText => Format(_sw.Elapsed, tenths: true);
        public string StopwatchButton => _sw.IsRunning ? "Pausar" : (_sw.Elapsed > TimeSpan.Zero ? "Reanudar" : "Iniciar");

        /// <summary>Texto corto para la isla compacta (null si nada está corriendo).</summary>
        public string? CompactText =>
            TimerRunning ? Format(_remaining, false)
            : _sw.IsRunning ? Format(_sw.Elapsed, false)
            : null;

        public bool CompactIsTimer => TimerRunning;

        public void SetMinutes(double minutes)
        {
            if (TimerRunning) return;
            _total = _remaining = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 600));
            Notify();
        }

        public void AddMinutes(int delta)
        {
            if (TimerRunning)
            {
                _endsAt = _endsAt.AddMinutes(delta);
                if (_endsAt < DateTime.Now.AddSeconds(1)) _endsAt = DateTime.Now.AddSeconds(1);
                _total = TimeSpan.FromSeconds(Math.Max(_total.TotalSeconds + delta * 60, 60));
            }
            else
            {
                var m = Math.Clamp(Math.Round(_remaining.TotalMinutes) + delta, 1, 600);
                _total = _remaining = TimeSpan.FromMinutes(m);
            }
            Update();
        }

        public void ToggleTimer()
        {
            if (TimerRunning)
            {
                _remaining = _endsAt - DateTime.Now;
                TimerRunning = false;
            }
            else
            {
                if (_remaining <= TimeSpan.Zero) _remaining = _total;
                _endsAt = DateTime.Now + _remaining;
                TimerRunning = true;
            }
            EnsureTicking();
            Notify();
        }

        public void ResetTimer()
        {
            TimerRunning = false;
            _remaining = _total;
            EnsureTicking();
            Notify();
        }

        public void ToggleStopwatch()
        {
            if (_sw.IsRunning) _sw.Stop(); else _sw.Start();
            EnsureTicking();
            Notify();
        }

        public void Lap()
        {
            if (_sw.Elapsed <= TimeSpan.Zero) return;
            Laps.Insert(0, $"Vuelta {Laps.Count + 1}   {Format(_sw.Elapsed, true)}");
        }

        public void ResetStopwatch()
        {
            _sw.Reset();
            Laps.Clear();
            EnsureTicking();
            Notify();
        }

        private void Update()
        {
            if (TimerRunning)
            {
                _remaining = _endsAt - DateTime.Now;
                if (_remaining <= TimeSpan.Zero)
                {
                    _remaining = TimeSpan.Zero;
                    TimerRunning = false;
                    Notify();
                    _remaining = _total;
                    TimerFinished?.Invoke();
                }
            }
            EnsureTicking();
            Notify();
        }

        private void EnsureTicking()
        {
            bool need = TimerRunning || _sw.IsRunning;
            if (need && !_tick.IsEnabled) _tick.Start();
            else if (!need && _tick.IsEnabled) _tick.Stop();
        }

        public static string Format(TimeSpan t, bool tenths)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            // Redondeo hacia arriba en el temporizador para que no muestre 00:00 antes de tiempo
            if (!tenths) t = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds - 0.001));
            string s = t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
            return tenths ? $"{s}.{t.Milliseconds / 100}" : s;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify()
        {
            foreach (var p in new[] { nameof(TimerText), nameof(TimerProgress), nameof(TimerButton), nameof(TimerRunning),
                                      nameof(TimerActive), nameof(StopwatchText), nameof(StopwatchButton), nameof(StopwatchRunning),
                                      nameof(StopwatchActive), nameof(CompactText) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }
}
