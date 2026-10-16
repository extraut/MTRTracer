namespace MTRTracer.Core.Mtr;

/// <summary>
/// Running latency statistics with mtr-compatible semantics.
/// Mean/variance use Welford's algorithm; jitter follows mtr (|rtt - previous rtt|) and RFC 3550 (Jint).
/// </summary>
public sealed class LatencyStats
{
    public const int HistorySize = 120;

    public int Sent { get; private set; }
    public int Recv { get; private set; }
    public double Last { get; private set; } = double.NaN;
    public double Best { get; private set; } = double.NaN;
    public double Worst { get; private set; } = double.NaN;

    private double _mean;
    private double _m2;
    private double _logSum;
    private double _prev = double.NaN;
    private double _jitterSum;
    private int _jitterCount;

    public double Jitter { get; private set; } = double.NaN;
    public double JMax { get; private set; } = double.NaN;
    public double JInt { get; private set; } = double.NaN;

    private readonly double[] _history = new double[HistorySize];
    private int _historyCount;
    private int _historyPos;

    public int ConsecutiveLoss { get; private set; }

    public double LossPct => Sent == 0 ? 0 : (Sent - Recv) * 100.0 / Sent;
    public double Avg => Recv == 0 ? double.NaN : _mean;
    public double StDev => Recv < 2 ? double.NaN : Math.Sqrt(_m2 / (Recv - 1));
    public double GMean => Recv == 0 ? double.NaN : Math.Exp(_logSum / Recv);
    public double JAvg => _jitterCount == 0 ? double.NaN : _jitterSum / _jitterCount;

    public void AddSent()
    {
        Sent++;
    }

    public void AddReply(double rtt)
    {
        Recv++;
        Last = rtt;
        Best = double.IsNaN(Best) ? rtt : Math.Min(Best, rtt);
        Worst = double.IsNaN(Worst) ? rtt : Math.Max(Worst, rtt);
        var delta = rtt - _mean;
        _mean += delta / Recv;
        _m2 += delta * (rtt - _mean);
        _logSum += Math.Log(Math.Max(rtt, 0.001));
        if (!double.IsNaN(_prev))
        {
            var j = Math.Abs(rtt - _prev);
            Jitter = j;
            _jitterSum += j;
            _jitterCount++;
            JMax = double.IsNaN(JMax) ? j : Math.Max(JMax, j);
            JInt = double.IsNaN(JInt) ? j : JInt + (j - JInt) / 16.0;
        }
        _prev = rtt;
        ConsecutiveLoss = 0;
        Push(rtt);
    }

    public void AddLoss()
    {
        ConsecutiveLoss++;
        Push(double.NaN);
    }

    private void Push(double v)
    {
        _history[_historyPos] = v;
        _historyPos = (_historyPos + 1) % HistorySize;
        if (_historyCount < HistorySize) _historyCount++;
    }

    /// <summary>Last samples, oldest first. NaN = lost.</summary>
    public double[] History()
    {
        var result = new double[_historyCount];
        var start = (_historyPos - _historyCount + HistorySize) % HistorySize;
        for (var i = 0; i < _historyCount; i++) result[i] = _history[(start + i) % HistorySize];
        return result;
    }

    public double RecentLossPct(int window)
    {
        var h = History();
        var n = Math.Min(window, h.Length);
        if (n == 0) return 0;
        var lost = 0;
        for (var i = h.Length - n; i < h.Length; i++) if (double.IsNaN(h[i])) lost++;
        return lost * 100.0 / n;
    }
}
