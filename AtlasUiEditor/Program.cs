// =====================================================================================
//  Atlas UI Editor  -  단일 파일 C# WinForms 애플리케이션
// =====================================================================================
//  기능:
//   - 아틀라스(PNG + JSON) 로드 및 스프라이트 크롭 렌더링
//   - TreeView 기반 부모/자식 노드 관리 (추가/삭제)
//   - PropertyGrid로 X, Y, Width, Height, Sprite(ImageUrl/AtlasRegion 두 필드에 동시 매핑) 편집
//   - 캔버스 실시간 렌더링, 10%~500% 줌, 자동 스크롤, 드래그 이동
//   - base_format.json 스키마(ColorHex/ImageUrl)와 실제 게임 export 스키마(AtlasRegion,
//     Type: Canvas/Panel/Button/Text/Image)를 모두 인식하여 저장/불러오기 (System.Text.Json)
//
//  실행 방법 (Windows, .NET 6/7/8 SDK 필요):
//     dotnet new winforms -n AtlasUIEditor
//     (생성된 Program.cs 를 이 파일로 교체)
//     dotnet run
// =====================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace AtlasUIEditor
{
    // =================================================================================
    //  진입점
    // =================================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                SpriteFitSelfTest.Run();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // 9-slice/fit 렌더링 로직 자가 점검. GUI 없이 `dotnet run --project AtlasUiEditor -- --selftest`
    // 로 실행 가능. 실패 시 예외를 던져 콘솔 종료 코드로 알 수 있다.
    internal static class SpriteFitSelfTest
    {
        public static void Run()
        {
            using var src = new Bitmap(30, 20);
            using (var sg = Graphics.FromImage(src))
            {
                sg.Clear(Color.Red);
                sg.FillRectangle(Brushes.Blue, 0, 0, 8, 8); // 좌상단 모서리 마커
            }

            // NineSlice: 모서리(8px)는 늘어나지 않고 원본 픽셀을 그대로 유지해야 한다.
            using (var dest = new Bitmap(100, 60))
            using (var g = Graphics.FromImage(dest))
            {
                CanvasPanel.DrawNineSlice(g, src, new RectangleF(0, 0, 100, 60), 8, 8, 8, 8);
                if (dest.GetPixel(2, 2) != src.GetPixel(2, 2))
                    throw new Exception("SpriteFitSelfTest 실패: NineSlice 모서리 픽셀이 보존되지 않음");
                if (dest.GetPixel(50, 30) == Color.FromArgb(0, 0, 0, 0))
                    throw new Exception("SpriteFitSelfTest 실패: NineSlice 중앙이 그려지지 않음");
            }

            // FitHeight: 높이는 rect에 꽉 차고, 비율 유지로 중앙 정렬되어 좌우에 여백이 생겨야 한다.
            using (var dest = new Bitmap(100, 40))
            using (var g = Graphics.FromImage(dest))
            {
                g.Clear(Color.Transparent);
                CanvasPanel.DrawFitHeight(g, src, new RectangleF(0, 0, 100, 40));
                if (dest.GetPixel(0, 20) != Color.FromArgb(0, 0, 0, 0))
                    throw new Exception("SpriteFitSelfTest 실패: FitHeight가 가로 중앙 정렬되지 않음(좌측 여백 없음)");
                if (dest.GetPixel(50, 20) == Color.FromArgb(0, 0, 0, 0))
                    throw new Exception("SpriteFitSelfTest 실패: FitHeight 중앙에 스프라이트가 그려지지 않음");
            }

            Console.WriteLine("SpriteFitSelfTest OK");
        }
    }

    // =================================================================================
    //  데이터 모델
    //  - base_format.json 스타일(ColorHex/ImageUrl)과
    //    실제 게임 export 스타일(AtlasRegion, Type: Canvas/Panel/Button/Text/Image) 모두 지원.
    //  - 두 스키마를 동시에 필드로 갖고, 편집 시(Sprite) 서로 동기화하여 호환성을 유지한다.
    // =================================================================================
    // 이미지 확장 방식 (Godot 4.7 export 시 그대로 대응됨)
    //  Stretch   : 전체 채우기 - 비율 무시하고 영역에 꽉 채움 (기존 기본 동작)
    //  NineSlice : 9-slice - 모서리(Slice*)는 고정 크기로 유지하고 중앙/모서리 사이만 늘림
    //              (Godot NinePatchRect의 patch_margin_left/top/right/bottom과 동일 개념)
    //  FitHeight : 상하 기준 fit - 높이를 영역에 맞추고 비율 유지, 가로 중앙 정렬 (좌우로 넘치거나 남을 수 있음)
    //  FitWidth  : 좌우 기준 fit - 너비를 영역에 맞추고 비율 유지, 세로 중앙 정렬 (상하로 넘치거나 남을 수 있음)
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ImageFitMode
    {
        Stretch,
        NineSlice,
        FitHeight,
        FitWidth
    }

    public class UINode
    {
        public string Id { get; set; } = "node_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "NewNode";
        public string Type { get; set; } = "Rect";     // Rect / Panel / Canvas / Image / Button / Text 등
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; } = 100;
        public float Height { get; set; } = 100;

        // 색상이 지정되지 않은 노드는 빈 문자열로 유지되어야 "채우지 않고 외곽선만" 그릴 수 있다.
        // (기본값을 회색으로 두면, ColorHex가 없는 JSON을 불러올 때 모든 노드가 불투명 회색으로
        //  덮여 화면이 뭉개지는 문제가 생긴다.)
        public string ColorHex { get; set; } = "";

        public string ImageUrl { get; set; } = "";     // base_format.json 호환 필드
        public string AtlasRegion { get; set; } = "";  // 실제 게임 export JSON 호환 필드 (Atlas.json의 name)

        public ImageFitMode FitMode { get; set; } = ImageFitMode.Stretch;

        // NineSlice 전용: 소스 스프라이트 기준 여백(px). Godot NinePatchRect의
        // patch_margin_left/top/right/bottom에 그대로 대입하면 된다.
        public int SliceLeft { get; set; }
        public int SliceRight { get; set; }
        public int SliceTop { get; set; }
        public int SliceBottom { get; set; }

        public List<UINode> Children { get; set; } = new List<UINode>();

        // 두 스키마 중 실제로 값이 들어있는 스프라이트 이름을 반환 (렌더링/저장 시 공용으로 사용)
        [JsonIgnore]
        public string EffectiveSprite => !string.IsNullOrEmpty(AtlasRegion) ? AtlasRegion : ImageUrl;

        // 저장 대상이 아닌 런타임 전용 필드
        [JsonIgnore] public UINode Parent { get; set; }
        [JsonIgnore] public TreeNode TreeNode { get; set; }
    }

    public class LayoutRoot
    {
        public UINode RootNode { get; set; }
    }

    // =================================================================================
    //  아틀라스 스프라이트 정보 (Atlas.json 구조)
    // =================================================================================
    public class AtlasSprite
    {
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public double ScaleApplied { get; set; }
    }

    // 로드된 아틀라스 스프라이트 이름 목록 (PropertyGrid 드롭다운에서 참조)
    public static class AtlasRegistry
    {
        public static List<string> SpriteNames { get; set; } = new List<string>();
    }

    // =================================================================================
    //  PropertyGrid 드롭다운용 TypeConverter
    // =================================================================================
    public class NodeTypeConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        // 목록에 없는 임의의 Type 문자열도 허용 (다양한 export 스키마 호환을 위해 비-배타적으로 설정)
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            => new StandardValuesCollection(new[] { "Rect", "Panel", "Canvas", "Image", "Button", "Text" });
    }

    public class SpriteNameConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => false;
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            => new StandardValuesCollection(AtlasRegistry.SpriteNames.ToArray());
    }

    // =================================================================================
    //  PropertyGrid 에 노출되는 프록시 객체
    //  - "Sprite" 라는 속성명은 유지하되, 내부적으로는 UINode.ImageUrl 필드에 매핑
    // =================================================================================
    [DefaultProperty("Name")]
    public class NodeProxy
    {
        private readonly UINode _node;
        private readonly CanvasPanel _canvas;

        public NodeProxy(UINode node, CanvasPanel canvas)
        {
            _node = node;
            _canvas = canvas;
        }

        [Category("식별"), Description("노드 고유 ID")]
        public string Id
        {
            get => _node.Id;
            set { _node.Id = value; }
        }

        [Category("식별"), Description("트리에 표시되는 이름")]
        public string Name
        {
            get => _node.Name;
            set { _node.Name = value; RefreshTreeText(); }
        }

        [Category("식별"), Description("Rect(색상 사각형) 또는 Image(스프라이트)")]
        [TypeConverter(typeof(NodeTypeConverter))]
        public string Type
        {
            get => _node.Type;
            set { _node.Type = value; RefreshTreeText(); _canvas.Invalidate(); }
        }

        [Category("Transform"), Description("부모 기준 상대 X 좌표")]
        public float X
        {
            get => _node.X;
            set { _node.X = value; _canvas.UpdateScrollSize(); _canvas.Invalidate(); }
        }

        [Category("Transform"), Description("부모 기준 상대 Y 좌표")]
        public float Y
        {
            get => _node.Y;
            set { _node.Y = value; _canvas.UpdateScrollSize(); _canvas.Invalidate(); }
        }

        [Category("Transform")]
        public float Width
        {
            get => _node.Width;
            set { _node.Width = Math.Max(1, value); _canvas.UpdateScrollSize(); _canvas.Invalidate(); }
        }

        [Category("Transform")]
        public float Height
        {
            get => _node.Height;
            set { _node.Height = Math.Max(1, value); _canvas.UpdateScrollSize(); _canvas.Invalidate(); }
        }

        [Category("외관"), Description("배경/틴트 색상 (예: #3182CE)")]
        public string ColorHex
        {
            get => _node.ColorHex;
            set { _node.ColorHex = value; _canvas.Invalidate(); }
        }

        // JSON 호환성 요구사항:
        // PropertyGrid 에는 "Sprite" 로 노출되지만, 실제 저장은 두 스키마 모두와 호환되도록
        // ImageUrl(base_format.json 스타일)과 AtlasRegion(실제 게임 export 스타일)에 동시에 매핑됨.
        [Category("외관"), Description("아틀라스 스프라이트 이름 (ImageUrl / AtlasRegion 필드에 동시 저장됨)")]
        [TypeConverter(typeof(SpriteNameConverter))]
        public string Sprite
        {
            get => _node.EffectiveSprite;
            set
            {
                var v = value ?? string.Empty;
                _node.ImageUrl = v;
                _node.AtlasRegion = v;
                _canvas.Invalidate();
            }
        }

        [Category("외관"), Description("이미지 확장 방식: Stretch=전체채우기, NineSlice=9-slice, FitHeight=상하기준, FitWidth=좌우기준")]
        public ImageFitMode FitMode
        {
            get => _node.FitMode;
            set { _node.FitMode = value; _canvas.Invalidate(); }
        }

        [Category("9-Slice"), Description("NineSlice 모드 전용. Godot patch_margin_left와 동일 (원본 스프라이트 기준 px)")]
        public int SliceLeft
        {
            get => _node.SliceLeft;
            set { _node.SliceLeft = Math.Max(0, value); _canvas.Invalidate(); }
        }

        [Category("9-Slice"), Description("NineSlice 모드 전용. Godot patch_margin_right와 동일 (원본 스프라이트 기준 px)")]
        public int SliceRight
        {
            get => _node.SliceRight;
            set { _node.SliceRight = Math.Max(0, value); _canvas.Invalidate(); }
        }

        [Category("9-Slice"), Description("NineSlice 모드 전용. Godot patch_margin_top와 동일 (원본 스프라이트 기준 px)")]
        public int SliceTop
        {
            get => _node.SliceTop;
            set { _node.SliceTop = Math.Max(0, value); _canvas.Invalidate(); }
        }

        [Category("9-Slice"), Description("NineSlice 모드 전용. Godot patch_margin_bottom와 동일 (원본 스프라이트 기준 px)")]
        public int SliceBottom
        {
            get => _node.SliceBottom;
            set { _node.SliceBottom = Math.Max(0, value); _canvas.Invalidate(); }
        }

        private void RefreshTreeText()
        {
            if (_node.TreeNode != null)
                _node.TreeNode.Text = $"{_node.Name} ({_node.Type})";
        }
    }

    // =================================================================================
    //  캔버스 : 실시간 렌더링 + 줌 + 드래그 이동 + 자동 스크롤
    // =================================================================================
    public class CanvasPanel : Panel
    {
        public UINode Root;
        public UINode SelectedNode;
        public float Zoom = 1.0f;
        public Func<string, Bitmap> SpriteResolver;

        public event Action<UINode> NodeSelected;
        public event Action NodeChanged;

        private bool _dragging;
        private PointF _dragStartLogical;
        private float _dragOrigX, _dragOrigY;

        public CanvasPanel()
        {
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = Color.FromArgb(45, 45, 45);
            MouseDown += CanvasPanel_MouseDown;
            MouseMove += CanvasPanel_MouseMove;
            MouseUp += CanvasPanel_MouseUp;
        }

        public void UpdateScrollSize()
        {
            if (Root == null) { AutoScrollMinSize = Size.Empty; return; }
            int w = (int)((Root.X + Root.Width) * Zoom) + 60;
            int h = (int)((Root.Y + Root.Height) * Zoom) + 60;
            AutoScrollMinSize = new Size(w, h);
        }

        private PointF ScreenToLogical(Point p)
        {
            float lx = (p.X - AutoScrollPosition.X) / Zoom;
            float ly = (p.Y - AutoScrollPosition.Y) / Zoom;
            return new PointF(lx, ly);
        }

        private UINode HitTest(UINode node, float absX, float absY, float px, float py)
        {
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var c = node.Children[i];
                var hit = HitTest(c, absX + c.X, absY + c.Y, px, py);
                if (hit != null) return hit;
            }
            if (px >= absX && px <= absX + node.Width && py >= absY && py <= absY + node.Height)
                return node;
            return null;
        }

        private void CanvasPanel_MouseDown(object sender, MouseEventArgs e)
        {
            if (Root == null) return;
            var logical = ScreenToLogical(e.Location);
            var hit = HitTest(Root, Root.X, Root.Y, logical.X, logical.Y);
            SelectedNode = hit;
            NodeSelected?.Invoke(hit);

            if (hit != null)
            {
                _dragging = true;
                _dragStartLogical = logical;
                _dragOrigX = hit.X;
                _dragOrigY = hit.Y;
            }
            Invalidate();
        }

        private void CanvasPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || SelectedNode == null) return;
            var logical = ScreenToLogical(e.Location);
            float dx = logical.X - _dragStartLogical.X;
            float dy = logical.Y - _dragStartLogical.Y;
            SelectedNode.X = _dragOrigX + dx;
            SelectedNode.Y = _dragOrigY + dy;
            NodeChanged?.Invoke();
            Invalidate();
        }

        private void CanvasPanel_MouseUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            g.ScaleTransform(Zoom, Zoom);
            if (Root != null)
                DrawNode(g, Root, Root.X, Root.Y);
        }

        private void DrawNode(Graphics g, UINode node, float absX, float absY)
        {
            var rect = new RectangleF(absX, absY, node.Width, node.Height);
            Bitmap sprite = null;
            var spriteName = node.EffectiveSprite; // AtlasRegion(실제 게임 export) 또는 ImageUrl(base_format) 둘 다 확인
            if (!string.IsNullOrEmpty(spriteName))
                sprite = SpriteResolver?.Invoke(spriteName);

            if (sprite != null)
            {
                DrawSprite(g, sprite, rect, node);
            }
            else if (!string.IsNullOrEmpty(node.ColorHex))
            {
                // 색상이 명시적으로 지정된 경우에만 채운다.
                Color c;
                try { c = ColorTranslator.FromHtml(node.ColorHex); }
                catch { c = Color.Gray; }
                using (var b = new SolidBrush(c))
                    g.FillRectangle(b, rect);
            }
            else
            {
                // 스프라이트도, 색상도 없는 노드(Panel/Canvas 같은 순수 구조용 컨테이너)는
                // 화면을 가리지 않도록 채우지 않고 점선 외곽선만 그려 위치/크기만 표시한다.
                using (var pen = new Pen(Color.FromArgb(120, 120, 120), 1f / Zoom) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            }

            bool hasFillOrImage = sprite != null || !string.IsNullOrEmpty(node.ColorHex);
            if (hasFillOrImage)
            {
                using (var borderPen = new Pen(Color.FromArgb(100, 100, 100), 1f / Zoom))
                    g.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
            }

            if (node == SelectedNode)
            {
                using (var selPen = new Pen(Color.Gold, 2f / Zoom) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(selPen, rect.X, rect.Y, rect.Width, rect.Height);
            }

            foreach (var child in node.Children)
                DrawNode(g, child, absX + child.X, absY + child.Y);
        }

        // node.FitMode에 따라 스프라이트를 rect 영역에 그린다. Godot 4.7의 TextureRect
        // stretch mode / NinePatchRect와 1:1 대응되도록 설계됨 (내보낸 FitMode/Slice* 값을
        // Godot 쪽에서 그대로 읽어 동일하게 재현할 수 있음).
        internal static void DrawSprite(Graphics g, Bitmap sprite, RectangleF rect, UINode node)
        {
            switch (node.FitMode)
            {
                case ImageFitMode.NineSlice:
                    DrawNineSlice(g, sprite, rect, node.SliceLeft, node.SliceRight, node.SliceTop, node.SliceBottom);
                    break;
                case ImageFitMode.FitHeight:
                    DrawFitHeight(g, sprite, rect);
                    break;
                case ImageFitMode.FitWidth:
                    DrawFitWidth(g, sprite, rect);
                    break;
                default:
                    g.DrawImage(sprite, rect);
                    break;
            }
        }

        // 상하 기준 fit: 높이를 rect에 맞추고 비율을 유지한 채 가로 중앙 정렬.
        internal static void DrawFitHeight(Graphics g, Bitmap sprite, RectangleF rect)
        {
            if (sprite.Height <= 0) return;
            float scale = rect.Height / sprite.Height;
            float w = sprite.Width * scale;
            g.DrawImage(sprite, new RectangleF(rect.X + (rect.Width - w) / 2f, rect.Y, w, rect.Height));
        }

        // 좌우 기준 fit: 너비를 rect에 맞추고 비율을 유지한 채 세로 중앙 정렬.
        internal static void DrawFitWidth(Graphics g, Bitmap sprite, RectangleF rect)
        {
            if (sprite.Width <= 0) return;
            float scale = rect.Width / sprite.Width;
            float h = sprite.Height * scale;
            g.DrawImage(sprite, new RectangleF(rect.X, rect.Y + (rect.Height - h) / 2f, rect.Width, h));
        }

        // 9-slice: 모서리(left/right/top/bottom)는 원본 픽셀 크기 그대로 유지하고,
        // 가장자리는 한 축으로만, 중앙은 양 축으로 늘려서 그린다. 여백이 0인 축은
        // 자동으로 생략되어 3-slice(가로 또는 세로 전용) 로도 동작한다.
        internal static void DrawNineSlice(Graphics g, Bitmap sprite, RectangleF rect, int left, int right, int top, int bottom)
        {
            int sw = sprite.Width, sh = sprite.Height;
            left = Math.Max(0, Math.Min(left, sw));
            right = Math.Max(0, Math.Min(right, sw - left));
            top = Math.Max(0, Math.Min(top, sh));
            bottom = Math.Max(0, Math.Min(bottom, sh - top));

            float dl = Math.Min(left, rect.Width / 2f);
            float dr = Math.Min(right, rect.Width / 2f);
            float dt = Math.Min(top, rect.Height / 2f);
            float db = Math.Min(bottom, rect.Height / 2f);

            float[] sx = { 0, left, sw - right, sw };
            float[] sy = { 0, top, sh - bottom, sh };
            float[] dx = { rect.X, rect.X + dl, rect.X + rect.Width - dr, rect.X + rect.Width };
            float[] dy = { rect.Y, rect.Y + dt, rect.Y + rect.Height - db, rect.Y + rect.Height };

            for (int row = 0; row < 3; row++)
            {
                float sH = sy[row + 1] - sy[row], dH = dy[row + 1] - dy[row];
                if (sH <= 0 || dH <= 0) continue;
                for (int col = 0; col < 3; col++)
                {
                    float sW = sx[col + 1] - sx[col], dW = dx[col + 1] - dx[col];
                    if (sW <= 0 || dW <= 0) continue;
                    g.DrawImage(sprite, new RectangleF(dx[col], dy[row], dW, dH), new RectangleF(sx[col], sy[row], sW, sH), GraphicsUnit.Pixel);
                }
            }
        }
    }

    // =================================================================================
    //  메인 폼
    // =================================================================================
    public class MainForm : Form
    {
        private CanvasPanel canvas;
        private TreeView treeView;
        private PropertyGrid propertyGrid;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;
        private NumericUpDown zoomInput;
        private ContextMenuStrip treeContextMenu;
        private SplitContainer mainSplit;
        private SplitContainer rightSplit;

        private Image atlasImage;
        private string atlasImagePath;
        private string atlasJsonPath;
        private string currentLayoutPath;

        private readonly Dictionary<string, AtlasSprite> atlasSprites =
            new Dictionary<string, AtlasSprite>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Bitmap> spriteCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        public MainForm()
        {
            Text = "Atlas UI Editor";
            Size = new Size(1400, 900);
            MinimumSize = new Size(900, 500);
            BackColor = Color.FromArgb(0x30, 0x30, 0x30);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            BuildUI();

            // SplitContainer 의 MinSize/FixedPanel/SplitterDistance 는 컨트롤이 실제 크기를
            // 갖기 전(생성 직후, 폼이 표시되기 전)에 설정하면 "Panel1MinSize 와 너비-Panel2MinSize
            // 사이여야 한다" 예외가 발생할 수 있다. 그래서 생성 시점에는 지정하지 않고,
            // 폼이 완전히 표시된 뒤(Shown) 실제 크기를 기준으로 한 번에 안전하게 적용한다.
            Shown += (s, e) =>
            {
                ConfigureSplitter(mainSplit, panel1Min: 200, panel2Min: 400, fixedPanel: FixedPanel.Panel1, desiredDistance: 260);
                ConfigureSplitter(rightSplit, panel1Min: 300, panel2Min: 260, fixedPanel: FixedPanel.Panel2, desiredDistance: rightSplit.Width - 320);
            };
            Resize += (s, e) =>
            {
                if (WindowState == FormWindowState.Minimized) return;
                SafeSetSplitterDistance(mainSplit, mainSplit.SplitterDistance);
                SafeSetSplitterDistance(rightSplit, rightSplit.SplitterDistance);
            };

            NewLayout();
        }

        // MinSize/FixedPanel을 지정한 뒤, 그 값들과 모순되지 않는 SplitterDistance를 안전하게 설정한다.
        private void ConfigureSplitter(SplitContainer sc, int panel1Min, int panel2Min, FixedPanel fixedPanel, int desiredDistance)
        {
            if (sc == null) return;

            // 폭이 두 최소 크기의 합보다 작으면 MinSize를 그 자리에서 줄여서 예외를 방지한다.
            int available = Math.Max(sc.Width, 1);
            if (panel1Min + panel2Min > available)
            {
                int half = Math.Max(1, available / 2);
                panel1Min = Math.Min(panel1Min, half);
                panel2Min = Math.Min(panel2Min, available - panel1Min);
            }

            try { sc.Panel1MinSize = panel1Min; } catch { /* 무시 */ }
            try { sc.Panel2MinSize = panel2Min; } catch { /* 무시 */ }
            try { sc.FixedPanel = fixedPanel; } catch { /* 무시 */ }

            SafeSetSplitterDistance(sc, desiredDistance);
        }

        // SplitterDistance 를 Panel1MinSize ~ (전체너비 - Panel2MinSize) 범위로 clamp 하여 설정.
        // 컨트롤이 아직 유효한 크기를 갖지 않은 경우(0 이하)에는 조용히 건너뛴다.
        private void SafeSetSplitterDistance(SplitContainer sc, int desired)
        {
            if (sc == null || sc.Width <= 0) return;

            int min = sc.Panel1MinSize;
            int max = sc.Width - sc.Panel2MinSize;

            if (max < min)
            {
                // 창이 너무 작아 두 최소 크기를 동시에 만족할 수 없는 경우, 절반으로 강제 설정
                int fallback = Math.Max(1, sc.Width / 2);
                try { sc.SplitterDistance = fallback; } catch { /* 무시 */ }
                return;
            }

            int clamped = Math.Min(Math.Max(desired, min), max);
            try { sc.SplitterDistance = clamped; } catch { /* 무시 */ }
        }

        // -----------------------------------------------------------------------------
        //  UI 구성
        // -----------------------------------------------------------------------------
        private void BuildUI()
        {
            // ---- 상태 표시줄 ----
            statusStrip = new StatusStrip { BackColor = Color.FromArgb(20, 20, 20) };
            statusLabel = new ToolStripStatusLabel("준비됨") { ForeColor = Color.Yellow };
            statusStrip.Items.Add(statusLabel);

            // ---- 상단 툴바 (TableLayoutPanel 사용) ----
            var toolbarPanel = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(24, 24, 24) };
            var toolbarTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 1,
                ColumnCount = 11,
                BackColor = Color.Transparent
            };
            for (int i = 0; i < toolbarTable.ColumnCount; i++)
                toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var btnLoadAtlasImg = MakeButton("아틀라스 이미지 로드");
            var btnLoadAtlasJson = MakeButton("아틀라스 정보 로드");
            var btnNew = MakeButton("새 레이아웃");
            var btnLoadLayout = MakeButton("레이아웃 불러오기");
            var btnSaveLayout = MakeButton("레이아웃 저장");
            var btnAddChild = MakeButton("자식 노드 추가");
            var btnRemove = MakeButton("노드 삭제");

            var lblZoom = new Label { Text = "Zoom:", ForeColor = Color.Gold, AutoSize = true, Margin = new Padding(14, 15, 2, 0) };
            zoomInput = new NumericUpDown { Minimum = 10, Maximum = 500, Value = 100, Width = 60, Margin = new Padding(2, 12, 2, 0) };
            var lblPercent = new Label { Text = "%", ForeColor = Color.White, AutoSize = true, Margin = new Padding(0, 15, 8, 0) };

            btnLoadAtlasImg.Click += BtnLoadAtlasImage_Click;
            btnLoadAtlasJson.Click += BtnLoadAtlasJson_Click;
            btnNew.Click += (s, e) =>
            {
                if (MessageBox.Show("현재 레이아웃을 지우고 새로 만드시겠습니까?", "확인",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    NewLayout();
            };
            btnLoadLayout.Click += BtnLoadLayout_Click;
            btnSaveLayout.Click += BtnSaveLayout_Click;
            btnAddChild.Click += AddChildNode;
            btnRemove.Click += RemoveNode;
            zoomInput.ValueChanged += (s, e) =>
            {
                canvas.Zoom = (float)zoomInput.Value / 100f;
                canvas.UpdateScrollSize();
                canvas.Invalidate();
            };

            int col = 0;
            toolbarTable.Controls.Add(btnLoadAtlasImg, col++, 0);
            toolbarTable.Controls.Add(btnLoadAtlasJson, col++, 0);
            toolbarTable.Controls.Add(btnNew, col++, 0);
            toolbarTable.Controls.Add(btnLoadLayout, col++, 0);
            toolbarTable.Controls.Add(btnSaveLayout, col++, 0);
            toolbarTable.Controls.Add(btnAddChild, col++, 0);
            toolbarTable.Controls.Add(btnRemove, col++, 0);
            toolbarTable.Controls.Add(lblZoom, col++, 0);
            toolbarTable.Controls.Add(zoomInput, col++, 0);
            toolbarTable.Controls.Add(lblPercent, col++, 0);

            toolbarPanel.Controls.Add(toolbarTable);

            // ---- 트리뷰 ----
            treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(37, 37, 38),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                HideSelection = false,
                FullRowSelect = true
            };
            treeView.AfterSelect += TreeView_AfterSelect;
            treeView.MouseUp += TreeView_MouseUp;

            treeContextMenu = new ContextMenuStrip();
            var miAdd = new ToolStripMenuItem("자식 노드 추가");
            miAdd.Click += AddChildNode;
            var miRemove = new ToolStripMenuItem("노드 삭제");
            miRemove.Click += RemoveNode;
            treeContextMenu.Items.Add(miAdd);
            treeContextMenu.Items.Add(miRemove);
            treeView.ContextMenuStrip = treeContextMenu;

            // ---- 캔버스 ----
            canvas = new CanvasPanel { Dock = DockStyle.Fill };
            canvas.SpriteResolver = GetSpriteBitmap;
            canvas.NodeSelected += OnCanvasNodeSelected;
            canvas.NodeChanged += OnCanvasNodeChanged;

            // ---- 프로퍼티 그리드 ----
            propertyGrid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                PropertySort = PropertySort.Categorized,
                ToolbarVisible = false,
                HelpVisible = true,
                BackColor = Color.FromArgb(30, 30, 30),
                LineColor = Color.FromArgb(60, 60, 60),
                CategoryForeColor = Color.Gold,
                ViewBackColor = Color.FromArgb(37, 37, 38),
                ViewForeColor = Color.White,
                HelpBackColor = Color.FromArgb(30, 30, 30),
                HelpForeColor = Color.White
            };

            // ---- 분할 컨테이너 구성 ----
            // MinSize/FixedPanel/SplitterDistance는 컨트롤이 아직 기본(작은) 폭일 때 지정하면
            // 즉시 예외가 발생할 수 있으므로 여기서는 지정하지 않고, 폼이 표시된 뒤(Shown)
            // 실제 크기를 기준으로 안전하게 설정한다.
            rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            rightSplit.Panel1.Controls.Add(canvas);
            rightSplit.Panel2.Controls.Add(propertyGrid);

            mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            mainSplit.Panel1.Controls.Add(treeView);
            mainSplit.Panel2.Controls.Add(rightSplit);

            // Dock 규칙: Top/Bottom 을 먼저 추가하고 Fill 을 마지막에 추가
            Controls.Add(mainSplit);
            Controls.Add(statusStrip);
            Controls.Add(toolbarPanel);
        }

        private Button MakeButton(string text)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                Height = 30,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(4, 8, 4, 4)
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 80, 80);
            return b;
        }

        // -----------------------------------------------------------------------------
        //  아틀라스 로드
        // -----------------------------------------------------------------------------
        private void BtnLoadAtlasImage_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "PNG 이미지|*.png|모든 파일|*.*" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var loaded = Image.FromFile(ofd.FileName);
                atlasImage?.Dispose();
                atlasImage = loaded;
                atlasImagePath = ofd.FileName;
                spriteCache.Clear();
                SetStatus($"아틀라스 이미지 로드 완료: {Path.GetFileName(ofd.FileName)} ({atlasImage.Width}x{atlasImage.Height})");
                canvas.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("이미지 로드 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("아틀라스 이미지 로드 실패");
            }
        }

        private void BtnLoadAtlasJson_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "아틀라스 JSON|*.json" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var text = File.ReadAllText(ofd.FileName);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<AtlasSprite>>(text, opts) ?? new List<AtlasSprite>();

                atlasSprites.Clear();
                foreach (var s in list)
                    if (!string.IsNullOrEmpty(s.Name))
                        atlasSprites[s.Name] = s;

                AtlasRegistry.SpriteNames = atlasSprites.Keys.OrderBy(k => k).ToList();
                spriteCache.Clear();
                atlasJsonPath = ofd.FileName;

                SetStatus($"아틀라스 정보 로드 완료: {Path.GetFileName(ofd.FileName)} (스프라이트 {list.Count}개)");
                canvas.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("아틀라스 JSON 로드 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("아틀라스 정보 로드 실패");
            }
        }

        private Bitmap GetSpriteBitmap(string name)
        {
            if (string.IsNullOrEmpty(name) || atlasImage == null) return null;
            if (spriteCache.TryGetValue(name, out var cached)) return cached;
            if (!atlasSprites.TryGetValue(name, out var info)) return null;
            if (info.Width <= 0 || info.Height <= 0) return null;

            try
            {
                var srcRect = new Rectangle(info.X, info.Y, info.Width, info.Height);
                var bmp = new Bitmap(info.Width, info.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(atlasImage, new Rectangle(0, 0, info.Width, info.Height), srcRect, GraphicsUnit.Pixel);
                }
                spriteCache[name] = bmp;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------------
        //  레이아웃 새로 만들기 / 불러오기 / 저장 (base_format.json 호환)
        // -----------------------------------------------------------------------------
        private void NewLayout()
        {
            var root = new UINode
            {
                Id = "root_panel",
                Name = "MainCanvas",
                Type = "Rect",
                X = 0,
                Y = 0,
                Width = 800,
                Height = 600,
                ColorHex = "#2D3748",
                ImageUrl = "",
                Children = new List<UINode>()
            };
            SetRoot(root);
            currentLayoutPath = null;
            SetStatus("새 레이아웃이 생성되었습니다.");
        }

        private void BtnLoadLayout_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog { Filter = "레이아웃 JSON|*.json" };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var text = File.ReadAllText(ofd.FileName);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var layout = JsonSerializer.Deserialize<LayoutRoot>(text, opts);
                if (layout?.RootNode == null)
                    throw new Exception("RootNode 를 찾을 수 없습니다. base_format.json 구조를 확인하세요.");

                LinkParents(layout.RootNode, null);
                SetRoot(layout.RootNode);
                currentLayoutPath = ofd.FileName;
                SetStatus($"레이아웃 불러오기 완료: {Path.GetFileName(ofd.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("레이아웃 로드 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("레이아웃 로드 실패");
            }
        }

        private void BtnSaveLayout_Click(object sender, EventArgs e)
        {
            if (canvas.Root == null)
            {
                MessageBox.Show("저장할 레이아웃이 없습니다.");
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = "레이아웃 JSON|*.json",
                FileName = currentLayoutPath != null ? Path.GetFileName(currentLayoutPath) : "layout.json"
            };
            if (sfd.ShowDialog() != DialogResult.OK) return;

            try
            {
                var layout = new LayoutRoot { RootNode = canvas.Root };
                var opts = new JsonSerializerOptions { WriteIndented = true };
                var text = JsonSerializer.Serialize(layout, opts);
                File.WriteAllText(sfd.FileName, text);
                currentLayoutPath = sfd.FileName;
                SetStatus($"레이아웃 저장 완료: {Path.GetFileName(sfd.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("저장 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("레이아웃 저장 실패");
            }
        }

        private void LinkParents(UINode node, UINode parent)
        {
            node.Parent = parent;
            node.Children ??= new List<UINode>();
            foreach (var c in node.Children)
                LinkParents(c, node);
        }

        private void SetRoot(UINode root)
        {
            canvas.Root = root;
            canvas.SelectedNode = null;
            canvas.UpdateScrollSize();
            canvas.AutoScrollPosition = new Point(0, 0); // 이전 레이아웃의 스크롤 위치가 남아 상단이 잘려 보이는 문제 방지
            BuildTree();
            propertyGrid.SelectedObject = null;
            canvas.Invalidate();
        }

        // -----------------------------------------------------------------------------
        //  트리뷰 구성 및 이벤트
        // -----------------------------------------------------------------------------
        private void BuildTree()
        {
            treeView.BeginUpdate();
            treeView.Nodes.Clear();
            if (canvas.Root != null)
            {
                var tn = CreateTreeNode(canvas.Root);
                treeView.Nodes.Add(tn);
                treeView.ExpandAll();
                // TreeView는 Nodes.Clear()/Add() 이후에도 이전 스크롤 위치를 유지하는 경우가 있어
                // 최상단 노드가 잘려 보일 수 있다. 명시적으로 스크롤을 맨 위로 되돌린다.
                treeView.TopNode = treeView.Nodes[0];
            }
            treeView.EndUpdate();
        }

        private TreeNode CreateTreeNode(UINode node)
        {
            var tn = new TreeNode($"{node.Name} ({node.Type})") { Tag = node };
            node.TreeNode = tn;
            foreach (var c in node.Children)
                tn.Nodes.Add(CreateTreeNode(c));
            return tn;
        }

        private void TreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            var node = (UINode)e.Node.Tag;
            canvas.SelectedNode = node;
            propertyGrid.SelectedObject = new NodeProxy(node, canvas);
            canvas.Invalidate();
        }

        private void TreeView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var node = treeView.GetNodeAt(e.Location);
                if (node != null) treeView.SelectedNode = node;
            }
        }

        private void OnCanvasNodeSelected(UINode node)
        {
            if (node?.TreeNode != null)
                treeView.SelectedNode = node.TreeNode; // AfterSelect 에서 PropertyGrid 갱신됨
            else
                propertyGrid.SelectedObject = null;
        }

        private void OnCanvasNodeChanged()
        {
            propertyGrid.Refresh();
        }

        // -----------------------------------------------------------------------------
        //  노드 추가 / 삭제
        // -----------------------------------------------------------------------------
        private void AddChildNode(object sender, EventArgs e)
        {
            var parent = canvas.SelectedNode ?? canvas.Root;
            if (parent == null)
            {
                MessageBox.Show("먼저 새 레이아웃을 생성하세요.");
                return;
            }

            var child = new UINode
            {
                Id = "node_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = "NewNode",
                Type = "Rect",
                X = 10,
                Y = 10,
                Width = 100,
                Height = 50,
                ColorHex = "#888888",
                ImageUrl = "",
                Children = new List<UINode>(),
                Parent = parent
            };
            parent.Children.Add(child);

            BuildTree();
            canvas.SelectedNode = child;
            if (child.TreeNode != null) treeView.SelectedNode = child.TreeNode;
            canvas.UpdateScrollSize();
            canvas.Invalidate();
            SetStatus($"자식 노드 추가됨: {child.Id} (부모: {parent.Id})");
        }

        private void RemoveNode(object sender, EventArgs e)
        {
            var node = canvas.SelectedNode;
            if (node == null)
            {
                MessageBox.Show("삭제할 노드를 선택하세요.");
                return;
            }
            if (node.Parent == null)
            {
                MessageBox.Show("루트 노드는 삭제할 수 없습니다.");
                return;
            }

            var confirm = MessageBox.Show($"'{node.Name}' 노드를 삭제하시겠습니까?", "확인",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            node.Parent.Children.Remove(node);
            canvas.SelectedNode = null;
            propertyGrid.SelectedObject = null;
            BuildTree();
            canvas.Invalidate();
            SetStatus($"노드 삭제됨: {node.Id}");
        }

        private void SetStatus(string msg)
        {
            statusLabel.Text = msg;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            atlasImage?.Dispose();
            foreach (var bmp in spriteCache.Values) bmp.Dispose();
            base.OnFormClosed(e);
        }
    }
}