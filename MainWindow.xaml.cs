using Microsoft.Win32;
using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace T0Prototype
{
    public partial class MainWindow : Window
    {
        // ===== 配置常量 =====
        private const double TARGET_CM = 6.0;          // 默认立方体尺寸 ~6cm
        private const int HANDLE = 16;                 // 窗口边缘拖动把手宽度(px)
        private const string APP_DIR = "T0Prototype";  // 数据目录名
        private const double S = 1.8;                  // 立方体边长(世界单位)
        private const double FOV = 45.0;               // 相机垂直视角

        // 物理参数（PRD §旋转物理）
        private const double SENS = 0.4 * Math.PI / 180;     // 灵敏度 0.4°/px
        private const double INERTIA = 0.94;                 // 惯性每帧衰减
        private const double MAXF = 720 * Math.PI / 180 / 60; // 限速 720°/s → 每帧上限
        private const double IDLE_MS = 5000;                 // 空闲 5s 后自转
        private const double IDLE_SPEED = 20 * Math.PI / 180 / 60; // 自转 20°/s
        private const double RESET_MS = 200;                 // 双击复位 200ms

        // 缩放（T8）
        private double _windowScale = 2.0;
        private const double WS_MIN = 1.0, WS_MAX = 4.0, WS_DEFAULT = 2.0;

        private IntPtr _hwnd;
        private NotifyIcon? _tray;
        private string _configPath = "";
        private string _facesDir = "";
        private string _zoomPath = "";
        private int _baseCubePx;
        private double _camZ;

        // ===== Media3D 场景 =====
        private Viewport3D? _vp;
        private PerspectiveCamera? _camera;
        private Model3DGroup? _pivot;     // 平移容器
        private Model3DGroup? _cube;      // 旋转容器
        private GeometryModel3D[] _faces = new GeometryModel3D[6];
        private DiffuseMaterial[] _faceMats = new DiffuseMaterial[6];
        private readonly Color[] _faceColors = new[]
        {
            Color.FromRgb(0xff,0x6b,0x6b),
            Color.FromRgb(0x4e,0xcd,0xc4),
            Color.FromRgb(0x45,0xb7,0xd1),
            Color.FromRgb(0xf9,0xca,0x24),
            Color.FromRgb(0x6c,0x5c,0xe7),
            Color.FromRgb(0xa2,0x9b,0xfe),
        };

        // ===== 交互状态 =====
        private bool _dragging, _panning, _wasPan, _resetting, _pulsing;
        private double _rx, _ry, _vx, _vy, _panX, _panY, _cubeScale = 1;
        private double _lastX, _lastY, _downX, _downY;
        private DateTime _downT, _lastMove, _resetStart, _pulseStart;
        private double _resetFromX, _resetFromY, _panXStart, _panYStart;
        private int _dragFace = -1;
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2200) };
        private readonly DispatcherTimer _tapTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private int _pendingTapFace = -1;

        private const int HOTKEY_TOGGLE = 1;
        private const int HOTKEY_RESET = 2;
        private const uint MOD_CS = 0x0002 | 0x0004; // MOD_CONTROL | MOD_SHIFT
        private const uint WM_HOTKEY = 0x0312;

        public MainWindow()
        {
            InitializeComponent();
            // T2 透明窗：事件挂在 RootGrid（无 WebView2 子窗口，WPF 原生控件输入正常）
            RootGrid.MouseDown += OnMouseDown;
            RootGrid.MouseMove += OnMouseMove;
            RootGrid.MouseUp += OnMouseUp;
            this.MouseDoubleClick += OnMouseDoubleClick;
            this.MouseWheel += OnMouseWheel;
            _toastTimer.Tick += (s, e) => { Toast.Opacity = 0; _toastTimer.Stop(); };
            _tapTimer.Tick += (s, e) => { _tapTimer.Stop(); if (_pendingTapFace >= 0) OpenFaceOrFlash(_pendingTapFace); _pendingTapFace = -1; };
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _vp = viewport;

            // ---- 本地数据目录（T6 持久化）----
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(local, APP_DIR);
            Directory.CreateDirectory(appDir);
            _facesDir = Path.Combine(appDir, "faces");
            Directory.CreateDirectory(_facesDir);
            _configPath = Path.Combine(appDir, "config.json");
            _zoomPath = Path.Combine(appDir, "zoom.txt");

            // ---- DPI 感知：6cm 基准像素（T3 尺寸）----
            var source = HwndSource.FromHwnd(_hwnd);
            double dpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            _baseCubePx = (int)(TARGET_CM * (96.0 / 2.54) * dpiScale);
            _windowScale = LoadZoom();
            ApplyWindowScale();

            // ---- 全局热键（T7）----
            RegisterHotKey(_hwnd, HOTKEY_TOGGLE, MOD_CS, (uint)Keys.H);
            RegisterHotKey(_hwnd, HOTKEY_RESET, MOD_CS, (uint)Keys.R);
            source?.AddHook(WndProcHotkey);

            // ---- 构建 3D 场景（T2 透明：AllowsTransparency 下 WPF 原生 3D 透明+输入天然正常）----
            BuildScene();
            _camZ = CamZFromScale(_windowScale);
            _camera = new PerspectiveCamera(new Point3D(0, 0, _camZ), new Vector3D(0, 0, -1),
                new Vector3D(0, 1, 0), FOV);
            _vp.Camera = _camera;
            // 全亮环境光 + 补光 → DiffuseMaterial 显示原始颜色（等价 Three.js MeshBasicMaterial）
            _vp.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
            _vp.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)) });
            _vp.Children.Add(new ModelVisual3D { Content = _pivot });
            ApplyTransforms();
            ApplySavedFaces();

            CompositionTarget.Rendering += OnRender;
            SetupTray();

            // 提示淡出
            var hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            hintTimer.Tick += (s, a) => { Hint.Opacity = 0; hintTimer.Stop(); };
            hintTimer.Start();
        }

        // ===== 场景构建 =====
        private void BuildScene()
        {
            _pivot = new Model3DGroup();
            _cube = new Model3DGroup();
            _pivot.Children.Add(_cube);
            AddFace(0, new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0), new Vector3D(0, 0, S / 2));
            AddFace(1, new AxisAngleRotation3D(new Vector3D(0, 1, 0), 180), new Vector3D(0, 0, -S / 2));
            AddFace(2, new AxisAngleRotation3D(new Vector3D(0, 1, 0), 90), new Vector3D(S / 2, 0, 0));
            AddFace(3, new AxisAngleRotation3D(new Vector3D(0, 1, 0), -90), new Vector3D(-S / 2, 0, 0));
            AddFace(4, new AxisAngleRotation3D(new Vector3D(1, 0, 0), -90), new Vector3D(0, S / 2, 0));
            AddFace(5, new AxisAngleRotation3D(new Vector3D(1, 0, 0), 90), new Vector3D(0, -S / 2, 0));
        }

        private void AddFace(int idx, AxisAngleRotation3D rot, Vector3D pos)
        {
            var geo = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new Point3D(-S/2, -S/2, 0),
                    new Point3D(S/2, -S/2, 0),
                    new Point3D(S/2, S/2, 0),
                    new Point3D(-S/2, S/2, 0),
                },
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
                TextureCoordinates = new PointCollection
                {
                    new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0),
                }
            };
            var mat = new DiffuseMaterial(MakePlaceholderBrush(_faceColors[idx], idx + 1));
            var model = new GeometryModel3D(geo, mat)
            {
                Transform = new Transform3DGroup
                {
                    Children = new Transform3DCollection
                    {
                        new RotateTransform3D(rot),
                        new TranslateTransform3D(pos),
                    }
                }
            };
            _faces[idx] = model;
            _faceMats[idx] = mat;
            _cube!.Children.Add(model);
        }

        private Brush MakePlaceholderBrush(Color color, int number)
        {
            int size = 256;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size));
                var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var tb = new FormattedText(number.ToString(), CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 120, Brushes.White, dpi);
                dc.DrawText(tb, new Point((size - tb.Width) / 2, (size - tb.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            return new ImageBrush(bmp) { Stretch = Stretch.Fill };
        }

        private void ApplyTransforms()
        {
            if (_cube == null || _pivot == null) return;
            var tg = new Transform3DGroup();
            tg.Children.Add(new ScaleTransform3D(_cubeScale, _cubeScale, _cubeScale));
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), _ry * 180 / Math.PI)));
            tg.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), _rx * 180 / Math.PI)));
            _cube.Transform = tg;
            _pivot.Transform = new TranslateTransform3D(_panX, _panY, 0);
        }

        // ===== 渲染循环（T4 物理）=====
        private void OnRender(object? sender, EventArgs e)
        {
            // T10 性能：窗口隐藏（收进托盘）时不空转重算/重绘，节省 CPU/GPU
            if (this.Visibility != Visibility.Visible) return;
            var now = DateTime.Now;
            if (_resetting)
            {
                double k = Math.Min(1, (now - _resetStart).TotalMilliseconds / RESET_MS);
                double ease = 1 - Math.Pow(1 - k, 3);
                _rx = _resetFromX * (1 - ease);
                _ry = _resetFromY * (1 - ease);
                _panX = _panXStart * (1 - ease);
                _panY = _panYStart * (1 - ease);
                if (k >= 1) _resetting = false;
            }
            else if (!_dragging)
            {
                if (Math.Abs(_vx) > 1e-5 || Math.Abs(_vy) > 1e-5)
                {
                    _ry += Math.Max(-MAXF, Math.Min(MAXF, _vy));
                    _rx += Math.Max(-MAXF, Math.Min(MAXF, _vx));
                    _vy *= INERTIA; _vx *= INERTIA;
                    if (Math.Abs(_vy) < 1e-5) _vy = 0;
                    if (Math.Abs(_vx) < 1e-5) _vx = 0;
                }
                else if ((now - _lastMove).TotalMilliseconds > IDLE_MS)
                {
                    _ry += IDLE_SPEED;
                }
            }
            if (_pulsing)
            {
                double pk = (now - _pulseStart).TotalMilliseconds / 220;
                if (pk >= 1) { _pulsing = false; _cubeScale = 1; }
                else _cubeScale = 1 + 0.06 * Math.Sin(pk * Math.PI);
            }
            ApplyTransforms();
        }

        // ===== 交互 =====
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var pt = e.GetPosition(this);
            bool onEdge = pt.X < HANDLE || pt.X > Width - HANDLE || pt.Y < HANDLE || pt.Y > Height - HANDLE;

            if (e.ChangedButton == MouseButton.Right)
            {
                int rf = HitTestFace(e.GetPosition(_vp!));
                if (rf >= 0) PickFaceImage(rf);
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;

            // 边缘或空白处 → 拖动窗口
            if (onEdge) { this.DragMove(); return; }
            int hit = HitTestFace(e.GetPosition(_vp!));
            if (hit < 0) { this.DragMove(); return; }

            _dragFace = hit;
            _dragging = true;
            _wasPan = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            _panning = _wasPan;
            _lastX = pt.X; _lastY = pt.Y;
            _downX = pt.X; _downY = pt.Y;
            _downT = DateTime.Now;
            _vx = 0; _vy = 0;
            _resetting = false;
            RootGrid.CaptureMouse();
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging) return;
            var pt = e.GetPosition(this);
            double dx = pt.X - _lastX, dy = pt.Y - _lastY;
            _lastX = pt.X; _lastY = pt.Y;
            if (_panning)
            {
                double wpp = S / _baseCubePx;
                _panX += dx * wpp; _panY -= dy * wpp;
                double fH = _camZ * Math.Tan(FOV * Math.PI / 180 / 2);
                double edge = 0.2;
                _panX = Math.Max(-fH + edge, Math.Min(fH - edge, _panX));
                _panY = Math.Max(-fH + edge, Math.Min(fH - edge, _panY));
            }
            else
            {
                double ry = dx * SENS, rx = dy * SENS;
                _ry += ry; _rx += rx;
                _vy = ry; _vx = rx;
                _lastMove = DateTime.Now;
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            RootGrid.ReleaseMouseCapture();
            var pt = e.GetPosition(this);
            double dist = Math.Sqrt((pt.X - _downX) * (pt.X - _downX) + (pt.Y - _downY) * (pt.Y - _downY));
            double dt = (DateTime.Now - _downT).TotalMilliseconds;
            if (!_wasPan && dist < 6 && dt < 400)
            {
                _pendingTapFace = _dragFace;   // T9：延迟执行，让双击(复位)优先
                _tapTimer.Stop(); _tapTimer.Start();
            }
            _lastMove = DateTime.Now;
        }

        private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            _tapTimer.Stop(); _pendingTapFace = -1;   // 取消待发的单击，避免误开照片
            _resetting = true;
            _resetStart = DateTime.Now;
            _resetFromX = _rx; _resetFromY = _ry;
            _panXStart = _panX; _panYStart = _panY;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _windowScale = Math.Max(WS_MIN, Math.Min(WS_MAX, _windowScale + e.Delta * 0.001));
            ApplyWindowScale();
            SaveZoom();
        }

        private int HitTestFace(Point pt)
        {
            var hit = VisualTreeHelper.HitTest(_vp!, pt) as RayMeshGeometry3DHitTestResult;
            if (hit?.ModelHit is GeometryModel3D m)
            {
                int idx = Array.IndexOf(_faces, m);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        // ===== T9 左键单击面：已换图→打开照片；占位面→脉冲+提示 =====
        private void OpenFaceOrFlash(int face)
        {
            var list = LoadConfig();
            var path = list[face];
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { }
            }
            else
            {
                _pulsing = true; _pulseStart = DateTime.Now;
                ShowToast($"第 {face + 1} 面（占位）· 右键点面可换图");
            }
        }

        private void ShowToast(string txt)
        {
            Toast.Text = txt;
            Toast.Opacity = 1;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ===== T6 换图 + 持久化 =====
        private void PickFaceImage(int face)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp",
                Title = $"选择第 {face + 1} 面的照片"
            };
            if (dlg.ShowDialog(this) != true) return;

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            string dest = Path.Combine(_facesDir, $"face{face}{ext}");
            File.Copy(dlg.FileName, dest, true);
            SaveFacePath(face, dest);
            SetFaceImage(face, dest);
        }

        private void SetFaceImage(int face, string path)
        {
            try
            {
                // Stream 加载：避免 UriSource 在 WPF 3D 下的文件锁/缓存问题
                BitmapImage bmp;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = fs;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                }
                bmp.Freeze();
                var newMat = new DiffuseMaterial(new ImageBrush(bmp) { Stretch = Stretch.Fill });
                _faces[face].Material = newMat;
                _faceMats[face] = newMat;
                // 强制整个 3D 视觉树重绘（确保 WPF 渲染管线采用新材质）
                _vp?.InvalidateVisual();
                if (_vp?.Parent is UIElement p) p.InvalidateVisual();
            }
            catch { }
        }

        private void ClearFace(int face)
        {
            SaveFacePath(face, null);
            var newMat = new DiffuseMaterial(MakePlaceholderBrush(_faceColors[face], face + 1));
            _faces[face].Material = newMat;
            _faceMats[face] = newMat;
        }

        private void ApplySavedFaces()
        {
            var list = LoadConfig();
            for (int i = 0; i < 6; i++)
            {
                var p = list[i];
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    SetFaceImage(i, p);
            }
        }

        private void ResetCube()
        {
            _resetting = true;
            _resetStart = DateTime.Now;
            _resetFromX = _rx; _resetFromY = _ry;
            _panXStart = _panX; _panYStart = _panY;
        }

        // ---- 配置读写（T6）----
        private List<string?> LoadConfig()
        {
            var list = new List<string?>(new string?[6]);
            for (int i = 0; i < 6; i++) list[i] = null;
            try
            {
                if (File.Exists(_configPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
                    if (doc.RootElement.TryGetProperty("faces", out var faces) &&
                        faces.ValueKind == JsonValueKind.Array)
                    {
                        int i = 0;
                        foreach (var f in faces.EnumerateArray())
                        {
                            if (i >= 6) break;
                            list[i] = f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
                                ? p.GetString() : null;
                            i++;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private void SaveFacePath(int face, string? path)
        {
            var list = LoadConfig();
            list[face] = path;
            var facesArr = new List<object>();
            for (int i = 0; i < 6; i++)
                facesArr.Add(new { index = i, path = list[i] });
            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(new { faces = facesArr },
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        // ===== 窗口缩放（T8）=====
        private void ApplyWindowScale()
        {
            int winPx = (int)(_baseCubePx * _windowScale) + 2 * HANDLE;
            this.Width = winPx;
            this.Height = winPx;
            this.Left = (SystemParameters.PrimaryScreenWidth - winPx) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - winPx) / 2;
            _camZ = CamZFromScale(_windowScale);
            if (_camera != null) _camera.Position = new Point3D(0, 0, _camZ);
        }

        private double LoadZoom()
        {
            try
            {
                if (File.Exists(_zoomPath) && double.TryParse(File.ReadAllText(_zoomPath), out var z))
                    return Math.Max(WS_MIN, Math.Min(WS_MAX, z));
            }
            catch { }
            return WS_DEFAULT;
        }

        private void SaveZoom()
        {
            try { File.WriteAllText(_zoomPath, _windowScale.ToString("F3")); } catch { }
        }

        // 相机距离：使 S 边长立方体在视口中占 1/SCALE 比例 → 视觉恒为 6cm
        private static double CamZFromScale(double scale)
        {
            double fovRad = FOV * Math.PI / 180.0;
            return S * scale / (2.0 * Math.Tan(fovRad / 2.0));
        }

        // ===== T7 全局热键 =====
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr WndProcHotkey(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_TOGGLE)
                {
                    this.Visibility = this.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
                    handled = true;
                }
                else if (id == HOTKEY_RESET)
                {
                    ResetCube();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // ===== 托盘图标（控制入口）=====
        private void SetupTray()
        {
            _tray = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "3D 立方体",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            var topItem = new ToolStripMenuItem("置顶显示") { Checked = this.Topmost };
            topItem.Click += (s, e) => { topItem.Checked = !topItem.Checked; this.Topmost = topItem.Checked; };
            menu.Items.Add(topItem);
            menu.Items.Add("显示 / 隐藏 (Ctrl+Shift+H)", null, (s, e) =>
            {
                this.Visibility = this.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            });
            menu.Items.Add("复位立方体 (Ctrl+Shift+R)", null, (s, e) => ResetCube());
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < 6; i++)
            {
                int f = i;
                menu.Items.Add($"替换第 {i + 1} 面图片", null, (s, e) => PickFaceImage(f));
            }
            menu.Items.Add(new ToolStripSeparator());
            for (int i = 0; i < 6; i++)
            {
                int f = i;
                menu.Items.Add($"清空第 {i + 1} 面", null, (s, e) => ClearFace(f));
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => this.Close());
            _tray.ContextMenuStrip = menu;
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotKey(_hwnd, HOTKEY_TOGGLE);
            UnregisterHotKey(_hwnd, HOTKEY_RESET);
            _tray?.Dispose();
            base.OnClosed(e);
        }
    }
}
