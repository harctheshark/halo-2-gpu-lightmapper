// Small view items for the window: the colour-coded log line, the step chips (FINAL_DESIGN section 4.9) and the root list.
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using H2GpuLightmapper.Core.Run;

namespace H2GpuLightmapper.Ui
{
    public static class Palette
    {
        public static readonly Brush Fg = Frozen(0xE6, 0xE6, 0xE6), Plain = Frozen(0xD4, 0xD4, 0xD4), Dim = Frozen(0xA0, 0xA0, 0xA0), Faint = Frozen(0x6E, 0x6E, 0x6E);
        public static readonly Brush Accent = Frozen(0x3A, 0x96, 0xDD), Good = Frozen(0x6C, 0xCB, 0x5F), Warn = Frozen(0xE5, 0xB5, 0x4A), Bad = Frozen(0xF0, 0x70, 0x7A);
        public static readonly Brush ErrLine = Frozen(0xE0, 0x9A, 0x9A), Field = Frozen(0x2D, 0x2D, 0x30), Edge = Frozen(0x3F, 0x3F, 0x46);
        public static readonly Brush ActiveFill = Frozen(0x1F, 0x4F, 0x78), DoneFill = Frozen(0x1E, 0x3D, 0x22), BadFill = Frozen(0x4A, 0x22, 0x26), Clear = Frozen(0x25, 0x25, 0x26);

        private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        public static Brush ForLine(ParsedLine l)
        {
            switch (l.Kind)
            {
                case LineKind.Phase: return Accent;
                case LineKind.Note: return Warn;
                case LineKind.GuardFail: return Bad;
                case LineKind.Error: return ErrLine;
                case LineKind.Install: return Good;
                case LineKind.Shell: return Dim;
                default: return l.IsErr ? ErrLine : Plain;
            }
        }
    }

    public sealed class LogItem
    {
        public string Text { get; set; }
        public Brush Brush { get; set; }
    }

    public enum ChipState { Pending, Active, Done, Failed, Skipped }

    public sealed class StepChip : INotifyPropertyChanged
    {
        private ChipState _state;
        public StepId Step { get; set; }
        public string Name { get; set; }
        public ChipState State
        {
            get { return _state; }
            set { _state = value; var h = PropertyChanged; if (h != null) { h(this, new PropertyChangedEventArgs("Fill")); h(this, new PropertyChangedEventArgs("Edge")); h(this, new PropertyChangedEventArgs("Text")); } }
        }
        public Brush Fill { get { return _state == ChipState.Active ? Palette.ActiveFill : _state == ChipState.Done ? Palette.DoneFill : _state == ChipState.Failed ? Palette.BadFill : Palette.Clear; } }
        public Brush Edge { get { return _state == ChipState.Active ? Palette.Accent : _state == ChipState.Done ? Palette.Good : _state == ChipState.Failed ? Palette.Bad : Palette.Edge; } }
        public Brush Text { get { return _state == ChipState.Skipped ? Palette.Faint : _state == ChipState.Pending ? Palette.Dim : Palette.Fg; } }
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The UI step list in order (section 4.9) with the progress weights of the bar.</summary>
        public static readonly KeyValuePair<StepId, string>[] Names =
        {
            new KeyValuePair<StepId, string>(StepId.Preflight, "Preflight"), new KeyValuePair<StepId, string>(StepId.Backup, "Back up"),
            new KeyValuePair<StepId, string>(StepId.Capture, "Capture"), new KeyValuePair<StepId, string>(StepId.Derive, "Derive"),
            new KeyValuePair<StepId, string>(StepId.GpuBake, "GPU bake"), new KeyValuePair<StepId, string>(StepId.TagWriter, "Tag writer"),
            new KeyValuePair<StepId, string>(StepId.AO, "AO"), new KeyValuePair<StepId, string>(StepId.ProbeCapture, "Probe capture"),
            new KeyValuePair<StepId, string>(StepId.Probes, "Probes"), new KeyValuePair<StepId, string>(StepId.Install, "Install"),
            new KeyValuePair<StepId, string>(StepId.Verify, "Verify"),
        };

        public static double Weight(StepId s)
        {
            switch (s)
            {
                case StepId.Preflight: return 2; case StepId.Backup: return 1; case StepId.Capture: return 14; case StepId.Derive: return 10;
                case StepId.GpuBake: return 40; case StepId.TagWriter: return 8; case StepId.AO: return 8; case StepId.ProbeCapture: return 14;
                case StepId.Probes: return 6; case StepId.Install: return 2; default: return 1;
            }
        }
    }

    public sealed class RootItem
    {
        public string Root { get; set; }
        public string Display { get; set; }
        public override string ToString() { return Display; }
    }
}
