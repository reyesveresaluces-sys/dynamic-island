using System.Windows;
using System.Windows.Controls;

namespace DynamicIsland
{
    /// <summary>Espacio "Temporizador": temporizador con atajos y cronómetro con vueltas.</summary>
    public partial class TimerSpace : UserControl
    {
        private static TimerTools T => IslandTools.Timers;

        public TimerSpace()
        {
            InitializeComponent();
            DataContext = IslandTools.Timers;
        }

        private void Minus_Click(object sender, RoutedEventArgs e) => T.AddMinutes(-1);
        private void Plus_Click(object sender, RoutedEventArgs e) => T.AddMinutes(1);

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string s && double.TryParse(s, out var m)) T.SetMinutes(m);
        }

        private void TimerToggle_Click(object sender, RoutedEventArgs e) => T.ToggleTimer();
        private void TimerReset_Click(object sender, RoutedEventArgs e) => T.ResetTimer();
        private void SwToggle_Click(object sender, RoutedEventArgs e) => T.ToggleStopwatch();
        private void Lap_Click(object sender, RoutedEventArgs e) => T.Lap();
        private void SwReset_Click(object sender, RoutedEventArgs e) => T.ResetStopwatch();
    }
}
