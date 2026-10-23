using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland
{
    /// <summary>
    /// Animación con física de resorte amortiguado: crece rápido, se pasa un poco y se asienta.
    /// (ζ = 0.62, ω = 9 en tiempo normalizado → ~8 % de rebote y reposo al final de la animación.)
    /// </summary>
    public sealed class SpringEase : EasingFunctionBase
    {
        private const double Damping = 0.62, Frequency = 9.0;

        public SpringEase() => EasingMode = EasingMode.EaseIn; // usamos la curva tal cual

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            double wd = Frequency * Math.Sqrt(1 - Damping * Damping);
            double decay = Math.Exp(-Damping * Frequency * t);
            return 1 - decay * (Math.Cos(wd * t) + Damping * Frequency / wd * Math.Sin(wd * t));
        }

        protected override Freezable CreateInstanceCore() => new SpringEase();
    }
}
