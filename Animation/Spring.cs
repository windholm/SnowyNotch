using System;

namespace SnowyNotch.Animation;

public sealed class Spring
{
    private const double SubStep = 1.0 / 240.0;

    public Spring(double value)
    {
        Value = value;
        Target = value;
    }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }

    public double Stiffness { get; set; } = 300;

    public double Damping { get; set; } = 28;

    public bool IsSettled => Math.Abs(Value - Target) < 0.25 && Math.Abs(Velocity) < 0.5;

    public void Step(double dt)
    {
        while (dt > 0)
        {
            double h = Math.Min(SubStep, dt);
            double accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public void Jump(double value, double velocity = 0)
    {
        Value = value;
        Velocity = velocity;
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
