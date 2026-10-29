namespace NotchWin;

/// <summary>
/// Пружинная анимация одного значения, как в Dynamic Island:
/// быстрый старт, небольшой перелёт и мягкая остановка.
/// </summary>
internal sealed class Spring
{
    public double Value;
    public double Target;
    public double Velocity;

    public double Stiffness = 320;  // жёсткость: чем выше, тем быстрее
    public double Damping = 0.72;   // 1 = без перелёта, меньше = пружинистее

    public Spring(double value)
    {
        Value = Target = value;
    }

    public bool IsAtRest => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        // Мелкие шаги, чтобы интегрирование было стабильным при просадке FPS.
        const double maxStep = 1.0 / 240;
        while (dt > 0)
        {
            var h = Math.Min(dt, maxStep);
            var c = 2 * Math.Sqrt(Stiffness) * Damping;
            var accel = Stiffness * (Target - Value) - c * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
        if (IsAtRest)
        {
            Value = Target;
            Velocity = 0;
        }
    }
}
