using System;
using System.Windows.Media.Animation;

namespace SnowyNotch.Animation;

/// <summary>
/// Сглаживание «как пружина»: быстро долетает до цели, чуть перелетает и успокаивается.
/// <see cref="Bounce"/> = 0 — без перелёта (просто быстрое затухание), 1 — заметный упругий перелёт (~8 %).
/// </summary>
public sealed class SpringEase : IEasingFunction
{
    public double Bounce { get; set; } = 0.5;

    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double decay = 7.5 - 2.2 * Bounce;   // чем упруже, тем дольше затухает
        double freq = 4 + 6.5 * Bounce;      // и тем заметнее колебание
        double v = 1 - Math.Exp(-decay * t) * Math.Cos(freq * t);
        // В конце отрезка кривая обязана прийти ровно в 1.
        double end = 1 - Math.Exp(-decay) * Math.Cos(freq);
        return v / end;
    }

    public static readonly SpringEase Soft = new() { Bounce = 0.25 };
    public static readonly SpringEase Lively = new() { Bounce = 0.6 };
    public static readonly SpringEase Bouncy = new() { Bounce = 0.9 };
}
