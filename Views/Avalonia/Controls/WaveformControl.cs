using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using Singularity.Models;
using SkiaSharp;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

namespace Singularity.Views.Avalonia.Controls
{
    public class WaveformControl : Control
    {
        public static readonly StyledProperty<WaveformAnalysisData> WaveformDataProperty =
            AvaloniaProperty.Register<WaveformControl, WaveformAnalysisData>(nameof(WaveformData));

        public WaveformAnalysisData WaveformData
        {
            get => GetValue(WaveformDataProperty);
            set => SetValue(WaveformDataProperty, value);
        }

        public static readonly StyledProperty<float> ProgressProperty =
            AvaloniaProperty.Register<WaveformControl, float>(nameof(Progress), 0f);

        public float Progress
        {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly StyledProperty<bool> IsRollingProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(IsRolling), false);

        public bool IsRolling
        {
            get => GetValue(IsRollingProperty);
            set => SetValue(IsRollingProperty, value);
        }

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayheadBrush), Brushes.White);

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(SeekCommand));

        public System.Windows.Input.ICommand? SeekCommand
        {
            get => GetValue(SeekCommandProperty);
            set => SetValue(SeekCommandProperty, value);
        }

        // Band Properties (Low, Mid, High) for RGB rendering
        public static readonly StyledProperty<byte[]?> LowBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(LowBand));
        public byte[]? LowBand { get => GetValue(LowBandProperty); set => SetValue(LowBandProperty, value); }
        public static readonly StyledProperty<byte[]?> MidBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(MidBand));
        public byte[]? MidBand { get => GetValue(MidBandProperty); set => SetValue(MidBandProperty, value); }
        public static readonly StyledProperty<byte[]?> HighBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(HighBand));
        public byte[]? HighBand { get => GetValue(HighBandProperty); set => SetValue(HighBandProperty, value); }

        public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Foreground));
        public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Background));
        public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> EnergyCurveProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<float>?>(nameof(EnergyCurve));

        public System.Collections.Generic.IEnumerable<float>? EnergyCurve
        {
            get => GetValue(EnergyCurveProperty);
            set => SetValue(EnergyCurveProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<float>?> VocalDensityCurveProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<float>?>(nameof(VocalDensityCurve));

        public System.Collections.Generic.IEnumerable<float>? VocalDensityCurve
        {
            get => GetValue(VocalDensityCurveProperty);
            set => SetValue(VocalDensityCurveProperty, value);
        }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<int>?> SegmentedEnergyProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<int>?>(nameof(SegmentedEnergy));

        public System.Collections.Generic.IEnumerable<int>? SegmentedEnergy
        {
            get => GetValue(SegmentedEnergyProperty);
            set => SetValue(SegmentedEnergyProperty, value);
        }

        // Sprint 2: Zoom Properties
        public static readonly StyledProperty<double> ZoomLevelProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(ZoomLevel), 1.0);

        /// <summary>
        /// Zoom level (1.0 = full track, 4.0 = 4x zoom, etc.)
        /// </summary>
        public double ZoomLevel
        {
            get => GetValue(ZoomLevelProperty);
            set => SetValue(ZoomLevelProperty, Math.Clamp(value, 1.0, 16.0));
        }

        // Defaults true so every existing caller (Now Playing, Cue Forge) keeps today's
        // scroll-to-zoom behavior unchanged. The Mix Transition Editor sets this false: its two
        // waveforms live inside a page ScrollViewer, and OnPointerWheelChanged below always marks
        // wheel events Handled — with scroll-to-zoom on, hovering either waveform silently ate
        // every scroll gesture instead of letting it reach that ScrollViewer, which is what made
        // scrolling the editor feel like it "only worked one way" (it worked only when the cursor
        // wasn't over a waveform).
        public static readonly StyledProperty<bool> EnableScrollZoomProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(EnableScrollZoom), true);

        public bool EnableScrollZoom
        {
            get => GetValue(EnableScrollZoomProperty);
            set => SetValue(EnableScrollZoomProperty, value);
        }

        public static readonly StyledProperty<double> ViewOffsetProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(ViewOffset), 0.0);

        /// <summary>
        /// Horizontal offset as fraction of track (0.0 = start, 1.0 = end)
        /// </summary>
        public double ViewOffset
        {
            get => GetValue(ViewOffsetProperty);
            set => SetValue(ViewOffsetProperty, Math.Clamp(value, 0.0, Math.Max(0, 1.0 - (1.0 / ZoomLevel))));
        }

        static WaveformControl()
        {
            AffectsRender<WaveformControl>(
                WaveformDataProperty, 
                ProgressProperty, 
                IsRollingProperty, 
                LowBandProperty, 
                MidBandProperty, 
                HighBandProperty, 
                EnergyCurveProperty,
                VocalDensityCurveProperty,
                SegmentedEnergyProperty,
                ForegroundProperty,
                BackgroundProperty,
                PlayheadBrushProperty,
                ZoomLevelProperty,
                ViewOffsetProperty,
                FrequencyColorModeProperty);
        }

        private bool _isDraggingProgress;
        private double _hoverX = -1; // -1 = not hovering

        // Bitmap Cache
        private RenderTargetBitmap? _baseBitmap;
        private RenderTargetBitmap? _activeBitmap;
        private Size _lastRenderSize;
        private bool _isDirty = true;

        // Sprint 4: Performance Optimizations
        private double _lastZoomLevel = 1.0;

        private DateTime _lastRenderTime = DateTime.MinValue;
        private const double FrameThrottleMs = 33.33; // ~30 FPS max
        private const double SizeTolerance = 5.0; // Pixels tolerance for bitmap reuse

        /// <summary>
        /// When <c>true</c>, the waveform is rendered using tri-band frequency colours
        /// (Low=hot-pink/red, Mid=neon-green, High=cyan/blue). If no <see cref="LowBand"/>
        /// data is bound, a synthetic approximation is generated from the peak data.
        /// </summary>
        public static readonly StyledProperty<bool> FrequencyColorModeProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(FrequencyColorMode), false);

        public bool FrequencyColorMode
        {
            get => GetValue(FrequencyColorModeProperty);
            set => SetValue(FrequencyColorModeProperty, value);
        }

        // Sprint 4: Vocal Ghost Layer
        public static readonly StyledProperty<bool> ShowVocalGhostProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(ShowVocalGhost), false);

        public bool ShowVocalGhost
        {
            get => GetValue(ShowVocalGhostProperty);
            set => SetValue(ShowVocalGhostProperty, value);
        }

        // Waveform appearance settings — previously every color/overlay was a compiled-in constant.
        public static readonly StyledProperty<bool> UseNeonPaletteProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(UseNeonPalette), true);

        public bool UseNeonPalette
        {
            get => GetValue(UseNeonPaletteProperty);
            set => SetValue(UseNeonPaletteProperty, value);
        }

        public static readonly StyledProperty<double> GainProperty =
            AvaloniaProperty.Register<WaveformControl, double>(nameof(Gain), 1.0);

        public double Gain
        {
            get => GetValue(GainProperty);
            set => SetValue(GainProperty, value);
        }

        public static readonly StyledProperty<bool> ShowEnergyCurveProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(ShowEnergyCurve), true);

        public bool ShowEnergyCurve
        {
            get => GetValue(ShowEnergyCurveProperty);
            set => SetValue(ShowEnergyCurveProperty, value);
        }

        private DispatcherTimer? _ghostPulseTimer;
        private float _ghostOpacity = 0.6f;
        private bool _ghostPulseUp = true;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // ShowVocalGhost defaults to false and most waveforms on screen at once (every deck
            // row, both Mix-editor waveforms, CueForge) never turn it on — this used to start an
            // unconditional 33ms DispatcherTimer on every single instance regardless, idling for
            // its whole lifetime. Only start it when actually needed; OnPropertyChanged below
            // starts/stops it as the property flips instead.
            if (ShowVocalGhost) EnsureGhostPulseTimer();
        }

        private void EnsureGhostPulseTimer()
        {
            if (_ghostPulseTimer != null) return;

            _ghostPulseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (s, ev) =>
            {
                if (ShowVocalGhost)
                {
                    // Sine wave pulse logic
                    // Simple linear approximation for now or actual sine
                    // 0.6 to 1.0
                    if (_ghostPulseUp)
                    {
                        _ghostOpacity += 0.02f;
                        if (_ghostOpacity >= 1.0f) { _ghostOpacity = 1.0f; _ghostPulseUp = false; }
                    }
                    else
                    {
                        _ghostOpacity -= 0.02f;
                        if (_ghostOpacity <= 0.6f) { _ghostOpacity = 0.6f; _ghostPulseUp = true; }
                    }
                    InvalidateVisual();
                }
            });
            _ghostPulseTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _ghostPulseTimer?.Stop();
            _ghostPulseTimer = null;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == WaveformDataProperty ||
                change.Property == BoundsProperty ||
                change.Property == LowBandProperty ||
                change.Property == MidBandProperty ||
                change.Property == HighBandProperty ||
                change.Property == UseNeonPaletteProperty ||
                change.Property == GainProperty ||
                change.Property == FrequencyColorModeProperty)
            {
                // These feed the cached base/active bitmaps (RenderStaticToContext), so a change
                // needs a real rebuild, not just a redraw.
                _isDirty = true;
                InvalidateVisual();
            }
            else if (change.Property == ShowEnergyCurveProperty ||
                     change.Property == ShowVocalGhostProperty)
            {
                // Drawn directly in Render(), outside the cached bitmap — a redraw is enough.
                if (change.Property == ShowVocalGhostProperty)
                {
                    if ((bool)(change.NewValue ?? false)) EnsureGhostPulseTimer();
                    else { _ghostPulseTimer?.Stop(); _ghostPulseTimer = null; }
                }
                InvalidateVisual();
            }
            // Sprint 4: Only invalidate on significant zoom changes (>5%)
            else if (change.Property == ZoomLevelProperty)
            {
                double newZoom = (double)(change.NewValue ?? 1.0);
                if (Math.Abs(newZoom - _lastZoomLevel) / _lastZoomLevel > 0.05)
                {
                    _isDirty = true;
                    _lastZoomLevel = newZoom;
                }
                InvalidateVisual();
            }
            else if (change.Property == ViewOffsetProperty)
            {
                // ViewOffset now shifts which slice of the track the cached bitmap actually shows
                // (see RenderStaticToContext's zoom/pan mapping), so — unlike before this was
                // wired up — a real rebuild is needed here too, not just a redraw.
                _isDirty = true;
                InvalidateVisual();
            }
        }

        // Sprint 2: Scroll-to-Zoom
        protected override void OnPointerWheelChanged(global::Avalonia.Input.PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            if (!EnableScrollZoom)
            {
                // Leave e.Handled false so the wheel event bubbles to a containing ScrollViewer.
                return;
            }

            // Zoom centered on mouse position
            var point = e.GetPosition(this);
            double mouseRatio = point.X / Bounds.Width;
            
            // Calculate zoom delta (Ctrl+scroll for faster zoom)
            double zoomDelta = e.Delta.Y > 0 ? 1.2 : 0.8;
            double oldZoom = ZoomLevel;
            double newZoom = Math.Clamp(oldZoom * zoomDelta, 1.0, 16.0);
            
            if (Math.Abs(newZoom - oldZoom) > 0.01)
            {
                // Adjust offset to keep mouse position anchored
                double visibleFraction = 1.0 / oldZoom;
                double mousePosition = ViewOffset + (mouseRatio * visibleFraction);
                
                double newVisibleFraction = 1.0 / newZoom;
                double newOffset = mousePosition - (mouseRatio * newVisibleFraction);
                
                ZoomLevel = newZoom;
                ViewOffset = Math.Clamp(newOffset, 0, Math.Max(0, 1.0 - newVisibleFraction));
                
                _isDirty = true;
                InvalidateVisual();
            }
            e.Handled = true;
        }

        protected override void OnPointerPressed(global::Avalonia.Input.PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
                return;

            _isDraggingProgress = true;
            e.Pointer.Capture(this);
            UpdateProgressFromPoint(point);
            e.Handled = true;
        }

        protected override void OnPointerMoved(global::Avalonia.Input.PointerEventArgs e)
        {
            var point = e.GetPosition(this);
            if (_isDraggingProgress)
            {
                UpdateProgressFromPoint(point);
            }

            _hoverX = point.X;
            InvalidateVisual();
        }

        protected override void OnPointerEntered(global::Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }

        protected override void OnPointerExited(global::Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverX = -1;
            InvalidateVisual();
        }

        private void UpdateProgressFromPoint(Point point)
        {
             if (IsRolling)
             {
                  // In Rolling mode, clicking left/right of center seeks relative to playhead?
                  // Or we just treat the whole strip as 0-1 range still? 
                  // Usually, clicking a rolling waveform seeks to that spot.
                  // For simplicity, let's keep the click 0-1 range for the static view, 
                  // and maybe a relative seek for rolling.
             }
             
             var progress = Math.Clamp(XToFraction(point.X, Bounds.Width), 0.0, 1.0);

             if (SeekCommand != null && SeekCommand.CanExecute(progress))
             {
                 SeekCommand.Execute(progress);
             }
             Progress = (float)progress; // Immediate UI feedback
        }

        protected override void OnPointerReleased(global::Avalonia.Input.PointerReleasedEventArgs e)
        {
            _isDraggingProgress = false;
            e.Pointer.Capture(null);
        }

        /// <summary>
        /// Maps a track-position fraction (0..1) to an X pixel, honoring ZoomLevel/ViewOffset —
        /// the single source of truth for "where does this moment in the track appear on screen"
        /// once the waveform can be zoomed into a sub-range (see RenderStaticToContext). At
        /// ZoomLevel == 1 this is exactly the old `fraction * width` behavior.
        /// </summary>
        private double FractionToX(double fraction, double width)
        {
            double zoom = Math.Max(1.0, ZoomLevel);
            return (fraction - ViewOffset) * zoom * width;
        }

        /// <summary>Inverse of <see cref="FractionToX"/> — maps an X pixel back to a track-position
        /// fraction (0..1), for pointer interactions (seek, hover tooltip) against a zoomed view.</summary>
        private double XToFraction(double x, double width)
        {
            double zoom = Math.Max(1.0, ZoomLevel);
            return ViewOffset + (x / width) / zoom;
        }

        public override void Render(DrawingContext context)
        {
            try
            {
                RenderInternal(context);
            }
            catch (Exception ex)
            {
                // Render-thread exceptions bypass all managed exception handling and hard-crash
                // the process with zero trace (same class of bug as VocalGhostDrawOperation
                // below). Skip the frame instead of taking the app down.
                Serilog.Log.Warning(ex, "WaveformControl: render tick failed — skipping frame");
            }
        }

        private void RenderInternal(DrawingContext context)
        {
            var data = WaveformData;
            if (data == null || data.IsEmpty || data.PeakData == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                context.DrawLine(new Pen(Brushes.Gray, 1), new Point(0, Bounds.Height / 2), new Point(Bounds.Width, Bounds.Height / 2));
                return;
            }

            // Throttle only the expensive bitmap rebuild (~30 FPS max).
            // Every frame still draws cues, phrases, playhead, and the active overlay.
            bool needsNewBitmap = _isDirty || _baseBitmap == null || _activeBitmap == null ||
                                  Math.Abs(_lastRenderSize.Width - Bounds.Width) > SizeTolerance ||
                                  Math.Abs(_lastRenderSize.Height - Bounds.Height) > SizeTolerance;

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastRenderTime).TotalMilliseconds;
            bool shouldRebuild = needsNewBitmap && (elapsed >= FrameThrottleMs || _isDirty || _baseBitmap == null);

            if (shouldRebuild)
            {
                _lastRenderTime = now;
                UpdateBitmapCache(Bounds.Size);
                _isDirty = false;
                _lastRenderSize = Bounds.Size;
            }

            var width = Bounds.Width;
            var height = Bounds.Height;

            // 0. Draw Vocal Ghost Layer (Behind everything)
            var vocalCurve = VocalDensityCurve?.ToList();
            if (ShowVocalGhost && vocalCurve != null && vocalCurve.Count > 0)
            {
                context.Custom(new VocalGhostDrawOperation(new Rect(0, 0, width, height), vocalCurve, data.DurationSeconds, Progress, _ghostOpacity, IsRolling, ZoomLevel, ViewOffset));
            }

            if (IsRolling)
            {
                // For rolling, we just use the direct render for now as it needs continuous updating
                RenderRolling(context, data, width, height, height / 2);
            }
            else
            {
                // fast render using cached bitmaps
                if (_baseBitmap != null)
                    context.DrawImage(_baseBitmap, new Rect(0, 0, width, height));

                if (_activeBitmap != null)
                {
                    double playedWidth = Math.Clamp(FractionToX(Progress, width), 0, width);
                    // Clip to played area
                    using (context.PushClip(new Rect(0, 0, playedWidth, height)))
                    {
                        context.DrawImage(_activeBitmap, new Rect(0, 0, width, height));
                    }
                }
            }

            // 2. Draw Curves (Energy, Vocals)
            RenderCurves(context, width, height);

            // Draw hover seek cursor (semi-transparent white line, only when not dragging)
            if (_hoverX >= 0 && !_isDraggingProgress)
            {
                var hoverPen = new Pen(new SolidColorBrush(Colors.White, 0.35), 1);
                context.DrawLine(hoverPen, new Point(_hoverX, 0), new Point(_hoverX, height));

                // Time tooltip: show estimated position as % or mm:ss if duration known
                if (WaveformData != null && WaveformData.DurationSeconds > 0)
                {
                    double hoverFraction = Math.Clamp(XToFraction(_hoverX, width), 0, 1);
                    double hoverSecs = hoverFraction * WaveformData.DurationSeconds;
                    int mm = (int)(hoverSecs / 60);
                    int ss = (int)(hoverSecs % 60);
                    string timeLabel = $"{mm}:{ss:D2}";

                    var tf = new FormattedText(
                        timeLabel,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        Typeface.Default,
                        10,
                        new SolidColorBrush(Colors.White, 0.75));

                    double labelX = (_hoverX + 4 + tf.Width > width) ? _hoverX - tf.Width - 4 : _hoverX + 4;
                    context.DrawText(tf, new Point(labelX, 4));
                }
            }

            // Draw Playhead Line — skipped entirely when zoomed and the actual play position has
            // scrolled outside the visible window, rather than drawing a misleading line pinned
            // to the window's edge.
            double playheadX = IsRolling ? width / 2 : FractionToX(Progress, width);
            if (IsRolling || (playheadX >= 0 && playheadX <= width))
            {
                context.DrawLine(new Pen(PlayheadBrush ?? Brushes.White, 2), new Point(playheadX, 0), new Point(playheadX, height));
            }


        }

        private void UpdateBitmapCache(Size size)
        {
            if (size.Width <= 0 || size.Height <= 0) return;

            // Dispose old bitmaps
            _baseBitmap?.Dispose();
            _activeBitmap?.Dispose();

            // Create new bitmaps
            // Note: Pixel size should match visual size * scaling, but for now 1:1 is likely fine or we get DPI
            var pixelSize = new PixelSize((int)size.Width, (int)size.Height);
            var dpi = new Vector(96, 96); // Standard DPI

            _baseBitmap = new RenderTargetBitmap(pixelSize, dpi);
            _activeBitmap = new RenderTargetBitmap(pixelSize, dpi);

            var data = WaveformData;
            var mid = size.Height / 2;
            var width = size.Width;

            // Render Unplayed (Base) State
            using (var ctx = _baseBitmap.CreateDrawingContext())
            {
                 RenderStaticToContext(ctx, data, width, size.Height, mid, false);
            }

            // Render Played (Active) State
            using (var ctx = _activeBitmap.CreateDrawingContext())
            {
                 RenderStaticToContext(ctx, data, width, size.Height, mid, true);
            }
        }

        private void RenderStaticToContext(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid, bool isActive)
        {
            var samples = data.PeakData!.Length;
            double step = width / samples;

            // Zoom/pan support (Mix Transition Editor's per-side windowed view, and manual
            // scroll-to-zoom): ZoomLevel/ViewOffset were already computed correctly upstream
            // (MixTransitionViewModel) and already drove OnPointerWheelChanged's zoom-around-mouse
            // math, but this render path — the one everything actually looks at — ignored them
            // entirely and always mapped the whole track across the control's width. Widening the
            // per-sample step by ZoomLevel and shifting by ViewOffset maps only the visible
            // fraction [ViewOffset, ViewOffset + 1/ZoomLevel] of the track across [0, width];
            // samples outside that window fall outside [0, width] and are skipped by each render
            // method's existing off-screen clipping check, so no loop-bounds changes are needed.
            // At ZoomLevel == 1 (every caller except the Mix editor: Now Playing, Cue Forge, etc.)
            // zoomedStep == step and xShift == 0, so behavior is unchanged.
            double zoom = Math.Max(1.0, ZoomLevel);
            double zoomedStep = step * zoom;
            double xShift = ViewOffset * width * zoom;

            var lowData  = LowBand  ?? data.LowData;
            var midData  = MidBand  ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb  = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                RenderTrueRgb(context, data, width, height, mid, samples, zoomedStep, lowData!, midData!, highData!, false, -xShift, isActive, zoom);
            }
            else if (FrequencyColorMode)
            {
                // Synthesize pseudo tri-band data from amplitude so freq colours still show
                // even when full-spectrum analysis hasn't run yet.
                var peak = data.PeakData!;
                var synLow  = new byte[samples];
                var synMid  = new byte[samples];
                var synHigh = new byte[samples];
                for (int i = 0; i < samples; i++)
                {
                    byte p = peak[i];
                    // Loud transients carry more low-end; quiet detail sits in highs.
                    synLow[i]  = (byte)(p > 160 ? p : 0);
                    synMid[i]  = (byte)(p is > 80 and < 220 ? p : 0);
                    synHigh[i] = (byte)(p < 110 ? (byte)(110 - p) : 0);
                }
                RenderTrueRgb(context, data, width, height, mid, samples, zoomedStep, synLow, synMid, synHigh, false, -xShift, isActive, zoom);
            }
            else
            {
                RenderSingleBandCached(context, data, width, mid, samples, zoomedStep, isActive, -xShift, zoom);
            }
        }

        private static readonly Pen StaticBasePen = new Pen(Brushes.DimGray, 1);
        private static readonly Pen StaticPlayedPen = new Pen(Brushes.DeepSkyBlue, 1);
        
        // Cache for RGB pens to avoid allocations
        private static readonly Pen LowBasePen = new Pen(new SolidColorBrush(Color.FromRgb(100, 0, 0), 0.35f * 0.8f), 1);
        private static readonly Pen LowPlayedPen = new Pen(new SolidColorBrush(Colors.Red, 0.8f), 1);
        private static readonly Pen MidBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 100, 0), 0.35f * 0.7f), 1);
        private static readonly Pen MidPlayedPen = new Pen(new SolidColorBrush(Colors.Lime, 0.7f), 1);
        private static readonly Pen HighBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 80, 100), 0.35f), 1);
        private static readonly Pen HighPlayedPen = new Pen(new SolidColorBrush(Colors.DeepSkyBlue, 1.0f), 1);

        private void RenderSingleBandCached(DrawingContext context, WaveformAnalysisData data, double width, double mid, int samples, double step, bool isActive, double xOffset = 0, double zoom = 1.0)
        {
            // Draw full waveform in one color. targetColumns is scaled by zoom so the visible
            // (zoomed-in) window still gets ~one rendered point per pixel instead of the same
            // whole-track stride spread thinly across a window a fraction of the control's width.
            int targetColumns = Math.Max(1, (int)(width * Math.Max(1.0, zoom)));
            int stride = Math.Max(1, samples / targetColumns);
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                for (int i = 0; i < samples; i += stride)
                {
                    double x = (i * step) + xOffset;
                    if (x < -step || x > width + step) continue;
                    double h = Math.Min((data.PeakData![i] / 255.0) * mid * (Gain > 0 ? Gain : 1.0), mid);
                    if (h < 0.5) continue;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, isActive ? StaticPlayedPen : StaticBasePen, geom);
        }

        // Optimzied TrueRGB: Renders FULL waveform with specific opacity/brightness
        private void RenderTrueRgb(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid, int samples, double step, byte[] low, byte[] midB, byte[] high, bool isRolling, double currentXOffset = 0, bool isActive = true, double zoom = 1.0)
        {
            var playedLimit = (int)(Progress * samples);
            var peak = data.PeakData!;
            // targetColumns scaled by zoom so a zoomed-in window (e.g. the Mix Transition
            // Editor's per-side view) still samples at ~one point per pixel of the *visible*
            // window instead of the whole-track stride, which would otherwise spread native
            // detail thinly and only let a quarter of it land on-screen at 4x zoom.
            int targetColumns = Math.Max(1, (int)(width * Math.Max(1.0, zoom)));
            int stride = Math.Max(1, samples / targetColumns);
            
            double duration = data.DurationSeconds > 0 ? data.DurationSeconds : samples / 100.0;

            // Palette — Neon (default) or Classic RGB, per the Waveform Appearance setting.
            Color lowColor, midColor, highColor;
            if (UseNeonPalette)
            {
                lowColor = Color.FromRgb(255, 40, 100);   // Hot Pink / Red
                midColor = Color.FromRgb(0, 255, 120);    // Neon Green
                highColor = Color.FromRgb(0, 200, 255);   // Cyan / Blue
            }
            else
            {
                lowColor = Color.FromRgb(255, 68, 68);    // Classic red
                midColor = Color.FromRgb(68, 255, 136);   // Classic green
                highColor = Color.FromRgb(68, 170, 255);  // Classic blue
            }

            double gain = Gain > 0 ? Gain : 1.0;

            int safeLen = Math.Min(samples, Math.Min(peak.Length, Math.Min(low.Length, Math.Min(midB.Length, high.Length))));
            for (int i = 0; i < safeLen; i += stride)
            {
                
                double h = Math.Min((peak[i] / 255.0) * mid * gain, mid);
                if (h < 0.5) continue;

                double x = (i * step) + currentXOffset;
                if (x < -step || x > width + step) continue;

                    float energyTint = 0.5f; // Neutral

                    // Intensity-based blending
                    double l = low[i] / 255.0;
                    double m = midB[i] / 255.0;
                    double hf = high[i] / 255.0;
                    double total = l + m + hf;

                    if (total > 0)
                    {
                        byte r = (byte)Math.Clamp((l * lowColor.R + m * midColor.R + hf * highColor.R) / total, 0, 255);
                        byte g = (byte)Math.Clamp((l * lowColor.G + m * midColor.G + hf * highColor.G) / total, 0, 255);
                        byte b = (byte)Math.Clamp((l * lowColor.B + m * midColor.B + hf * highColor.B) / total, 0, 255);
                        
                        // Apply Energy Temperature (MIK Parity: Blue -> Green -> Yellow -> Red)
                        // This uses a spectral shift based on energyTint (0.0 - 1.0)
                        float t = energyTint;
                        byte targetR = (byte)(t < 0.5f ? 0 : Math.Clamp((t - 0.5f) * 2 * 255, 0, 255));
                        byte targetG = (byte)Math.Clamp((1.0f - Math.Abs(t - 0.5f) * 2) * 255, 0, 255);
                        byte targetB = (byte)(t > 0.5f ? 0 : Math.Clamp((0.5f - t) * 2 * 255, 0, 255));

                        // Blend the spectral tint into the frequency-based color
                        r = (byte)Math.Clamp((r * 0.7f) + (targetR * 0.3f), 0, 255);
                        g = (byte)Math.Clamp((g * 0.7f) + (targetG * 0.3f), 0, 255);
                        b = (byte)Math.Clamp((b * 0.7f) + (targetB * 0.3f), 0, 255);

                        bool isPlayed = isRolling ? (i <= playedLimit) : isActive;
                        float opacity = isPlayed ? 1.0f : 0.35f;
                    
                    var col = Color.FromArgb((byte)(opacity * 255), r, g, b);
                    context.DrawLine(GetOrCreateRgbColumnPen(col), new Point(x, mid - h), new Point(x, mid + h));
                }
            }
        }

        // RenderTrueRgb draws one column at a time, each with its own (continuously blended, so
        // rarely identical to its neighbor) color — `new Pen(new SolidColorBrush(col), 1)` inside
        // that loop allocated two objects per column, up to ~1000+ per bitmap rebuild (this method
        // only runs on an actual _isDirty rebuild — data/zoom change or an active cue drag — not
        // every frame, but those rebuilds can happen up to 30x/sec while dragging). Caching by
        // exact color reuses the same Pen for any repeat, at zero visual difference. Capped since
        // colors are a continuous blend — an unbounded cache from one long zoom/drag session could
        // otherwise grow without limit.
        private readonly Dictionary<Color, Pen> _rgbColumnPenCache = new();
        private const int MaxRgbColumnPenCacheEntries = 4096;

        private Pen GetOrCreateRgbColumnPen(Color color)
        {
            if (_rgbColumnPenCache.TryGetValue(color, out var pen)) return pen;

            pen = new Pen(new SolidColorBrush(color), 1);
            if (_rgbColumnPenCache.Count >= MaxRgbColumnPenCacheEntries) _rgbColumnPenCache.Clear();
            _rgbColumnPenCache[color] = pen;
            return pen;
        }
        private void DrawBandBatch(DrawingContext context, byte[] data, int samples, double step, double mid, int playedLimit, Pen basePen, Pen playedPen)
        {
            var baseGeom = new StreamGeometry();
            using (var ctx = baseGeom.Open())
            {
                for (int i = playedLimit; i < Math.Min(samples, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, basePen, baseGeom);

            var playedGeom = new StreamGeometry();
            using (var ctx = playedGeom.Open())
            {
                for (int i = 0; i < Math.Min(playedLimit, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, playedPen, playedGeom);
        }

        private void RenderRolling(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid)
        {
            double windowSec = 10.0;
            double pixelsPerSec = width / windowSec;
            double currentSec = Progress * data.DurationSeconds;
            double startSec = currentSec - (windowSec / 2);
            
            int samplesPerSec = (int)(data.PeakData!.Length / data.DurationSeconds);
            int startIdx = (int)(startSec * samplesPerSec);
            double startX = (width / 2) + ( (startSec - currentSec) * pixelsPerSec );

            var lowData = LowBand ?? data.LowData;
            var midData = MidBand ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                // step = pixels per sample. 
                // pixelsPerSec = width / 10.0
                // samplesPerSec = total_samples / duration
                // step = pixelsPerSec / samplesPerSec
                double step = pixelsPerSec / samplesPerSec;
                RenderTrueRgb(context, data, width, height, mid, data.PeakData.Length, step, lowData!, midData!, highData!, true, (width / 2) - (currentSec * pixelsPerSec), true);
            }
            else
            {
                // Fallback to static blue if no RGB
                int endIdx = startIdx + (int)(windowSec * samplesPerSec);
                int playedLimit = (int)(Progress * data.PeakData.Length);
                var playedGeom = new StreamGeometry();
                var baseGeom = new StreamGeometry();
                using (var pCtx = playedGeom.Open())
                using (var bCtx = baseGeom.Open())
                {
                    for (int i = startIdx; i <= endIdx; i++)
                    {
                        if (i < 0 || i >= data.PeakData.Length) continue;
                        double sampleSec = (double)i / samplesPerSec;
                        double x = (width / 2) + (sampleSec - currentSec) * pixelsPerSec;
                        double h = (data.PeakData[i] / 255.0) * mid;
                        var ctx = i <= playedLimit ? pCtx : bCtx;
                        ctx.BeginFigure(new Point(x, mid - h), false);
                        ctx.LineTo(new Point(x, mid + h));
                    }
                }
                context.DrawGeometry(null, StaticPlayedPen, playedGeom);
                context.DrawGeometry(null, StaticBasePen, baseGeom);
            }
        }

        private void RenderCurves(DrawingContext context, double width, double height)
        {
            var energy = EnergyCurve;
            var vocals = VocalDensityCurve;
            
            if (energy == null && vocals == null) return;

            // Draw Energy Curve (Yellow glow)
            if (energy != null && ShowEnergyCurve)
            {
                RenderCurve(context, energy, width, height, Color.FromRgb(255, 255, 0), 0.6f);
            }

            // Draw Vocal Curve (Purple glow) — same toggle as the vocal ghost pulse layer, one
            // "show vocal info" setting rather than splitting it into two.
            if (vocals != null && ShowVocalGhost)
            {
                RenderCurve(context, vocals, width, height, Color.FromRgb(189, 16, 224), 0.5f);
            }
        }

        private void RenderCurve(DrawingContext context, System.Collections.Generic.IEnumerable<float> points, double width, double height, Color color, float opacity)
        {
            var list = System.Linq.Enumerable.ToList(points);
            if (list.Count < 2) return;

            double step = width / (list.Count - 1);
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(new Point(0, height - (list[0] * height)), false);
                for (int i = 1; i < list.Count; i++)
                {
                    ctx.LineTo(new Point(i * step, height - (list[i] * height)));
                }
            }

            var brush = new SolidColorBrush(color, opacity);
            context.DrawGeometry(null, new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geom);
        }

    }

    public class VocalGhostDrawOperation : ICustomDrawOperation
    {
        public Rect Bounds { get; }
        private readonly System.Collections.Generic.List<float> _vocalData;
        private readonly double _duration;
        private readonly float _progress;
        private readonly float _opacity;
        private readonly bool _isRolling;
        private readonly double _zoom;
        private readonly double _offset;

        public VocalGhostDrawOperation(Rect bounds, System.Collections.Generic.List<float> vocalData, double duration, float progress, float opacity, bool isRolling, double zoom, double offset)
        {
            Bounds = bounds;
            _vocalData = vocalData;
            _duration = duration;
            _progress = progress;
            _opacity = opacity;
            _isRolling = isRolling;
            _zoom = zoom;
            _offset = offset;
        }

        public void Dispose() { }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            try
            {
            var lease = context.TryGetFeature<ISkiaSharpApiLease>();
            if (lease == null) return;

            using var canvas = lease.SkCanvas;
            canvas.Save();

            var width = (float)Bounds.Width;
            var height = (float)Bounds.Height;
            var samples = _vocalData.Count;
            
            // Neon Purple (#B450FF) -> SKColor
            var baseColor = new SKColor(180, 80, 255, (byte)(_opacity * 255)); 

            using var paint = new SKPaint
            {
                Color = baseColor,
                BlendMode = SKBlendMode.Screen,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            
            // Create Gradient Shader (Vertical)
            var colors = new SKColor[] { SKColors.Transparent, baseColor, SKColors.Transparent };
            var pos = new float[] { 0.0f, 0.5f, 1.0f };
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                colors,
                pos,
                SKShaderTileMode.Clamp);
            
            paint.Shader = shader;

            double duration = _duration > 0 ? _duration : 1.0;
            
            // Visible Range
            double visibleDuration = duration / _zoom;
            double startTime = _offset * duration;
            double endTime = startTime + visibleDuration;
            
            int startIndex = (int)((startTime / duration) * samples);
            int endIndex = (int)((endTime / duration) * samples);
            
            startIndex = Math.Clamp(startIndex, 0, samples - 1);
            endIndex = Math.Clamp(endIndex, 0, samples - 1);
            
            int range = endIndex - startIndex;
            if (range <= 0) { canvas.Restore(); return; }

            // Step size for performance
            int step = Math.Max(1, range / (int)width); 

            float lastX = -1;
            
            for (int i = startIndex; i < endIndex; i += step)
            {
                if (i >= _vocalData.Count) break;
                
                float prob = _vocalData[i]; 
                
                // Logic: InstProb < 0.2 means High Vocal Presence (Vocal Pocket)
                if (prob < 0.2f) 
                {
                    // Map index to X
                    double sampleTime = (i / (double)samples) * duration;
                    double relTime = sampleTime - startTime;
                    double relPos = relTime / visibleDuration;
                    
                    float x = (float)(relPos * width);
                    float w = Math.Max(1.0f, (float)(width / range) * step);
                    
                    if (x > lastX)
                    {
                        canvas.DrawRect(x, 0, w, height, paint);
                        lastX = x + w;
                    }
                }
            }

            canvas.Restore();
            }
            catch (Exception ex)
            {
                // Render-thread exceptions bypass all managed exception handling and hard-crash
                // the process with zero trace. Skip the frame instead of taking the app down.
                Serilog.Log.Warning(ex, "WaveformControl: vocal-ghost render tick failed — skipping frame");
            }
        }
    }
}
