using System.Diagnostics;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using ILNumerics.Drawing;
using ILNumerics.Drawing.Plotting;
using static ILNumerics.ILMath;
using DColor = System.Drawing.Color;
using DLabel = ILNumerics.Drawing.Label;
using DPoint = System.Drawing.Point;
using DPointF = System.Drawing.PointF;
using WpfPanel = ILNumerics.Drawing.Controls.WPF.Panel;
using WinFormsPanel = ILNumerics.Drawing.Controls.WinForms.Panel;
using WpfBorder = System.Windows.Controls.Border;

namespace ILNumerics.VisualDrawingTestApp.Windows {

    /// <summary>
    /// Visual / interactive regression test for multiple ILNumerics OpenGL panels (WPF and WinForms) in one window.
    /// </summary>
    /// <remarks>
    /// <para>Each OpenGL panel owns a dedicated OpenGL context. Mouse picking, rendering of scene changes, resizing and
    /// closing happen outside of the panel's own render callback, i.e. possibly while ANOTHER panel's context is current.
    /// Before the fix, the WPF OGLControl.PickAt() did not make its context current (IOGLControl.EnsureCurrent() was empty)
    /// and both WPF and WinForms panels freed their GL resources / took scene snapshots in whatever context was current:
    /// one panel's VAOs / framebuffers / programs were used or deleted in another panel's context -> wrong pick results,
    /// rendering failures or an access violation in the GL driver (e.g. OGLControl.OnMouseMove -> PickAt -> ...
    /// OGLLabelRenderer.EnsureOglObjects -> GL.BindVertexArray in nvoglv64.dll).</para>
    /// <para>This window reproduces that situation with WPF panels, WinForms panels (hosted in WindowsFormsHost) or both mixed:
    /// panel creation / closing, scene replacement, resizing and picking - manually via mouse and buttons, or randomly
    /// in 'Auto-Stress' mode. Pass criteria: no crash, 'center falsch' = 0, 'Exceptions' = 0.</para>
    /// <para>Closed panels are disposed and never re-added: re-loading an unloaded OpenGL panel is a separate, known limitation.</para>
    /// </remarks>
    public partial class OpenGLMultiPanelStressWindow : Window {

        const string CenterTag = "center";
        const int InitialPanels = 4;
        const int MaxPanels = 6;

        #region panel adapter (WPF / WinForms)
        /// <summary>Common view on a WPF or a WinForms ILNumerics OpenGL panel.</summary>
        abstract class StressPanel {
            public string Name = "";
            public readonly Stopwatch Age = Stopwatch.StartNew();
            /// <summary>The WPF element added to the panel grid.</summary>
            public abstract FrameworkElement Host { get; }
            public abstract Scene Scene { get; set; }
            public abstract Group? SceneSyncRoot { get; }
            public abstract bool IsReady { get; }
            /// <summary>Center / random point in the coordinates PickAt() of this panel expects.</summary>
            public abstract DPoint Center { get; }
            public abstract DPoint RandomPoint(Random rnd);
            public abstract int? PickAt(DPoint p);
            public abstract void Invalidate();
            /// <summary>Called after the host was removed from the visual tree.</summary>
            public abstract void Dispose();
        }
        sealed class WpfStressPanel : StressPanel {
            readonly WpfPanel m_panel = new WpfPanel() { RendererType = RendererTypes.OpenGL };
            public override FrameworkElement Host => m_panel;
            public override Scene Scene { get => m_panel.Scene; set => m_panel.Scene = value; }
            public override Group? SceneSyncRoot => m_panel.SceneSyncRoot;
            public override bool IsReady => m_panel.IsLoaded && m_panel.ActualWidth >= 4 && m_panel.ActualHeight >= 4;
            // WPF: logical coordinates
            public override DPoint Center => new DPoint((int)(m_panel.ActualWidth / 2), (int)(m_panel.ActualHeight / 2));
            public override DPoint RandomPoint(Random rnd) => new DPoint(rnd.Next((int)m_panel.ActualWidth), rnd.Next((int)m_panel.ActualHeight));
            public override int? PickAt(DPoint p) => m_panel.PickAt(p, 0);
            public override void Invalidate() => m_panel.Invalidate();
            public override void Dispose() { /* unloading the panel frees its GL resources */ }
        }
        sealed class WinFormsStressPanel : StressPanel {
            readonly WinFormsPanel m_panel = new WinFormsPanel() { RendererType = RendererTypes.OpenGL };
            readonly WindowsFormsHost m_host;
            public WinFormsStressPanel() { m_host = new WindowsFormsHost { Child = m_panel }; }
            public override FrameworkElement Host => m_host;
            public override Scene Scene { get => m_panel.Scene; set => m_panel.Scene = value; }
            public override Group? SceneSyncRoot => m_panel.SceneSyncRoot;
            public override bool IsReady => !m_panel.IsDisposed && m_panel.IsHandleCreated && m_panel.ClientSize.Width >= 4 && m_panel.ClientSize.Height >= 4;
            // WinForms: pixel coordinates
            public override DPoint Center => new DPoint(m_panel.ClientSize.Width / 2, m_panel.ClientSize.Height / 2);
            public override DPoint RandomPoint(Random rnd) => new DPoint(rnd.Next(m_panel.ClientSize.Width), rnd.Next(m_panel.ClientSize.Height));
            public override int? PickAt(DPoint p) => m_panel.PickAt(p, 0);
            public override void Invalidate() => m_panel.Invalidate();
            public override void Dispose() {
                // disposing frees the GL resources (Dispose / OnHandleDestroyed), possibly while another panel's context is current
                m_host.Child = null;
                m_panel.Dispose();
                m_host.Dispose();
            }
        }
        #endregion

        readonly Random m_rnd = new Random(1234);
        readonly DispatcherTimer m_timer;
        readonly Stopwatch m_runtime = new Stopwatch();
        readonly List<StressPanel> m_panels = new List<StressPanel>();
        int m_panelCounter, m_sceneCounter;
        long m_picks, m_hits, m_centerOk, m_centerWrong, m_exceptions, m_created, m_closed, m_swaps, m_resizes, m_renders;

        public OpenGLMultiPanelStressWindow() {
            InitializeComponent();
            for (int i = 0; i < InitialPanels; i++) addPanel();
            // Background priority: WPF renders (Render priority) and WinForms paints run between two ticks, so picking
            // and rendering of different panels interleave like with real mouse input.
            m_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            m_timer.Tick += (s, e) => autoStep();
            Closed += (s, e) => {
                m_timer.Stop();
                foreach (var p in m_panels.ToList()) closePanel(p);
            };
            updateStatus();
        }

        #region panels & scenes
        StressPanel? randomPanel() => m_panels.Count == 0 ? null : m_panels[m_rnd.Next(m_panels.Count)];

        StressPanel addPanel() {
            bool winForms = cmbKind.SelectedIndex switch {
                1 => false,
                2 => true,
                _ => m_panelCounter % 2 == 1,   // mixed: alternate WPF / WinForms
            };
            StressPanel panel = winForms ? new WinFormsStressPanel() : new WpfStressPanel();
            panel.Name = $"P{++m_panelCounter}{(winForms ? "F" : "W")}";
            panel.Scene = createScene(panel.Name);
            panelHost.Children.Add(new WpfBorder {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Child = panel.Host
            });
            m_panels.Add(panel);
            panel.Scene.Configure();
            panel.Invalidate();
            m_created++;
            return panel;
        }

        void closePanel(StressPanel panel) {
            // removing the panel from the visual tree unloads (WPF) / disposing destroys (WinForms) it: its GL resources
            // are freed while (possibly) another panel's context is current.
            var border = panelHost.Children.OfType<WpfBorder>().First(b => b.Child == panel.Host);
            panelHost.Children.Remove(border);
            border.Child = null;
            m_panels.Remove(panel);
            panel.Dispose();
            m_closed++;
            log($"{panel.Name} geschlossen");
        }

        void swapScene(StressPanel panel) {
            panel.Scene = createScene(panel.Name);
            panel.Scene.Configure();
            panel.Invalidate();
            m_swaps++;
        }

        void resize() {
            // changes the size of all panels: WPF picking framebuffers must be resized in their own contexts
            panelHost.Columns = panelHost.Columns == 3 ? 2 : 3;
            Width = 900 + m_rnd.Next(700);
            Height = 600 + m_rnd.Next(300);
            m_resizes++;
        }

        Scene createScene(string panelName) {
            int sceneNo = ++m_sceneCounter;
            float phase = (float)m_rnd.NextDouble() * 6f;
            using (Scope.Enter()) {
                Array<float> x = linspace<float>(0f, 10f, 200);
                Array<float> y1 = sin(x + phase);
                Array<float> y2 = cos(x * 0.5f + phase) * 0.8f;

                var scene = new Scene();
                var plotCube = new PlotCube(twoDMode: m_rnd.Next(2) == 0) {
                    new LinePlot(x, y1, lineColor: DColor.Blue, markerStyle: MarkerStyle.Dot),
                    new LinePlot(x, y2, lineColor: DColor.DarkGreen),
                    new Legend("sin", "cos"),
                };
                plotCube.Axes.XAxis.Label.Text = $"x ({panelName}, scene {sceneNo})";
                plotCube.Axes.YAxis.Label.Text = "y";
                scene.Add(plotCube);

                // large label in the center of the panel: target of the center picks. It must live in its own
                // Screen2DNear group: scene.Screen has no render target set, so its content shares the depth buffer
                // with the plot cube and grid lines / line plots crossing the center would be picked instead.
                var center = new DLabel($"{panelName} center #{sceneNo}") {
                    Tag = CenterTag,
                    Position = new Vector3(0.5f, 0.5f, 0),
                    Anchor = new DPointF(0.5f, 0.5f),
                    Color = DColor.DarkBlue,
                };
                center.Font = new(center.Font.Name, 28f);
                scene.Add(new Group(target: RenderTarget.Screen2DNear) { center });

                // hover feedback: goes through OGLControl.OnMouseMove -> InputController -> PickAt
                center.MouseEnter += (s, a) => { center.Color = DColor.Red; center.Configure(); a.Refresh = true; };
                center.MouseLeave += (s, a) => { center.Color = DColor.DarkBlue; center.Configure(); a.Refresh = true; };
                scene.MouseMove += (s, a) => {
                    if (!a.DirectionUp) return;
                    var t = a.Target;
                    txtHover.Text = $"Hover {panelName}: {t?.GetType().Name ?? "-"} tag='{t?.Tag}' id={t?.ID}";
                };
                return scene;
            }
        }
        #endregion

        #region picking
        void pick(StressPanel panel, bool center) {
            // freshly created panels may not have rendered / created their context yet
            if (!panel.IsReady || panel.Age.ElapsedMilliseconds < 500) return;
            var pt = center ? panel.Center : panel.RandomPoint(m_rnd);
            int? id = panel.PickAt(pt);
            m_picks++;
            if (id.HasValue) m_hits++;
            if (center) {
                var node = id.HasValue ? panel.SceneSyncRoot?.FindById<Drawable>(id.Value) : null;
                if (node != null && node.Tag as string == CenterTag) {
                    m_centerOk++;
                } else {
                    m_centerWrong++;
                    log($"{panel.Name}: center pick -> id={id?.ToString() ?? "null"} ({node?.GetType().Name ?? "kein Knoten"}, tag='{node?.Tag}')");
                }
            }
        }

        void pickRound() {
            // alternate: render one panel (its context becomes current), then pick in all others
            foreach (var renderer in m_panels.ToList()) {
                renderer.Invalidate();
                foreach (var p in m_panels.Where(p => p != renderer).ToList()) {
                    pick(p, center: true);
                    pick(p, center: false);
                }
            }
        }
        #endregion

        #region auto stress
        void autoStep() {
            try {
                int r = m_rnd.Next(100);
                int count = m_panels.Count;
                if (count == 0 || (r < 3 && count < MaxPanels)) {
                    addPanel();
                } else if (r < 6 && count > 1) {
                    closePanel(randomPanel()!);
                } else if (r < 12) {
                    swapScene(randomPanel()!);
                } else if (r < 15) {
                    resize();
                } else if (r < 40) {
                    randomPanel()!.Invalidate();   // this panel renders next -> its context becomes current
                    m_renders++;
                } else {
                    pick(randomPanel()!, center: m_rnd.Next(2) == 0);
                }
            } catch (Exception exc) {
                m_exceptions++;
                log("Exception: " + exc.GetType().Name + ": " + exc.Message);
                Trace.WriteLine(exc.ToString());
            }
            updateStatus();
        }
        #endregion

        #region UI
        void updateStatus() {
            int wf = m_panels.Count(p => p is WinFormsStressPanel);
            txtStatus.Text = $"Panels: {m_panels.Count} (WPF {m_panels.Count - wf}, WinForms {wf}; geöffnet {m_created}, geschlossen {m_closed})  |  " +
                             $"Picks: {m_picks} (Treffer {m_hits})  |  center ok: {m_centerOk}  center falsch: {m_centerWrong}  |  Exceptions: {m_exceptions}  |  " +
                             $"Szenen: {m_swaps}  Resize: {m_resizes}  Renders: {m_renders}  |  Laufzeit: {m_runtime.Elapsed:hh\\:mm\\:ss}";
        }
        void log(string text) {
            lstLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
            while (lstLog.Items.Count > 300) lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
        }
        void guarded(Action action) {
            try { action(); } catch (Exception exc) { m_exceptions++; log("Exception: " + exc.GetType().Name + ": " + exc.Message); }
            updateStatus();
        }

        private void btnAdd_Click(object sender, RoutedEventArgs e) => guarded(() => { if (m_panels.Count < MaxPanels) addPanel(); });
        private void btnClose_Click(object sender, RoutedEventArgs e) => guarded(() => { var p = randomPanel(); if (p != null) closePanel(p); });
        private void btnSwap_Click(object sender, RoutedEventArgs e) => guarded(() => { foreach (var p in m_panels.ToList()) swapScene(p); });
        private void btnResize_Click(object sender, RoutedEventArgs e) => guarded(resize);
        private void btnPick_Click(object sender, RoutedEventArgs e) => guarded(pickRound);
        private void btnAuto_Checked(object sender, RoutedEventArgs e) { m_runtime.Start(); m_timer.Start(); }
        private void btnAuto_Unchecked(object sender, RoutedEventArgs e) { m_timer.Stop(); m_runtime.Stop(); }
        private void btnReset_Click(object sender, RoutedEventArgs e) {
            m_picks = m_hits = m_centerOk = m_centerWrong = m_exceptions = m_created = m_closed = m_swaps = m_resizes = m_renders = 0;
            m_runtime.Reset(); if (m_timer.IsEnabled) m_runtime.Start();
            lstLog.Items.Clear();
            updateStatus();
        }
        #endregion
    }
}
