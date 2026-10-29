using LightCraftMidi.Core.LightCraft;

namespace LightCraftMidi.Core.Mapping;

/// <summary>Maps raw MIDI values onto a develop control's range and back (for feedback).</summary>
public static class ValueScaler
{
    public static (double Lo, double Hi) Range(MidiMapping m, DevelopControl c)
    {
        double lo = Math.Clamp(m.RangeMin ?? c.Min, c.Min, c.Max);
        double hi = Math.Clamp(m.RangeMax ?? c.Max, c.Min, c.Max);
        return (lo, hi);
    }

    /// <summary>Middle raw value (64 of 0-127, 8192 of 0-16383) — snaps to 0 on ranges centred on zero.</summary>
    public static int Center(int maxRaw) => (maxRaw + 1) / 2;

    private static bool IsSymmetric(double lo, double hi) => lo < 0 && Math.Abs(lo + hi) < 1e-9;

    public static double ToTarget(int raw, int maxRaw, MidiMapping m, DevelopControl c)
    {
        var (lo, hi) = Range(m, c);
        raw = Math.Clamp(raw, 0, maxRaw);
        if (IsSymmetric(lo, hi) && raw == Center(maxRaw)) return 0;

        double n = (double)raw / maxRaw;
        if (m.Invert) n = 1 - n;
        double v = lo + n * (hi - lo);
        if (c.Step > 0) v = Math.Round(v / c.Step) * c.Step;
        v = Math.Round(v, Math.Clamp(c.Decimals + 2, 0, 10));
        return Math.Clamp(v, Math.Min(lo, hi), Math.Max(lo, hi));
    }

    public static int ToMidi(double value, int maxRaw, MidiMapping m, DevelopControl c)
    {
        var (lo, hi) = Range(m, c);
        if (IsSymmetric(lo, hi) && Math.Abs(value) < 1e-9) return Center(maxRaw);
        if (Math.Abs(hi - lo) < 1e-12) return 0;
        double n = Math.Clamp((value - lo) / (hi - lo), 0, 1);
        if (m.Invert) n = 1 - n;
        return (int)Math.Round(n * maxRaw);
    }
}
