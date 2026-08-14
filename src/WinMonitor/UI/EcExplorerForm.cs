using System.Globalization;
using WinMonitor.Config;
using WinMonitor.Core;
using WinMonitor.Localization;

namespace WinMonitor.UI;

/// <summary>
/// Interactive Embedded-Controller explorer. Live-dumps all 256 EC registers and gives the user
/// three purpose-built tools for finding the (undocumented, model-specific) fan / thermal registers
/// on laptops like the LG gram:
///   1. Correlation auto-finder — ranks every register / adjacent word by how strongly it tracks the
///      live CPU temperature or load. A real tacho/PWM rises to the top after ~1 minute of normal use.
///   2. Value search — highlights the register (or word) that currently holds a number the user sees
///      in another tool (HWiNFO / LG software), so they can locate the RPM source directly.
///   3. Idle/Load snapshot diff — capture idle (A) then loaded (B) and see only the biggest movers.
/// Any register (or register pair) can then be promoted into a named sensor.
/// </summary>
public sealed class EcExplorerForm : Form
{
    private const int Grid = 16;                // 16x16 = 256 registers
    private const int RecentHighlightMs = 4000; // a register stays highlighted this long after it changes
    private const int WindowSize = 90;          // rolling correlation window (~90 s at 1 Hz)
    private const int MinSamplesForCorr = 20;   // need at least this many samples before ranking
    private const int TopCandidates = 12;       // rows shown in the "likely fan / thermal" list
    private const int FindTolerancePermille = 30; // word match tolerance for value search: ±3%

    private readonly EmbeddedController _ec;
    private readonly EcConfig _config;
    private readonly Action _onChanged;                 // persist + rescan sensors
    private readonly Func<(float temp, float load)> _cpuThermal;

    private readonly GridPanel _grid;
    private readonly Label _detail;
    private readonly Label _hint;
    private readonly ListBox _sensorList;
    private readonly Button _addButton, _removeButton, _resetButton, _closeButton;
    private readonly System.Windows.Forms.Timer _timer;
    private System.Drawing.Icon? _windowIcon;   // owned: ExtractAssociatedIcon result, freed in Dispose

    // --- fan-finder tool controls ---
    private readonly ListBox _candidateList;
    private readonly TextBox _findBox;
    private readonly Button _captureAButton, _captureBButton;
    private readonly ListBox _diffList;

    private readonly byte[] _values = new byte[256];
    private readonly bool[] _ok = new bool[256];
    private readonly int[] _min = new int[256];
    private readonly int[] _max = new int[256];
    private readonly long[] _lastChangeTick = new long[256];
    private int _selected = -1;

    // --- correlation rolling window (frame-major, allocated once, no per-tick allocation) ---
    private readonly byte[][] _frames = new byte[WindowSize][]; // each frame = 256 register bytes
    private readonly float[] _tempWin = new float[WindowSize];  // CPU package temp per frame (NaN = unknown)
    private readonly float[] _loadWin = new float[WindowSize];  // CPU total load per frame (NaN = unknown)
    private int _frameCount;                                    // frames captured (caps at WindowSize)
    private int _frameHead;                                     // next write slot (circular)

    // Ranking scratch reused each poll (no allocation in the recompute path).
    private readonly int[] _candSeries = new int[TopCandidates];  // 0..255 byte reg, 256..510 -> word at (idx-256)
    private readonly float[] _candScore = new float[TopCandidates];
    private readonly bool[] _candLoad = new bool[TopCandidates];  // true = correlated with load, false = temp
    private int _candCount;

    // --- value search ---
    private int _findTarget = -1;                    // parsed "find value", -1 = none
    private readonly bool[] _findHit = new bool[256]; // per-register: this register participates in a match

    // --- snapshot diff ---
    private readonly int[] _snapA = new int[256];  // -1 = not captured
    private readonly int[] _snapB = new int[256];
    private bool _haveA, _haveB;

    public EcExplorerForm(EmbeddedController ec, EcConfig config, Action onChanged, Func<(float temp, float load)> cpuThermal)
    {
        _ec = ec;
        _config = config;
        _onChanged = onChanged;
        _cpuThermal = cpuThermal;

        Text = Loc.T("ec.title");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(860, 560);
        Size = new Size(920, 600);
        // Owned by this form: ExtractAssociatedIcon transfers ownership and Form.Dispose does not
        // release an Icon it was merely assigned.
        try { Icon = _windowIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Theme.WindowBack;

        for (int i = 0; i < 256; i++) { _min[i] = int.MaxValue; _max[i] = int.MinValue; _snapA[i] = -1; _snapB[i] = -1; }
        for (int f = 0; f < WindowSize; f++) _frames[f] = new byte[256];

        _hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(8, 6, 8, 4),
            Text = Loc.T("ec.hint"),
            ForeColor = SystemColors.GrayText,
        };

        _grid = new GridPanel { Dock = DockStyle.Fill };
        _grid.CellSelected += OnCellSelected;

        _detail = new Label
        {
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(6),
            TextAlign = ContentAlignment.TopLeft,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        // Right-side tool panel: correlation candidates (top), value search + capture diff (middle),
        // monitored EC sensors + actions (bottom).
        var right = new Panel { Dock = DockStyle.Right, Width = 340, Padding = new Padding(6) };

        // --- monitored sensors (bottom of the panel) ---
        var sensorsLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = Loc.T("ec.defined"), Font = new Font(Font, FontStyle.Bold) };
        _sensorList = new ListBox { Dock = DockStyle.Top, Height = 96, IntegralHeight = false };
        _sensorList.SelectedIndexChanged += (_, _) => UpdateButtons();

        var sensorButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _addButton = new Button { Text = Loc.T("ec.add"), Width = 190, Height = 28 };
        _addButton.Click += OnAdd;
        _removeButton = new Button { Text = Loc.T("ec.remove"), Width = 64, Height = 28 };
        _removeButton.Click += OnRemove;
        _resetButton = new Button { Text = Loc.T("ec.reset_minmax"), Width = 64, Height = 28 };
        _resetButton.Click += (_, _) => ResetMinMax();
        sensorButtons.Controls.Add(_addButton);
        sensorButtons.Controls.Add(_removeButton);
        sensorButtons.Controls.Add(_resetButton);

        var sensorGroup = new Panel { Dock = DockStyle.Bottom, Height = 152 };
        // Add last-first so Dock=Top stacks visually as label, list, buttons (top to bottom).
        sensorGroup.Controls.Add(sensorButtons);
        sensorGroup.Controls.Add(_sensorList);
        sensorGroup.Controls.Add(sensorsLabel);

        // --- value search + capture diff (middle) ---
        var findLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = Loc.T("ec.find_value"), Font = new Font(Font, FontStyle.Bold) };
        _findBox = new TextBox { Dock = DockStyle.Top };
        _findBox.TextChanged += (_, _) => { ParseFindValue(); RecomputeFindHits(); _grid.Invalidate(); };

        var captureButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        _captureAButton = new Button { Text = Loc.T("ec.capture_a"), Width = 100, Height = 26 };
        _captureAButton.Click += (_, _) => CaptureSnapshot(isA: true);
        _captureBButton = new Button { Text = Loc.T("ec.capture_b"), Width = 100, Height = 26 };
        _captureBButton.Click += (_, _) => CaptureSnapshot(isA: false);
        captureButtons.Controls.Add(_captureAButton);
        captureButtons.Controls.Add(_captureBButton);

        var diffLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = Loc.T("ec.compare"), Font = new Font(Font, FontStyle.Bold) };
        _diffList = new ListBox { Dock = DockStyle.Top, Height = 90, IntegralHeight = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
        _diffList.DoubleClick += (_, _) => SelectFromDiff();

        var midGroup = new Panel { Dock = DockStyle.Bottom, Height = 200 };
        // Add in reverse (Dock=Top stacking honors add order top-to-bottom when added last-first).
        midGroup.Controls.Add(_diffList);
        midGroup.Controls.Add(diffLabel);
        midGroup.Controls.Add(captureButtons);
        midGroup.Controls.Add(_findBox);
        midGroup.Controls.Add(findLabel);

        // --- correlation candidates (fills the top of the panel) ---
        var candLabel = new Label { Dock = DockStyle.Top, Height = 20, Text = Loc.T("ec.correlation"), Font = new Font(Font, FontStyle.Bold) };
        var candHelp = new Label { Dock = DockStyle.Top, Height = 30, Text = Loc.T("ec.likely_header"), ForeColor = SystemColors.GrayText, Padding = new Padding(0, 0, 0, 2) };
        _candidateList = new ListBox { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 8.5f) };
        _candidateList.DoubleClick += (_, _) => SelectAndAddFromCandidate();

        right.Controls.Add(_candidateList);
        right.Controls.Add(candHelp);
        right.Controls.Add(candLabel);
        right.Controls.Add(midGroup);
        right.Controls.Add(sensorGroup);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        _closeButton = new Button { Text = Loc.T("common.close"), Width = 90, Height = 28, DialogResult = DialogResult.OK };
        _closeButton.Click += (_, _) => Close();
        bottom.Controls.Add(_closeButton);

        Controls.Add(_grid);
        Controls.Add(_detail);
        Controls.Add(right);
        Controls.Add(_hint);
        Controls.Add(bottom);

        RefreshSensorList();
        UpdateButtons();
        UpdateCaptureButtons();
        RefreshDiffList();      // show the "capture A then B" hint before any capture
        RefreshCandidateList(); // show the "collecting samples" hint before the first poll

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll(); // immediate first read
    }

    private volatile bool _dumpInFlight;

    private void Poll()
    {
        if (!_ec.Available) { _detail.Text = Loc.T("ec.unavailable"); return; }
        if (_dumpInFlight) return;   // never let a slow EC dump queue up on the UI thread
        _dumpInFlight = true;

        // Sample the CPU thermal state on the UI thread (cheap, cached snapshot) so it lines up with
        // the dump we are about to marshal back. The 256-register dump itself runs off the UI thread
        // (it is time-budgeted inside EmbeddedController) so the window never hangs.
        (float temp, float load) thermal;
        try { thermal = _cpuThermal(); } catch { thermal = (float.NaN, float.NaN); }

        Task.Run(() =>
        {
            byte[] data; bool[] ok;
            try { data = _ec.Dump(out ok); }
            catch { data = new byte[256]; ok = new bool[256]; }
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(() => { ApplyDump(data, ok, thermal.temp, thermal.load); _dumpInFlight = false; }));
                else
                    _dumpInFlight = false;
            }
            catch { _dumpInFlight = false; }
        });
    }

    private void ApplyDump(byte[] data, bool[] ok, float cpuTemp, float cpuLoad)
    {
        if (IsDisposed) return;
        long now = Environment.TickCount64;
        int count = Math.Min(256, Math.Min(data.Length, ok.Length));
        for (int i = 0; i < count; i++)
        {
            _ok[i] = ok[i];
            if (!ok[i]) continue;
            int v = data[i];
            if (v != _values[i]) _lastChangeTick[i] = now;
            _values[i] = (byte)v;
            if (v < _min[i]) _min[i] = v;
            if (v > _max[i]) _max[i] = v;
        }

        PushFrame(cpuTemp, cpuLoad);
        RecomputeCandidates();
        RecomputeFindHits();

        _grid.Invalidate();
        UpdateDetail();
        RefreshSensorValues();
    }

    // ---------- correlation auto-finder ----------

    /// <summary>Appends the current register values + CPU thermal state to the circular window.</summary>
    private void PushFrame(float cpuTemp, float cpuLoad)
    {
        var frame = _frames[_frameHead];
        Buffer.BlockCopy(_values, 0, frame, 0, 256);
        _tempWin[_frameHead] = cpuTemp;
        _loadWin[_frameHead] = cpuLoad;
        _frameHead = (_frameHead + 1) % WindowSize;
        if (_frameCount < WindowSize) _frameCount++;
    }

    /// <summary>
    /// Ranks every register byte and every adjacent LE-16 word by |Pearson r| against CPU temp and
    /// load over the rolling window; keeps the top <see cref="TopCandidates"/>. Runs entirely on
    /// preallocated scratch — no allocation, no LINQ — so it is safe to call each poll.
    /// </summary>
    private void RecomputeCandidates()
    {
        _candCount = 0;
        int n = _frameCount;
        if (n < MinSamplesForCorr) { RefreshCandidateList(); return; }

        // Quick pass: how many frames have a valid (non-NaN) temp / load target.
        int tN = 0, lN = 0;
        for (int f = 0; f < n; f++)
        {
            if (!float.IsNaN(_tempWin[f])) tN++;
            if (!float.IsNaN(_loadWin[f])) lN++;
        }
        bool haveTemp = tN >= MinSamplesForCorr;
        bool haveLoad = lN >= MinSamplesForCorr;
        if (!haveTemp && !haveLoad) { RefreshCandidateList(); return; }

        // 511 candidate series: 0..255 = register byte, 256..510 = LE16 word at (idx-256).
        for (int series = 0; series <= 510; series++)
        {
            bool isWord = series >= 256;
            int reg = isWord ? series - 256 : series;
            if (isWord && reg >= 0xFF) continue;

            // Ignore constant / near-constant series (idle flicker of ±1 must not qualify).
            float minStd = isWord ? 4f : 1.5f;

            float best = 0f; bool bestLoad = false;
            if (haveTemp) { float r = PearsonGated(reg, isWord, _tempWin, minStd, n); if (Math.Abs(r) > Math.Abs(best)) { best = r; bestLoad = false; } }
            if (haveLoad) { float r = PearsonGated(reg, isWord, _loadWin, minStd, n); if (Math.Abs(r) > Math.Abs(best)) { best = r; bestLoad = true; } }

            float score = Math.Abs(best);
            if (score < 0.35f) continue; // weak — not worth listing

            InsertCandidate(series, score, bestLoad);
        }

        RefreshCandidateList();
    }

    /// <summary>Value of candidate <paramref name="reg"/> (byte or LE16 word) in window frame f.</summary>
    private float SeriesValue(int f, int reg, bool isWord)
    {
        var frame = _frames[f];
        if (!isWord) return frame[reg];
        return frame[reg] | (frame[reg + 1] << 8);
    }

    /// <summary>
    /// Pearson r between a register/word series and a target series, computed in a single pass over
    /// exactly the frames where the target is non-NaN (so series and target stats share one frame
    /// set — the mismatched-mean bug that let r exceed 1 is gone). Also gates out near-constant
    /// series (idle ±1 flicker) using the series std over the same frames. Uses the computational
    /// form r = (c·Σst − Σs·Σt) / √((c·Σss − Σs²)(c·Σtt − Σt²)).
    /// </summary>
    private float PearsonGated(int reg, bool isWord, float[] target, float minStd, int n)
    {
        double sumS = 0, sumT = 0, sumSS = 0, sumTT = 0, sumST = 0;
        int c = 0;
        for (int f = 0; f < n; f++)
        {
            float y = target[f];
            if (float.IsNaN(y)) continue;
            float x = SeriesValue(f, reg, isWord);
            sumS += x; sumT += y; sumSS += x * x; sumTT += y * y; sumST += x * y; c++;
        }
        if (c < MinSamplesForCorr) return 0f;

        double denomS = c * sumSS - sumS * sumS;   // = c² · var(series)
        double denomT = c * sumTT - sumT * sumT;   // = c² · var(target)
        if (denomS <= 1e-9 || denomT <= 1e-9) return 0f;

        double seriesStd = Math.Sqrt(denomS) / c;
        if (seriesStd < minStd) return 0f;

        double r = (c * sumST - sumS * sumT) / Math.Sqrt(denomS * denomT);
        return (float)r;   // mathematically in [-1,1]
    }

    /// <summary>Insertion-sort the candidate into the fixed top-N table (descending score).</summary>
    private void InsertCandidate(int series, float score, bool load)
    {
        int pos = _candCount;
        if (_candCount < TopCandidates) _candCount++;
        else if (score <= _candScore[TopCandidates - 1]) return; // not good enough for the table
        else pos = TopCandidates - 1;

        while (pos > 0 && _candScore[pos - 1] < score)
        {
            _candSeries[pos] = _candSeries[pos - 1];
            _candScore[pos] = _candScore[pos - 1];
            _candLoad[pos] = _candLoad[pos - 1];
            pos--;
        }
        _candSeries[pos] = series;
        _candScore[pos] = score;
        _candLoad[pos] = load;
    }

    private void RefreshCandidateList()
    {
        _candidateList.BeginUpdate();
        _candidateList.Items.Clear();
        if (_frameCount < MinSamplesForCorr)
        {
            _candidateList.Items.Add(Loc.F("ec.collecting", _frameCount, MinSamplesForCorr));
        }
        else if (_candCount == 0)
        {
            _candidateList.Items.Add(Loc.T("ec.no_candidates"));
        }
        else
        {
            for (int i = 0; i < _candCount; i++)
            {
                int series = _candSeries[i];
                bool isWord = series >= 256;
                int reg = isWord ? series - 256 : series;
                // Only show a live "cur" when the register(s) actually read this poll.
                bool curOk = _ok[reg] && (!isWord || (reg < 0xFF && _ok[reg + 1]));
                string cur = curOk ? (isWord ? (_values[reg] | (_values[reg + 1] << 8)) : _values[reg]).ToString(CultureInfo.CurrentCulture) : "?";
                string kind = isWord ? "LE16" : "byte";
                string tgt = _candLoad[i] ? Loc.T("ec.target_load") : Loc.T("ec.target_temp");
                _candidateList.Items.Add(string.Format(CultureInfo.CurrentCulture,
                    "0x{0:X2} {1,-4} r={2:0.00} ({3}) cur={4}", reg, kind, _candScore[i], tgt, cur));
            }
        }
        _candidateList.EndUpdate();
    }

    /// <summary>Double-click a candidate: select its register and pre-open Add with a guessed kind.</summary>
    private void SelectAndAddFromCandidate()
    {
        int i = _candidateList.SelectedIndex;
        if (i < 0 || i >= _candCount || _frameCount < MinSamplesForCorr) return;
        int series = _candSeries[i];
        bool isWord = series >= 256;
        int reg = isWord ? series - 256 : series;

        _selected = reg;
        _grid.SetSelected(reg);
        UpdateDetail();
        UpdateButtons();

        int guessedKind = GuessKind(reg, isWord);
        OpenAddDialog(guessedKind);
    }

    /// <summary>
    /// Sensible default interpretation for the Add dialog: an LE16 word whose current value is in
    /// plausible fan-RPM territory → RpmDirect; a byte in 0..100 → Percent; otherwise RawByte.
    /// Returns the EcSensorDialog kind index (see EcSensorDialog._kind order).
    /// </summary>
    private int GuessKind(int reg, bool isWord)
    {
        if (isWord && reg < 0xFF && _ok[reg] && _ok[reg + 1])
        {
            int word = _values[reg] | (_values[reg + 1] << 8);
            if (word is >= 300 and <= 12000) return 3; // RPM (direct word)
            return 1; // Word (LE/BE)
        }
        int b = _values[reg];
        if (b <= 100) return 2; // Percent
        return 0; // RawByte
    }

    // ---------- value search ----------

    private void ParseFindValue()
    {
        string s = _findBox.Text.Trim();
        if (s.Length == 0) { _findTarget = -1; return; }
        // Accept hex (0x..) or decimal.
        bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string body = hex ? s.Substring(2) : s;
        if (int.TryParse(body, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= 0)
            _findTarget = v;
        else
            _findTarget = -1;
    }

    /// <summary>
    /// Marks every register that participates in a match for the current "find value": an exact byte
    /// equal, or an LE16/BE16 word (with the next register) within ±3%. Both bytes of a matching word
    /// are flagged so the pair lights up. Cheap — runs each poll and on text change.
    /// </summary>
    private void RecomputeFindHits()
    {
        Array.Clear(_findHit, 0, _findHit.Length);
        int target = _findTarget;
        if (target < 0) return;

        int tol = (int)((long)target * FindTolerancePermille / 1000);
        for (int i = 0; i < 256; i++)
        {
            if (!_ok[i]) continue;
            if (target <= 0xFF && _values[i] == target) { _findHit[i] = true; }
            if (i < 0xFF && _ok[i + 1])
            {
                int le = _values[i] | (_values[i + 1] << 8);
                int be = (_values[i] << 8) | _values[i + 1];
                if (Math.Abs(le - target) <= tol || Math.Abs(be - target) <= tol)
                {
                    _findHit[i] = true;
                    _findHit[i + 1] = true;
                }
            }
        }
    }

    // ---------- idle/load snapshot diff ----------

    private void CaptureSnapshot(bool isA)
    {
        var dst = isA ? _snapA : _snapB;
        for (int i = 0; i < 256; i++) dst[i] = _ok[i] ? _values[i] : -1;
        if (isA) _haveA = true; else _haveB = true;
        UpdateCaptureButtons();
        RefreshDiffList();
    }

    private void UpdateCaptureButtons()
    {
        _captureAButton.Text = _haveA ? Loc.T("ec.capture_a_done") : Loc.T("ec.capture_a");
        _captureBButton.Text = _haveB ? Loc.T("ec.capture_b_done") : Loc.T("ec.capture_b");
    }

    private void RefreshDiffList()
    {
        _diffList.BeginUpdate();
        _diffList.Items.Clear();
        if (!_haveA || !_haveB)
        {
            _diffList.Items.Add(Loc.T("ec.compare_hint"));
            _diffList.EndUpdate();
            return;
        }

        // Rank registers by |B-A| descending, filtered to |delta|>=3. Fixed-size selection over 256
        // entries — cheap and allocation-free apart from the (few) list captions.
        int shown = 0;
        // Simple approach: repeatedly find the next-largest remaining delta (max 32 rows).
        const int maxRows = 32;
        Span<bool> used = stackalloc bool[256];
        for (int k = 0; k < maxRows; k++)
        {
            int bestIdx = -1, bestDelta = 2; // strictly > 2 => |delta|>=3
            for (int i = 0; i < 256; i++)
            {
                if (used[i] || _snapA[i] < 0 || _snapB[i] < 0) continue;
                int d = Math.Abs(_snapB[i] - _snapA[i]);
                if (d > bestDelta) { bestDelta = d; bestIdx = i; }
            }
            if (bestIdx < 0) break;
            used[bestIdx] = true;
            int a = _snapA[bestIdx], b = _snapB[bestIdx];
            int signed = b - a;
            _diffList.Items.Add(string.Format(CultureInfo.CurrentCulture,
                "0x{0:X2}  {1,3} -> {2,3}  {3}{4}", bestIdx, a, b, signed >= 0 ? "+" : "", signed));
            shown++;
        }
        if (shown == 0) _diffList.Items.Add(Loc.T("ec.compare_none"));
        _diffList.EndUpdate();
    }

    private void SelectFromDiff()
    {
        int i = _diffList.SelectedIndex;
        if (!_haveA || !_haveB || i < 0) return;
        string? cap = _diffList.Items[i] as string;
        if (cap is null || !cap.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
        if (int.TryParse(cap.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int reg) && reg is >= 0 and < 256)
        {
            _selected = reg;
            _grid.SetSelected(reg);
            UpdateDetail();
            UpdateButtons();
        }
    }

    // ---------- selection / detail ----------

    private void OnCellSelected(int index)
    {
        _selected = index;
        UpdateDetail();
        UpdateButtons();
    }

    private void UpdateDetail()
    {
        if (_selected < 0)
        {
            _detail.Text = Loc.T("ec.select_prompt");
            return;
        }
        int i = _selected;
        int lo = _values[i];
        string word = i < 0xFF && _ok[i] && _ok[i + 1]
            ? $"  LE16=0x{_values[i] | (_values[i + 1] << 8):X4} ({_values[i] | (_values[i + 1] << 8)})  BE16=0x{(_values[i] << 8) | _values[i + 1]:X4} ({(_values[i] << 8) | _values[i + 1]})"
            : "";
        string range = _min[i] <= _max[i] ? $"min {_min[i]} / max {_max[i]} / Δ{_max[i] - _min[i]}" : "—";
        _detail.Text =
            $"Reg 0x{i:X2} ({i}):  dec={lo}  hex=0x{lo:X2}  bin={Convert.ToString(lo, 2).PadLeft(8, '0')}\r\n" +
            $"{range}{word}\r\n" +
            (LikelyFan(i) ? Loc.T("ec.likely_fan") : "");
    }

    /// <summary>Heuristic: an adjacent pair whose LE/BE value has ranged through plausible RPM territory.</summary>
    private bool LikelyFan(int i)
    {
        if (i >= 0xFF) return false;
        int spanLo = _max[i] - _min[i];
        if (spanLo < 3) return false; // static byte
        return true;
    }

    private void OnAdd(object? sender, EventArgs e) => OpenAddDialog(-1);

    /// <summary>Opens the Add-as-sensor dialog for the selected register, optionally pre-selecting a kind.</summary>
    private void OpenAddDialog(int guessedKindIndex)
    {
        if (_selected < 0) return;
        // Freeze polling while the modal dialog is open: the dialog previews a fixed register
        // snapshot, and there is no point re-running the 511-series correlation under the modal loop.
        _timer.Stop();
        try
        {
            using var dlg = new EcSensorDialog(_selected, _values, _ok, guessedKindIndex);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null) return;
            // Replace any existing def on the same register+kind.
            _config.Sensors.RemoveAll(s => s.Register == dlg.Result.Register && s.Kind == dlg.Result.Kind);
            _config.Sensors.Add(dlg.Result);
            _config.Enabled = true;
            _onChanged();
            RefreshSensorList();
        }
        finally
        {
            if (!IsDisposed) _timer.Start();
        }
    }

    private void OnRemove(object? sender, EventArgs e)
    {
        if (_sensorList.SelectedIndex < 0 || _sensorList.SelectedIndex >= _config.Sensors.Count) return;
        _config.Sensors.RemoveAt(_sensorList.SelectedIndex);
        _onChanged();
        RefreshSensorList();
    }

    private void ResetMinMax()
    {
        for (int i = 0; i < 256; i++) { _min[i] = _ok[i] ? _values[i] : int.MaxValue; _max[i] = _ok[i] ? _values[i] : int.MinValue; }
        _grid.Invalidate();
        UpdateDetail();
    }

    private void RefreshSensorList()
    {
        _sensorList.BeginUpdate();
        _sensorList.Items.Clear();
        foreach (var s in _config.Sensors)
            _sensorList.Items.Add($"0x{s.Register:X2} {s.DisplayName} [{s.Kind}]");
        _sensorList.EndUpdate();
        UpdateButtons();
    }

    private void RefreshSensorValues()
    {
        // Recompute live values into the list captions without rebuilding (cheap, few items).
        for (int idx = 0; idx < _config.Sensors.Count && idx < _sensorList.Items.Count; idx++)
        {
            var s = _config.Sensors[idx];
            float? v = s.Compute(_values, _ok);
            string val = v is { } f ? Units.Format(s.Quantity, f) : "—";
            string caption = $"0x{s.Register:X2} {s.DisplayName} = {val}";
            if (!string.Equals(_sensorList.Items[idx] as string, caption, StringComparison.Ordinal))
                _sensorList.Items[idx] = caption;
        }
    }

    private void UpdateButtons()
    {
        _addButton.Enabled = _selected >= 0;
        _removeButton.Enabled = _sensorList.SelectedIndex >= 0;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _detail.Font.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Releases the window icon here rather than in OnFormClosed: a form can be disposed without
    /// ever being shown or closed, and the icon handle is ours either way.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Icon = null;
            _windowIcon?.Dispose();
            _windowIcon = null;
        }
        base.Dispose(disposing);
    }

    // ---------- owner-drawn 16x16 register grid ----------

    private sealed class GridPanel : Panel
    {
        public event Action<int>? CellSelected;
        private int _selected = -1;

        // Shared with the owner form via reflection-free direct references set below.
        internal byte[] Values = Array.Empty<byte>();
        internal bool[] Ok = Array.Empty<bool>();
        internal long[] LastChange = Array.Empty<long>();
        internal bool[] FindHit = Array.Empty<bool>();

        public GridPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        /// <summary>Programmatic selection (from a candidate / diff double-click).</summary>
        public void SetSelected(int idx)
        {
            _selected = idx;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int idx = HitTest(e.Location);
            if (idx >= 0)
            {
                _selected = idx;
                CellSelected?.Invoke(idx);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        private int HitTest(Point p)
        {
            var (ox, oy, cw, ch) = Metrics();
            if (cw <= 0 || ch <= 0) return -1;
            int col = (p.X - ox) / cw;
            int row = (p.Y - oy) / ch;
            if (col < 0 || col >= Grid || row < 0 || row >= Grid) return -1;
            return row * Grid + col;
        }

        private (int ox, int oy, int cw, int ch) Metrics()
        {
            int headerX = 34, headerY = 22;
            int cw = (Width - headerX - 4) / Grid;
            int ch = (Height - headerY - 4) / Grid;
            return (headerX, headerY, cw, ch);
        }

        // Per-paint GDI churn used to be roughly 264 objects a second while the explorer was open:
        // two fonts, two brushes, three pens, a StringFormat, and one SolidBrush per cell. All of
        // them are cached now. The cell brush is recoloured in place rather than drawn from a fixed
        // palette because the "recently changed" highlight fades through alpha.
        private Font? _cellFont;
        private float _cellFontSize;
        private Font? _headFont;
        private SolidBrush? _headBrush, _textBrush, _cellBrush;
        private Pen? _gridPen, _selPen, _findPen;
        private StringFormat? _fmt;

        private void EnsureResources(float cellFontSize)
        {
            if (_cellFont is null || Math.Abs(_cellFontSize - cellFontSize) > 0.01f)
            {
                _cellFont?.Dispose();
                _cellFont = new Font("Segoe UI", cellFontSize);
                _cellFontSize = cellFontSize;
            }
            _headFont ??= new Font("Consolas", 8f);
            _headBrush ??= new SolidBrush(SystemColors.GrayText);
            _textBrush ??= new SolidBrush(SystemColors.ControlText);
            _cellBrush ??= new SolidBrush(SystemColors.Window);
            _gridPen ??= new Pen(Color.FromArgb(225, 225, 225));
            _selPen ??= new Pen(Color.FromArgb(0, 120, 215), 2f);
            _findPen ??= new Pen(Color.FromArgb(0, 153, 51), 2f);
            _fmt ??= new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cellFont?.Dispose(); _cellFont = null;
                _headFont?.Dispose(); _headFont = null;
                _headBrush?.Dispose(); _headBrush = null;
                _textBrush?.Dispose(); _textBrush = null;
                _cellBrush?.Dispose(); _cellBrush = null;
                _gridPen?.Dispose(); _gridPen = null;
                _selPen?.Dispose(); _selPen = null;
                _findPen?.Dispose(); _findPen = null;
                _fmt?.Dispose(); _fmt = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SystemColors.Window);
            var (ox, oy, cw, ch) = Metrics();
            if (cw <= 2 || ch <= 2) return;

            long now = Environment.TickCount64;
            EnsureResources(Math.Max(6.5f, Math.Min(9f, ch * 0.32f)));
            Font font = _cellFont!, headFont = _headFont!;
            SolidBrush headBrush = _headBrush!, textBrush = _textBrush!, bgBrush = _cellBrush!;
            Pen gridPen = _gridPen!, selPen = _selPen!, findPen = _findPen!;
            StringFormat fmt = _fmt!;

            for (int c = 0; c < Grid; c++)
                g.DrawString(c.ToString("X"), headFont, headBrush, new RectangleF(ox + c * cw, 2, cw, oy), fmt);
            for (int r = 0; r < Grid; r++)
                g.DrawString((r * Grid).ToString("X2"), headFont, headBrush, new RectangleF(0, oy + r * ch, ox, ch), fmt);

            for (int i = 0; i < 256; i++)
            {
                int r = i / Grid, c = i % Grid;
                var rect = new Rectangle(ox + c * cw, oy + r * ch, cw, ch);

                bool find = FindHit.Length > i && FindHit[i];
                Color bg = SystemColors.Window;
                bool ok = Ok.Length > i && Ok[i];
                if (find) bg = Color.FromArgb(198, 239, 206);          // value-search match: distinct green
                else if (!ok) bg = Color.FromArgb(245, 245, 245);
                else if (LastChange.Length > i)
                {
                    long age = now - LastChange[i];
                    if (LastChange[i] != 0 && age < RecentHighlightMs)
                    {
                        int a = (int)(160 * (1.0 - age / (double)RecentHighlightMs));
                        bg = Color.FromArgb(Math.Max(30, a), 255, 214, 102); // fading amber
                    }
                }
                if (bgBrush.Color != bg) bgBrush.Color = bg;
                g.FillRectangle(bgBrush, rect);
                g.DrawRectangle(gridPen, rect);

                string txt = ok ? Values[i].ToString("X2") : "··";
                g.DrawString(txt, font, ok ? textBrush : headBrush, rect, fmt);

                if (find) g.DrawRectangle(findPen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
                if (i == _selected) g.DrawRectangle(selPen, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            }
        }
    }

    // Bind the grid's data arrays after construction (kept in the form; grid just renders them).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
        _grid.Values = _values;
        _grid.Ok = _ok;
        _grid.LastChange = _lastChangeTick;
        _grid.FindHit = _findHit;
    }
}

/// <summary>Small modal dialog to promote an EC register into a named sensor definition.</summary>
internal sealed class EcSensorDialog : Form
{
    public EcSensorDef? Result { get; private set; }

    private readonly int _register;
    private readonly TextBox _name;
    private readonly ComboBox _kind;
    private readonly ComboBox _quantity;
    private readonly NumericUpDown _scale;
    private readonly NumericUpDown _divisor;
    private readonly CheckBox _bigEndian;
    private readonly Label _preview;
    private readonly byte[] _values;
    private readonly bool[] _ok;

    public EcSensorDialog(int register, byte[] values, bool[] ok, int initialKindIndex = -1)
    {
        _register = register;
        _values = values;
        _ok = ok;

        Text = Loc.F("ec.add_title", $"0x{register:X2}");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(360, 300);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _name = new TextBox { Dock = DockStyle.Fill, Text = Loc.T("ec.default_name") };
        _kind = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _kind.Items.AddRange(new object[] { "RawByte", "Word (LE/BE)", "Percent", "RPM (direct word)", "RPM (divisor / word)" });
        // Pre-select a guessed interpretation when opened from a correlation candidate; else RawByte.
        _kind.SelectedIndex = initialKindIndex is >= 0 and <= 4 ? initialKindIndex : 0;
        _kind.SelectedIndexChanged += (_, _) => UpdatePreview();

        _quantity = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _quantity.Items.AddRange(new object[] { "Fan (RPM)", "Control (%)", "Temperature (°C)", "Other" });
        _quantity.SelectedIndex = GuessQuantityIndex(_kind.SelectedIndex);

        _scale = new NumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 4, Minimum = -100000, Maximum = 100000, Value = 1m, Increment = 0.1m };
        _scale.ValueChanged += (_, _) => UpdatePreview();
        _divisor = new NumericUpDown { Dock = DockStyle.Fill, DecimalPlaces = 0, Minimum = 1, Maximum = 100000000, Value = 1000000m };
        _divisor.ValueChanged += (_, _) => UpdatePreview();
        _bigEndian = new CheckBox { Dock = DockStyle.Fill, Text = Loc.T("ec.big_endian") };
        _bigEndian.CheckedChanged += (_, _) => UpdatePreview();

        _preview = new Label { Dock = DockStyle.Fill, ForeColor = SystemColors.HotTrack, AutoSize = false, Height = 24 };

        layout.Controls.Add(new Label { Text = Loc.T("ec.field_name"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = Loc.T("ec.field_kind"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 1);
        layout.Controls.Add(_kind, 1, 1);
        layout.Controls.Add(new Label { Text = Loc.T("ec.field_quantity"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 2);
        layout.Controls.Add(_quantity, 1, 2);
        layout.Controls.Add(new Label { Text = Loc.T("ec.field_scale"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 3);
        layout.Controls.Add(_scale, 1, 3);
        layout.Controls.Add(new Label { Text = Loc.T("ec.field_divisor"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 4);
        layout.Controls.Add(_divisor, 1, 4);
        layout.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 5);
        layout.Controls.Add(_bigEndian, 1, 5);
        layout.Controls.Add(new Label { Text = Loc.T("ec.preview"), Anchor = AnchorStyles.Left, AutoSize = true }, 0, 6);
        layout.Controls.Add(_preview, 1, 6);

        var okButton = new Button { Text = Loc.T("common.ok"), DialogResult = DialogResult.OK, Width = 90 };
        okButton.Click += (_, _) => Build();
        var cancel = new Button { Text = Loc.T("common.cancel"), DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(okButton);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = okButton;
        CancelButton = cancel;
        UpdatePreview();
    }

    /// <summary>Default quantity that pairs with a guessed kind (RPM kinds → Fan, Percent → Control).</summary>
    private static int GuessQuantityIndex(int kindIndex) => kindIndex switch
    {
        2 => 1,        // Percent -> Control (%)
        3 or 4 => 0,   // RPM kinds -> Fan (RPM)
        _ => 0,        // default Fan
    };

    private EcValueKind SelectedKind => _kind.SelectedIndex switch
    {
        0 => EcValueKind.RawByte,
        1 => EcValueKind.Word,
        2 => EcValueKind.Percent,
        3 => EcValueKind.RpmDirect,
        _ => EcValueKind.RpmDivided,
    };

    private SensorQuantity SelectedQuantity => _quantity.SelectedIndex switch
    {
        0 => SensorQuantity.Fan,
        1 => SensorQuantity.Control,
        2 => SensorQuantity.Temperature,
        _ => SensorQuantity.Level,
    };

    private void UpdatePreview()
    {
        bool rpmDiv = SelectedKind == EcValueKind.RpmDivided;
        _divisor.Enabled = rpmDiv;
        _bigEndian.Enabled = SelectedKind is EcValueKind.Word or EcValueKind.RpmDirect or EcValueKind.RpmDivided;
        var def = Build(persist: false);
        float? v = def?.Compute(_values, _ok);
        _preview.Text = v is { } f ? Units.Format(SelectedQuantity, f) : "—";
    }

    private void Build() => Result = Build(persist: true);

    private EcSensorDef? Build(bool persist)
    {
        var def = new EcSensorDef
        {
            Register = _register,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? $"EC 0x{_register:X2}" : _name.Text.Trim(),
            Kind = SelectedKind,
            Quantity = SelectedQuantity,
            Scale = (float)_scale.Value,
            Divisor = (float)_divisor.Value,
            BigEndian = _bigEndian.Checked,
            Enabled = true,
        };
        if (persist) Result = def;
        return def;
    }
}
