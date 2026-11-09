using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ILNumerics.Drawing;
using ILNumerics.Drawing.Plotting;
using static ILNumerics.ILMath;
using DColor = System.Drawing.Color;
using DLabel = ILNumerics.Drawing.Label;
using DPoint = System.Drawing.Point;
using DPointF = System.Drawing.PointF;
using WpfPanel = ILNumerics.Drawing.Controls.WPF.Panel;
using WpfBorder = System.Windows.Controls.Border;

namespace ILNumerics.VisualDrawingTestApp.Windows {

    /// <summary>
    /// Visual / interactive regression test for multiple ILNumerics WPF OpenGL panels in one window.
    /// </summary>
    /// <remarks>
    /// <para>Each WPF OpenGL panel owns a dedicated, non-shared OpenGL context. Mouse picking, resizing and unloading
    /// happen outside of the panel's own render callback, i.e. possibly while ANOTHER panel's context is current.
    /// Before the fix, OGLControl.PickAt() did not make its context current (IOGLControl.EnsureCurrent() was empty):
    /// picking then used one panel's VAOs / framebuffers in another panel's context -> wrong pick results, rendering
    /// failures or an access violation in the GL driver (OGLControl.OnMouseMove -> PickAt -> ...
    /// OGLLabelRenderer.EnsureOglObjects -> GL.BindVertexArray).</para>
    /// <para>This window reproduces that situation: several OpenGL panels with plots and labels, panel creation / closing,
    /// scene replacement, resizing and picking - manually via mouse and buttons, or randomly in 'Auto-Stress' mode.
    /// Pass criteria: no crash, 'center falsch' = 0, 'Exceptions' = 0.</para>
    /// <para>Closed panels are never re-added: re-loading an unloaded OpenGL panel is a separate, known limitation.</para>
    /// </remarks>
    public partial class WpfMultiPanelStressWindow : Window {

        const string CenterTag = "center";
        const int InitialPanels = 3;
        const int MaxPanels = 6;

        sealed class PanelInfo {
            public string Name = "";
            public readonly Stopwatch Age = Stopwatch.StartNew();
        }

        readonly Random m_rnd = new Random(1234);
        readonly DispatcherTimer m_timer;
        readonly Stopwatch m_runtime = new Stopwatch();
        int m_panelCounter, m_sceneCounter;
        long m_picks, m_hits, m_centerOk, m_centerWrong, m_exceptions, m_created, m_closed, m_swaps, m_resizes, m_renders;

        public WpfMultiPanelStressWindow() {
            InitializeComponent();
            for (int i = 0; i < InitialPanels; i++) addPanel();
            // Background priority: WPF renders (Render priority) run between two ticks, so picking and rendering of
            // different panels interleave like with real mouse input.
            m_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            m_timer.Tick += (s, e) => autoStep();
            Closed += (s, e) => m_timer.Stop();
            updateStatus();
        }

        #region panels & scenes
        IEnumerable<WpfPanel> Panels => panelHost.Children.OfType<WpfBorder>().Select(b => b.Child).OfType<WpfPanel>();
        static PanelInfo info(WpfPanel p) => (PanelInfo)p.Tag;
        WpfPanel? randomPanel() {
            var panels = Panels.ToList();
            return panels.Count == 0 ? null : panels[m_rnd.Next(panels.Count)];
        }

        WpfPanel addPanel() {
            var name = $"P{++m_panelCounter}";
            var panel = new WpfPanel() { RendererType = RendererTypes.OpenGL, Tag = new PanelInfo { Name = name } };
            panel.Scene = createScene(name);
            panelHost.Children.Add(new WpfBorder {
                BorderBrush = System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Child = panel
            });
            panel.Scene.Configure();
            panel.Invalidate();
            m_created++;
            return panel;
        }

        void closePanel(WpfPanel panel) {
            // removing the panel from the visual tree unloads it: its GL resources are freed while (possibly) another
            // panel's context is current.
            var border = panelHost.Children.OfType<WpfBorder>().First(b => b.Child == panel);
            panelHost.Children.Remove(border);
            border.Child = null;
            m_closed++;
            log($"{info(panel).Name} geschlossen");
        }

        void swapScene(WpfPanel panel) {
            panel.Scene = createScene(info(panel).Name);
            panel.Scene.Configure();
            panel.Invalidate();
            m_swaps++;
        }

        void resize() {
            // changes the size of all panels: picking framebuffers must be resized in their own contexts
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
        void pick(WpfPanel panel, bool center) {
            var pi = info(panel);
            // freshly created panels may not have rendered / created their context yet
            if (!panel.IsLoaded || panel.ActualWidth < 4 || panel.ActualHeight < 4 || pi.Age.ElapsedMilliseconds < 500) return;
            var pt = center
                ? new DPoint((int)(panel.ActualWidth / 2), (int)(panel.ActualHeight / 2))
                : new DPoint(m_rnd.Next((int)panel.ActualWidth), m_rnd.Next((int)panel.ActualHeight));
            int? id = panel.PickAt(pt, 0);
            m_picks++;
            if (id.HasValue) m_hits++;
            if (center) {
                var node = id.HasValue ? panel.SceneSyncRoot?.FindById<Drawable>(id.Value) : null;
                if (node != null && node.Tag as string == CenterTag) {
                    m_centerOk++;
                } else {
                    m_centerWrong++;
                    log($"{pi.Name}: center pick -> id={id?.ToString() ?? "null"} ({node?.GetType().Name ?? "kein Knoten"}, tag='{node?.Tag}')");
                }
            }
        }

        void pickRound() {
            // alternate: render one panel (its context becomes current), then pick in all others
            foreach (var renderer in Panels.ToList()) {
                renderer.Invalidate();
                foreach (var p in Panels.Where(p => p != renderer)) {
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
                int count = Panels.Count();
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
            txtStatus.Text = $"Panels: {Panels.Count()} (geöffnet {m_created}, geschlossen {m_closed})  |  Picks: {m_picks} (Treffer {m_hits})  |  " +
                             $"center ok: {m_centerOk}  center falsch: {m_centerWrong}  |  Exceptions: {m_exceptions}  |  " +
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

        private void btnAdd_Click(object sender, RoutedEventArgs e) => guarded(() => { if (Panels.Count() < MaxPanels) addPanel(); });
        private void btnClose_Click(object sender, RoutedEventArgs e) => guarded(() => { var p = randomPanel(); if (p != null) closePanel(p); });
        private void btnSwap_Click(object sender, RoutedEventArgs e) => guarded(() => { foreach (var p in Panels.ToList()) swapScene(p); });
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
