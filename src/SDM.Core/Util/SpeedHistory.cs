using System.Globalization;
using System.Text;

namespace SDM.Core.Util;

/// <summary>
/// The last N speed samples (one per second) and their drawing as SVG/WPF path data, for the speed charts.
/// </summary>
public sealed class SpeedHistory
{
    private readonly double[] _samples;
    private int _count;
    private int _next;

    public SpeedHistory(int capacity = 60) => _samples = new double[Math.Max(2, capacity)];

    public int Capacity => _samples.Length;
    public int Count => _count;

    public void Add(double bytesPerSecond)
    {
        _samples[_next] = Math.Max(0, double.IsFinite(bytesPerSecond) ? bytesPerSecond : 0);
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<double> Samples
    {
        get
        {
            var list = new double[_count];
            int start = (_next - _count + _samples.Length) % _samples.Length;
            for (int i = 0; i < _count; i++) list[i] = _samples[(start + i) % _samples.Length];
            return list;
        }
    }

    public double Peak => _count == 0 ? 0 : Samples.Max();
    public double Average => _count == 0 ? 0 : Samples.Average();

    /// <summary>
    /// Line through the samples in a <paramref name="width"/>×<paramref name="height"/> box (newest at the right edge,
    /// y grows downwards). With <paramref name="closed"/> the shape is closed along the bottom for an area fill.
    /// Empty when there are fewer than two samples.
    /// </summary>
    public string ToPath(double width, double height, bool closed = false)
    {
        var samples = Samples;
        if (samples.Count < 2) return "";
        double peak = Math.Max(samples.Max(), 1);
        double step = width / (Capacity - 1);
        double x0 = width - step * (samples.Count - 1);
        var sb = new StringBuilder();
        if (closed) sb.Append("M").Append(F(x0)).Append(',').Append(F(height)).Append(' ');
        for (int i = 0; i < samples.Count; i++)
        {
            double x = x0 + step * i;
            double y = height - samples[i] / peak * height * 0.92;
            sb.Append(i == 0 && !closed ? "M" : "L").Append(F(x)).Append(',').Append(F(y)).Append(' ');
        }
        if (closed) sb.Append("L").Append(F(width)).Append(',').Append(F(height)).Append(" Z");
        return sb.ToString().TrimEnd();
    }

    public void Clear() { _count = 0; _next = 0; }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
