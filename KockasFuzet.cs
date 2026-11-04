using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kockás füzet")]
[assembly: AssemblyDescription("Magyar, lapozható négyzetrácsos füzet")]
[assembly: AssemblyProduct("Kockás füzet")]
[assembly: AssemblyVersion("1.0.1.0")]

[DataContract]
sealed class Notebook
{
    public const int CoverLineLimit = 32;
    public static readonly char[] LineBreaks = { '\r', '\n', '\u0085', '\u2028', '\u2029' };
    [DataMember(IsRequired = true)] public int Version = 1;
    [DataMember(IsRequired = true)] public string Kind = "kockas";
    [DataMember(IsRequired = true)] public List<PageData> Pages = new List<PageData>();
    [DataMember] public int Position;
    [DataMember(EmitDefaultValue = false)] public bool RightPage;
    [DataMember(EmitDefaultValue = false)] public string CoverLineOne;
    [DataMember(EmitDefaultValue = false)] public string CoverLineTwo;

    public static bool IsCoverLine(string text)
    {
        return text == null || (text.Length <= CoverLineLimit && text.IndexOfAny(LineBreaks) < 0);
    }

    public static Notebook Empty()
    {
        var book = new Notebook();
        for (int i = 0; i < 64; i++) book.Pages.Add(new PageData());
        return book;
    }
}

[DataContract]
sealed class PageData
{
    [DataMember] public string Text = "";
    [DataMember] public string Font = "Segoe Print";
    [DataMember(EmitDefaultValue = false)] public string Ink;
}

static class Storage
{
    const long MaxBytes = 32 * 1024 * 1024;
    public const int MaxPages = 2048;
    static readonly DataContractJsonSerializer Json = new DataContractJsonSerializer(typeof(Notebook));

    public static readonly string LocalFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KockasFuzet", "fuzet.json");

    public static Notebook Read(string path)
    {
        using (var input = File.OpenRead(path))
        {
            if (input.Length > MaxBytes) throw new InvalidDataException();
            var book = Json.ReadObject(input) as Notebook;
            if (book == null || book.Version != 1 || book.Kind != "kockas" || book.Pages == null ||
                book.Pages.Count < 2 || book.Pages.Count > MaxPages || book.Pages.Count % 2 != 0 ||
                book.Position < 0 || book.Position >= book.Pages.Count || book.Position % 2 != 0 ||
                !Notebook.IsCoverLine(book.CoverLineOne) || !Notebook.IsCoverLine(book.CoverLineTwo))
                throw new InvalidDataException();
            foreach (var page in book.Pages)
            {
                if (page == null || page.Text == null || page.Text.Length > Sheet.MaxText ||
                    Array.IndexOf(Sheet.Fonts, page.Font) < 0)
                    throw new InvalidDataException();
                if (page.Ink != null) ReadInk(page.Ink);
            }
            return book;
        }
    }

    public static void Write(string path, Notebook book)
    {
        string folder = Path.GetDirectoryName(path);
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                Json.WriteObject(output, book);
                if (output.Length > MaxBytes)
                    throw new IOException("A füzet mérete meghaladja a 32 MB-ot.");
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static StrokeCollection ReadInk(string ink)
    {
        if (ink == null) return new StrokeCollection();
        using (var input = new MemoryStream(Convert.FromBase64String(ink)))
            return new StrokeCollection(input);
    }

    public static string WriteInk(StrokeCollection strokes)
    {
        if (strokes.Count == 0) return null;
        using (var output = new MemoryStream())
        {
            strokes.Save(output);
            return Convert.ToBase64String(output.ToArray());
        }
    }
}

static class Palette
{
    public static Color Color(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(Color(hex));
        brush.Freeze();
        return brush;
    }
}

sealed class Sheet : Grid
{
    public const int PageWidth = 420;
    public const int PageHeight = 594;
    public const int MaxText = 4000;
    const int Cell = 14;
    const int GridLeft = 28;
    const int GridTop = 66;
    const int GridWidth = 26 * Cell;
    const int GridHeight = 35 * Cell;
    const int FirstBaseline = GridTop + 3 * Cell;
    const int LineStep = 2 * Cell;
    const int LineCount = 16;
    const int TextSize = 17;
    const int EditorTop = 85;
    const int EditorHeight = LineCount * LineStep + 2;
    public static readonly string[] Fonts = {
        "Segoe Print", "Georgia", "Segoe UI", "Consolas", "Times New Roman"
    };
    static readonly RoutedUICommand[] RichFormatting = {
        EditingCommands.ToggleBold, EditingCommands.ToggleItalic, EditingCommands.ToggleUnderline,
        EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript,
        EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
        EditingCommands.AlignLeft, EditingCommands.AlignCenter, EditingCommands.AlignRight,
        EditingCommands.AlignJustify, EditingCommands.ToggleBullets, EditingCommands.ToggleNumbering,
        EditingCommands.IncreaseIndentation, EditingCommands.DecreaseIndentation
    };

    public readonly RichTextBox Editor;
    readonly InkPresenter ink;
    readonly Canvas paper = new Canvas();
    readonly bool isLeft;
    bool updating;
    bool rejecting;
    public PageData Data { get; private set; }
    public StrokeCollection Ink { get { return ink.Strokes; } }
    public event Action<Sheet> Activated;
    public event Action Changed;
    public event Action Full;

    public Sheet(bool left)
    {
        isLeft = left;
        Width = PageWidth;
        Height = PageHeight;
        ClipToBounds = true;
        var parchment = new LinearGradientBrush(Palette.Color("#FFFDF4"), Palette.Color("#F6EFD9"),
            new Point(left ? 0 : 1, 0), new Point(left ? 1 : 0, 0));
        parchment.GradientStops.Add(new GradientStop(Palette.Color("#FAF6E8"), 0.91));
        parchment.GradientStops.Add(new GradientStop(Palette.Color("#DED4B9"), 0.997));
        parchment.GradientStops.Add(new GradientStop(Palette.Color("#C2B394"), 1));
        Background = parchment;
        paper.IsHitTestVisible = false;
        Children.Add(paper);

        Editor = new RichTextBox {
            Language = XmlLanguage.GetLanguage("hu-HU"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(GridLeft + 2 * Cell - 5, EditorTop, GridLeft + Cell - 5, 0),
            Height = EditorHeight,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Foreground = Palette.Brush("#20395B"),
            AcceptsTab = false,
            FontSize = TextSize,
            IsDocumentEnabled = false,
            UndoLimit = 80
        };
        Editor.Template = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
            " TargetType='RichTextBox'><ScrollViewer x:Name='PART_ContentHost' Focusable='False' " +
            "Background='{TemplateBinding Background}' Padding='0'/></ControlTemplate>");
        Editor.TextChanged += OnTextChanged;
        Editor.GotKeyboardFocus += delegate { if (Activated != null) Activated(this); };
        CommandManager.AddPreviewExecutedHandler(Editor, delegate(object sender, ExecutedRoutedEventArgs e) {
            if (Array.IndexOf(RichFormatting, e.Command as RoutedUICommand) >= 0) e.Handled = true;
        });
        Editor.LayoutUpdated += AlignText;
        DataObject.AddPastingHandler(Editor, PasteText);
        Children.Add(Editor);

        // The spread draws live ink. This presenter draws the page's ink for its animation texture.
        ink = new InkPresenter { IsHitTestVisible = false, Opacity = 0 };
        Children.Add(ink);
    }

    static void PasteText(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }
        string text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (text == null || text.Length > MaxText)
        {
            e.CancelCommand();
            var sheet = ((RichTextBox)sender).Parent as Sheet;
            if (sheet != null && sheet.Full != null) sheet.Full();
            return;
        }
        e.DataObject = new DataObject(DataFormats.UnicodeText, text);
        e.FormatToApply = DataFormats.UnicodeText;
    }

    void DrawPaper(int number)
    {
        paper.Children.Clear();
        var grid = Palette.Brush("#BECBDC");
        for (int x = GridLeft; x <= GridLeft + GridWidth; x += Cell)
            Rule(x, GridTop, 0.8, GridHeight + 0.8, grid);
        for (int y = GridTop; y <= GridTop + GridHeight; y += Cell)
            Rule(GridLeft, y, GridWidth + 0.8, 0.8, grid);
        var header = new TextBlock {
            Text = "J E G Y Z E T", FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Brush("#826255")
        };
        Canvas.SetLeft(header, GridLeft);
        Canvas.SetTop(header, 35);
        paper.Children.Add(header);
        var footer = new TextBlock {
            Text = number.ToString("00"), FontFamily = new FontFamily("Georgia"), FontSize = 14,
            Foreground = Palette.Brush("#826255")
        };
        if (isLeft) Canvas.SetLeft(footer, 25);
        else Canvas.SetRight(footer, 25);
        Canvas.SetTop(footer, 566);
        paper.Children.Add(footer);
    }

    void Rule(double x, double y, double width, double height, Brush brush)
    {
        var line = new Border { Width = width, Height = height, Background = brush };
        Canvas.SetLeft(line, x);
        Canvas.SetTop(line, y);
        paper.Children.Add(line);
    }

    FlowDocument Document(string text, string font)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            if (lines[i].Length > 0) paragraph.Inlines.Add(new Run(lines[i]));
        }
        var doc = new FlowDocument(paragraph) {
            PagePadding = new Thickness(0), FontSize = TextSize, FontFamily = new FontFamily(font),
            LineHeight = LineStep, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        // Enter and plain-text paste can create further paragraphs.
        var style = new Style(typeof(Paragraph));
        style.Setters.Add(new Setter(Paragraph.MarginProperty, new Thickness(0)));
        doc.Resources.Add(typeof(Paragraph), style);
        return doc;
    }

    public void Load(PageData data, int number)
    {
        updating = true;
        try
        {
            Data = data;
            Editor.Document = Document(data.Text, data.Font);
            Editor.ScrollToHome();
            ink.Strokes = Storage.ReadInk(data.Ink);
            DrawPaper(number);
        }
        finally { updating = false; }
    }

    void AlignText(object sender, EventArgs e)
    {
        if (Data == null || !Editor.IsMeasureValid) return;
        var pointer = Editor.Document.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward);
        if (pointer == null) return;
        Rect line = pointer.GetCharacterRect(LogicalDirection.Forward);
        if (line.IsEmpty) return;
        var face = new Typeface(Editor.Document.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        GlyphTypeface glyph;
        if (!face.TryGetGlyphTypeface(out glyph)) return;
        var margin = Editor.Margin;
        double top = FirstBaseline - line.Top - glyph.Baseline * TextSize;
        if (top < EditorTop - 8 || top > EditorTop + 8 || Math.Abs(margin.Top - top) < 0.1) return;
        Editor.Margin = new Thickness(margin.Left, top, margin.Right, 0);
    }

    bool Fits()
    {
        Editor.UpdateLayout();
        var scroll = Editor.Template.FindName("PART_ContentHost", Editor) as ScrollViewer;
        return scroll == null || scroll.ViewportHeight == 0 ||
            scroll.ExtentHeight <= scroll.ViewportHeight + 0.5;
    }

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (updating || rejecting || Data == null) return;
        var document = Editor.Document;
        string text = PlainText(document);
        if (text.Length > MaxText || !Fits())
        {
            rejecting = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(delegate {
                try
                {
                    if (Editor.Document != document) return;
                    if (Editor.CanUndo) Editor.Undo();
                    if (PlainText(Editor.Document) != Data.Text)
                    {
                        Editor.Document = Document(Data.Text, Data.Font);
                        Editor.CaretPosition = Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                    }
                    Editor.ScrollToHome();
                }
                finally { rejecting = false; }
            }));
            if (Full != null) Full();
            return;
        }
        Data.Text = text;
        if (Changed != null) Changed();
    }

    static string PlainText(FlowDocument document)
    {
        string text = new TextRange(document.ContentStart, document.ContentEnd).Text;
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
            return text.Substring(0, text.Length - 2);
        return text;
    }

    public bool SetFont(string font)
    {
        if (Data.Font == font) return true;
        Editor.Document.FontFamily = new FontFamily(font);
        if (!Fits())
        {
            Editor.Document.FontFamily = new FontFamily(Data.Font);
            Editor.ScrollToHome();
            return false;
        }
        Data.Font = font;
        if (Changed != null) Changed();
        return true;
    }

    public void SaveInk(StrokeCollection strokes)
    {
        ink.Strokes = strokes;
        Data.Ink = Storage.WriteInk(strokes);
    }

    public BitmapSource Snapshot()
    {
        var bounds = new Rect(0, 0, PageWidth, PageHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.Fill }, null, bounds);
        var result = new RenderTargetBitmap(PageWidth * 2, PageHeight * 2, 192, 192, PixelFormats.Pbgra32);
        ink.Opacity = 1;
        try
        {
            ink.Measure(bounds.Size);
            ink.Arrange(bounds);
            result.Render(visual);
        }
        finally { ink.Opacity = 0; }
        result.Freeze();
        return result;
    }
}

sealed class PageTurn : Grid
{
    const int Strips = 44;
    readonly MeshGeometry3D front;
    readonly MeshGeometry3D back;
    readonly int direction;
    readonly double flexibility;
    readonly Border shadow;
    readonly TranslateTransform shadowOffset = new TranslateTransform();

    public PageTurn(BitmapSource left, BitmapSource right, BitmapSource face, BitmapSource reverse, int direction,
        double flexibility = 0.89)
    {
        this.direction = direction;
        this.flexibility = flexibility;
        Width = 2 * Sheet.PageWidth;
        Height = Sheet.PageHeight;
        Children.Add(new Image { Source = left, Width = Sheet.PageWidth, HorizontalAlignment = HorizontalAlignment.Left });
        Children.Add(new Image { Source = right, Width = Sheet.PageWidth, HorizontalAlignment = HorizontalAlignment.Right });
        var shade = new LinearGradientBrush(Colors.Transparent, Colors.Transparent, 0);
        shade.GradientStops.Add(new GradientStop(Palette.Color("#500C1A19"), 0.5));
        shadow = new Border {
            Width = 60, HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = shadowOffset, Background = shade
        };
        Children.Add(shadow);

        var viewport = new Viewport3D {
            ClipToBounds = false,
            Camera = new OrthographicCamera(new Point3D(0, 0, 1800),
                new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), Width)
        };
        var models = new Model3DGroup();
        models.Children.Add(new AmbientLight(Palette.Color("#DBDBDB")));
        models.Children.Add(new DirectionalLight(Palette.Color("#242424"), new Vector3D(-0.4, -0.3, -1)));
        models.Children.Add(Surface(face, direction < 0, direction < 0, out front));
        models.Children.Add(Surface(reverse, direction > 0, direction > 0, out back));
        viewport.Children.Add(new ModelVisual3D { Content = models });
        Children.Add(viewport);
        Frame(0);
    }

    static GeometryModel3D Surface(BitmapSource image, bool reverseWinding, bool mirror, out MeshGeometry3D mesh)
    {
        mesh = new MeshGeometry3D();
        for (int i = 0; i <= Strips; i++)
        {
            double u = (double)i / Strips;
            if (mirror) u = 1 - u;
            mesh.TextureCoordinates.Add(new Point(u, 0));
            mesh.TextureCoordinates.Add(new Point(u, 1));
        }
        for (int i = 0; i < Strips; i++)
        {
            int j = i * 2;
            int a = reverseWinding ? j + 2 : j + 1;
            int b = reverseWinding ? j + 1 : j + 2;
            foreach (int index in new[] { j, a, b, b, a, j + 3 })
                mesh.TriangleIndices.Add(index);
        }
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        brush.Freeze();
        return new GeometryModel3D(mesh, new DiffuseMaterial(brush));
    }

    public void Frame(double progress)
    {
        double turn = Math.PI * progress;
        double curl = flexibility * Math.Sin(turn);
        double x = 0;
        double z = 2;
        double bend = Math.Sin(turn);
        double step = (double)Sheet.PageWidth / Strips;
        var points = new Point3DCollection(2 * (Strips + 1));
        for (int i = 0; i <= Strips; i++)
        {
            double along = (double)i / Strips;
            double lift = bend * along * along;
            points.Add(new Point3D(direction * x, Sheet.PageHeight / 2.0 - 20 * lift, z));
            points.Add(new Point3D(direction * x, -Sheet.PageHeight / 2.0 + 12 * lift, z));
            if (i == Strips) break;
            double angle = turn + curl * (1 - 2.0 * (i + 0.5) / Strips);
            x += Math.Cos(angle) * step;
            z += Math.Sin(angle) * step;
        }
        points.Freeze();
        front.Positions = points;
        back.Positions = points;
        shadow.Opacity = bend * 0.75;
        double edge = Sheet.PageWidth + direction * x;
        shadowOffset.X = Math.Max(0, Math.Min(2 * Sheet.PageWidth - shadow.Width, edge - 30));
    }
}

sealed class NotebookWindow : Window
{
    const double LabelLeft = 120;
    const double LabelWidth = 181;
    const double LabelLineOne = 177;
    const double LabelLineTwo = 215;
    const double LabelFontSize = 17;
    static readonly FontFamily LabelFont = new FontFamily("Segoe Print");
    static readonly Brush LabelInk = Palette.Brush("#20395B");
    static readonly Brush Cream = Palette.Brush("#FFF4E9");
    static readonly Brush Muted = Palette.Brush("#E8BFB1");
    static readonly Brush Coral = Palette.Brush("#FFAC8C");
    static readonly Brush Accent = Palette.Brush("#A44934");
    static readonly Brush ButtonFill = Palette.Brush("#71392F");
    static readonly ControlTemplate ButtonTemplate = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='Frame' CornerRadius='7' Background='{TemplateBinding Background}' " +
        "BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
        "Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='#FFAC8C'/></Trigger>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='#FFE2CB'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.65'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Frame' Property='Opacity' Value='0.35'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");
    static readonly ControlTemplate PageEdgeTemplate = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='Edge' Background='Transparent'>" +
        "<ContentPresenter x:Name='Arrow' HorizontalAlignment='Center' VerticalAlignment='Center' Opacity='0'/>" +
        "</Border><ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Edge' Property='Background' Value='#27F28362'/>" +
        "<Setter TargetName='Arrow' Property='Opacity' Value='1'/></Trigger>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Edge' Property='Background' Value='#40F28362'/>" +
        "<Setter TargetName='Arrow' Property='Opacity' Value='1'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Edge' Property='Opacity' Value='0'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    readonly Sheet left = new Sheet(true);
    readonly Sheet right = new Sheet(false);
    readonly Grid bookArea = new Grid { Width = 2 * Sheet.PageWidth, Height = Sheet.PageHeight };
    readonly Grid spread = new Grid();
    readonly InkCanvas spreadInk = new InkCanvas { Background = Brushes.Transparent, ClipToBounds = true };
    static readonly Matrix ToRightPage = new Matrix(1, 0, 0, 1, -Sheet.PageWidth, 0);
    static readonly Matrix FromRightPage = new Matrix(1, 0, 0, 1, Sheet.PageWidth, 0);
    readonly TextBlock status = Label("", 12, Muted);
    readonly TextBlock pageCounter = Label("", 13, Cream);
    readonly TextBlock fontCaption = Label("", 11, Muted);
    readonly ComboBox fonts = new ComboBox { Width = 155, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
    readonly Button pen;
    readonly Button type;
    readonly Button eraser;
    readonly Button previous;
    readonly Button next;
    readonly Button leftEdge;
    readonly Button rightEdge;
    readonly Button import;
    readonly Button export;
    readonly Border sleeve;
    readonly Button coverButton;
    readonly Grid coverPanel = new Grid { Width = Sheet.PageWidth, Height = Sheet.PageHeight };
    readonly TextBox coverLineOne;
    readonly TextBox coverLineTwo;
    readonly DrawingImage cover = CreateCover();
    readonly DockPanel toolbar;
    readonly StackPanel pager;
    readonly Button closeCover;
    readonly TextBlock openingHint = Label("A címke két sora írható. Kinyitáshoz kattints a borító narancssárga részére.", 13, Cream);
    Notebook book;
    Sheet active;
    PageTurn flipping;
    EventHandler animation;
    InkCanvasEditingMode mode;
    bool syncingFont;
    bool dirty;
    bool localReadFailed;
    bool isOpen;

    public NotebookWindow(Notebook initial = null)
    {
        Title = "Kockás füzet";
        Language = XmlLanguage.GetLanguage("hu-HU");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = 1060;
        Height = 900;
        MinWidth = 760;
        MinHeight = 535;
        MaxHeight = SystemParameters.WorkArea.Height;
        FontFamily = new FontFamily("Segoe UI");
        Background = Palette.Brush("#542A24");
        Icon = CreateIcon();

        var layout = new Grid { Background = Background };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;

        var masthead = new DockPanel { LastChildFill = true, Margin = new Thickness(28, 19, 28, 14) };
        var fileTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        import = MakeButton("Importálás", Import, "Füzet megnyitása fájlból (Ctrl+O)");
        export = MakeButton("Exportálás", Export, "Mentés hordozható füzetfájlba");
        fileTools.Children.Add(import);
        fileTools.Children.Add(export);
        DockPanel.SetDock(fileTools, Dock.Right);
        masthead.Children.Add(fileTools);
        var brand = new StackPanel();
        brand.Children.Add(Label("P A P Í R  É S  T I N T A", 10, Coral));
        var title = Label("Kockás füzet", 31, Cream);
        title.FontFamily = new FontFamily("Georgia");
        title.Margin = new Thickness(0, 1, 0, 0);
        brand.Children.Add(title);
        masthead.Children.Add(brand);
        layout.Children.Add(masthead);

        toolbar = new DockPanel { Margin = new Thickness(28, 0, 28, 0), LastChildFill = true };
        var fontTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        fontCaption.VerticalAlignment = VerticalAlignment.Center;
        fontCaption.Margin = new Thickness(0, 0, 10, 0);
        fontTools.Children.Add(fontCaption);
        foreach (string font in Sheet.Fonts)
            fonts.Items.Add(new ComboBoxItem { Content = font, FontFamily = new FontFamily(font) });
        fonts.SelectionChanged += FontChanged;
        System.Windows.Automation.AutomationProperties.SetName(fonts, "Betűtípus");
        fontTools.Children.Add(fonts);
        DockPanel.SetDock(fontTools, Dock.Right);
        toolbar.Children.Add(fontTools);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        type = MakeButton("Gépelés", delegate { SetMode(InkCanvasEditingMode.None); }, "Írás billentyűzettel");
        pen = MakeButton("Toll", delegate { SetMode(InkCanvasEditingMode.Ink); }, "Írás egérrel vagy digitális tollal");
        eraser = MakeButton("Radír", delegate { SetMode(InkCanvasEditingMode.EraseByStroke); }, "Kézírás törlése vonásonként");
        tools.Children.Add(type);
        tools.Children.Add(pen);
        tools.Children.Add(eraser);
        toolbar.Children.Add(tools);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);

        sleeve = new Border {
            Background = Palette.Brush("#ED795A"), BorderBrush = Palette.Brush("#FFB497"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 8, 9), IsHitTestVisible = false
        };
        var edges = new Border {
            Background = Palette.Brush("#E6D5CA"), BorderBrush = Palette.Brush("#BB755D"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2)
        };
        sleeve.Child = edges;
        var stageFrame = new Grid { Width = 2 * Sheet.PageWidth + 26, Height = Sheet.PageHeight + 22 };
        bookArea.VerticalAlignment = VerticalAlignment.Top;
        bookArea.Margin = new Thickness(0, 7, 0, 0);
        stageFrame.Children.Add(sleeve);
        stageFrame.Children.Add(bookArea);
        var stage = new Viewbox {
            Child = stageFrame, Stretch = Stretch.Uniform,
            Margin = new Thickness(29, 18, 29, 13)
        };
        Grid.SetRow(stage, 2);
        layout.Children.Add(stage);
        left.HorizontalAlignment = HorizontalAlignment.Left;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        spread.Children.Add(left);
        spread.Children.Add(right);
        spreadInk.DefaultDrawingAttributes.Color = Palette.Color("#223F70");
        spreadInk.DefaultDrawingAttributes.Width = spreadInk.DefaultDrawingAttributes.Height = 2.5;
        spreadInk.EditingModeInverted = InkCanvasEditingMode.EraseByStroke;
        spreadInk.PreviewMouseDown += delegate(object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton == MouseButton.Left) ActivateInkPage(e.GetPosition(spreadInk));
        };
        spreadInk.PreviewStylusDown += delegate(object sender, StylusDownEventArgs e) {
            ActivateInkPage(e.GetPosition(spreadInk));
        };
        spread.Children.Add(spreadInk);
        leftEdge = MakePageEdge(-1);
        rightEdge = MakePageEdge(1);
        spread.Children.Add(leftEdge);
        spread.Children.Add(rightEdge);
        bookArea.Children.Add(spread);
        coverButton = new Button {
            Width = Sheet.PageWidth, Height = Sheet.PageHeight, Cursor = Cursors.Hand,
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Template = ButtonTemplate,
            Content = new Image { Source = cover, Width = Sheet.PageWidth, Height = Sheet.PageHeight }
        };
        System.Windows.Automation.AutomationProperties.SetName(coverButton, "Füzet kinyitása");
        coverButton.Click += delegate { SetCoverOpen(true); };
        coverPanel.Children.Add(coverButton);
        var labelFields = new Canvas();
        coverLineOne = MakeCoverLine("A borítócímke első sora", LabelLineOne);
        coverLineTwo = MakeCoverLine("A borítócímke második sora", LabelLineTwo);
        coverLineOne.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.Key != Key.Enter) return;
            coverLineTwo.Focus();
            e.Handled = true;
        };
        labelFields.Children.Add(coverLineOne);
        labelFields.Children.Add(coverLineTwo);
        coverPanel.Children.Add(labelFields);
        bookArea.Children.Add(coverPanel);
        foreach (var sheet in new[] { left, right })
        {
            sheet.Activated += Activate;
            sheet.Changed += MarkDirty;
            sheet.Full += PageFull;
        }

        var footer = new Grid { Margin = new Thickness(28, 0, 28, 18) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        pager = new StackPanel { Orientation = Orientation.Horizontal };
        previous = MakeButton("‹  Előző", delegate { Turn(-1); }, "Előző oldalpár (Ctrl+Page Up)");
        next = MakeButton("Következő  ›", delegate { Turn(1); }, "Következő oldalpár (Ctrl+Page Down)");
        pager.Children.Add(previous);
        pageCounter.MinWidth = 116;
        pageCounter.TextAlignment = TextAlignment.Center;
        pageCounter.VerticalAlignment = VerticalAlignment.Center;
        pager.Children.Add(pageCounter);
        pager.Children.Add(next);
        Grid.SetColumn(pager, 1);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(status);
        footer.Children.Add(pager);
        openingHint.HorizontalAlignment = HorizontalAlignment.Center;
        openingHint.VerticalAlignment = VerticalAlignment.Center;
        openingHint.IsHitTestVisible = false;
        Grid.SetColumn(openingHint, 1);
        footer.Children.Add(openingHint);
        closeCover = MakeButton("Becsukás", delegate { SetCoverOpen(false); }, "A füzet becsukása");
        closeCover.HorizontalAlignment = HorizontalAlignment.Right;
        closeCover.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(closeCover, 2);
        footer.Children.Add(closeCover);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        autosave.Tick += delegate { autosave.Stop(); SaveLocal(); };
        PreviewKeyDown += Shortcut;
        Closing += OnClosing;
        book = initial ?? ReadLocal();
        LoadCoverLabel();
        LoadSpread();
        SetMode(InkCanvasEditingMode.None);
        Loaded += delegate { coverButton.Focus(); };
    }

    static DrawingImage CreateCover()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            var bounds = new Rect(0, 0, Sheet.PageWidth, Sheet.PageHeight);
            var orange = new LinearGradientBrush(Palette.Color("#F18060"), Palette.Color("#F8916E"), 35);
            orange.GradientStops.Add(new GradientStop(Palette.Color("#F68764"), 0.65));
            dc.DrawRectangle(orange, null, bounds);
            var squares = new Pen(Palette.Brush("#D9FFF1DE"), 1);
            for (int x = 8; x < Sheet.PageWidth; x += 15)
                dc.DrawLine(squares, new Point(x, 0), new Point(x, Sheet.PageHeight));
            for (int y = 8; y < Sheet.PageHeight; y += 15)
                dc.DrawLine(squares, new Point(0, y), new Point(Sheet.PageWidth, y));
            var spine = new LinearGradientBrush(Palette.Color("#70A2513B"), Colors.Transparent, 0);
            dc.DrawRectangle(spine, null, new Rect(0, 0, 16, Sheet.PageHeight));
            dc.DrawRectangle(null, new Pen(Palette.Brush("#C7644D"), 1), new Rect(0.5, 0.5,
                Sheet.PageWidth - 1, Sheet.PageHeight - 1));

            var red = Palette.Brush("#C94E32");
            var label = new Rect(82, 112, 256, 160);
            dc.DrawGeometry(Palette.Brush("#FFFCF7"), null, LabelOutline(label, 22));
            label.Inflate(-6, -6);
            dc.DrawGeometry(null, new Pen(red, 6), LabelOutline(label, 18));
            label.Inflate(-8, -8);
            dc.DrawGeometry(null, new Pen(red, 1.5), LabelOutline(label, 13));
            var rule = new Pen(Palette.Brush("#BB6B57"), 1.1);
            dc.DrawLine(rule, new Point(LabelLeft, LabelLineOne), new Point(LabelLeft + LabelWidth, LabelLineOne));
            dc.DrawLine(rule, new Point(LabelLeft, LabelLineTwo), new Point(LabelLeft + LabelWidth, LabelLineTwo));
            var code = new FormattedText("27–32", CultureInfo.GetCultureInfo("hu-HU"),
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 12, red, 1);
            dc.DrawText(code, new Point(270, 135));
            dc.DrawEllipse(null, new Pen(red, 1.1), new Point(291, 239), 13, 11);
            dc.DrawGeometry(red, null, Geometry.Parse("M 291,228 L 291,240 284,240 Z M 293,232 L 298,240 293,240 Z M 283,241 L 299,241 295,245 287,245 Z"));
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    static Geometry LabelOutline(Rect rect, double corner)
    {
        var outline = new StreamGeometry();
        using (var path = outline.Open())
        {
            path.BeginFigure(new Point(rect.Left + corner, rect.Top), true, true);
            path.PolyLineTo(new[] {
                new Point(rect.Right - corner, rect.Top), new Point(rect.Right, rect.Top + corner),
                new Point(rect.Right, rect.Bottom - corner), new Point(rect.Right - corner, rect.Bottom),
                new Point(rect.Left + corner, rect.Bottom), new Point(rect.Left, rect.Bottom - corner),
                new Point(rect.Left, rect.Top + corner)
            }, true, false);
        }
        outline.Freeze();
        return outline;
    }

    static FormattedText FormatCoverLine(string value, double size)
    {
        return new FormattedText(value, CultureInfo.GetCultureInfo("hu-HU"), FlowDirection.LeftToRight,
            new Typeface(LabelFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, LabelInk, 1);
    }

    TextBox MakeCoverLine(string name, double baseline)
    {
        var field = new TextBox {
            FontFamily = LabelFont, FontSize = LabelFontSize, Foreground = LabelInk, CaretBrush = LabelInk,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Width = LabelWidth, Height = 30, MaxLength = Notebook.CoverLineLimit, AcceptsReturn = false,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        System.Windows.Automation.AutomationProperties.SetName(field, name);
        Canvas.SetLeft(field, LabelLeft);
        Canvas.SetTop(field, baseline - FormatCoverLine("Á", LabelFontSize).Baseline);
        field.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e) {
            if (e.Text.IndexOfAny(Notebook.LineBreaks) >= 0) e.Handled = true;
        };
        DataObject.AddPastingHandler(field, delegate(object sender, DataObjectPastingEventArgs e) {
            var value = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
            if (value == null || value.IndexOfAny(Notebook.LineBreaks) >= 0) e.CancelCommand();
            else e.FormatToApply = DataFormats.UnicodeText;
        });
        field.TextChanged += delegate {
            var text = FormatCoverLine(field.Text, LabelFontSize);
            field.FontSize = LabelFontSize * Math.Min(1, (LabelWidth - 5) / Math.Max(1, text.WidthIncludingTrailingWhitespace));
            Canvas.SetTop(field, baseline - FormatCoverLine("Á", field.FontSize).Baseline);
            if (field == coverLineOne)
            {
                if ((book.CoverLineOne ?? "") == field.Text) return;
                book.CoverLineOne = field.Text;
            }
            else
            {
                if ((book.CoverLineTwo ?? "") == field.Text) return;
                book.CoverLineTwo = field.Text;
            }
            MarkDirty();
        };
        return field;
    }

    void LoadCoverLabel()
    {
        coverLineOne.Text = book.CoverLineOne ?? "";
        coverLineTwo.Text = book.CoverLineTwo ?? "";
    }

    static void DrawCoverLine(DrawingContext dc, TextBox field, double baseline)
    {
        if (field.Text.Length == 0) return;
        var text = FormatCoverLine(field.Text, field.FontSize);
        dc.DrawText(text, new Point(LabelLeft + 2, baseline - text.Baseline));
    }

    static ImageSource CreateIcon()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(Accent, null, new Rect(0, 0, 32, 32), 6, 6);
            dc.DrawRoundedRectangle(Coral, null, new Rect(6, 4, 21, 24), 2, 2);
            var rule = new Pen(Cream, 0.85);
            for (int y = 7; y < 27; y += 4) dc.DrawLine(rule, new Point(7, y), new Point(26, y));
            for (int x = 9; x < 26; x += 4) dc.DrawLine(rule, new Point(x, 5), new Point(x, 27));
            dc.DrawRoundedRectangle(Cream, new Pen(Accent, 0.8), new Rect(10, 10, 13, 8), 1, 1);
        }
        var icon = new DrawingImage(drawing);
        icon.Freeze();
        return icon;
    }

    static TextBlock Label(string text, double size, Brush color)
    {
        return new TextBlock { Text = text, FontSize = size, Foreground = color };
    }

    static Button MakeButton(string text, Action action, string tip)
    {
        var button = new Button {
            Content = text, Foreground = Cream, Background = ButtonFill,
            BorderBrush = Palette.Brush("#A96552"), BorderThickness = new Thickness(1),
            Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(0, 0, 6, 0),
            FontSize = 13, MinHeight = 34, Template = ButtonTemplate, ToolTip = tip
        };
        button.Click += delegate { action(); };
        return button;
    }

    Button MakePageEdge(int direction)
    {
        bool backward = direction < 0;
        string action = backward ? "Előző oldalpár" : "Következő oldalpár";
        var edge = new Button {
            Content = backward ? "‹" : "›",
            Template = PageEdgeTemplate, Width = 23, FontSize = 25,
            Cursor = Cursors.Hand, Foreground = Accent,
            HorizontalAlignment = backward ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            ToolTip = action
        };
        System.Windows.Automation.AutomationProperties.SetName(edge, action);
        edge.Click += delegate { Turn(direction); };
        return edge;
    }

    Notebook ReadLocal()
    {
        if (!File.Exists(Storage.LocalFile))
        {
            status.Text = "Helyi automatikus mentés";
            return Notebook.Empty();
        }
        try
        {
            var stored = Storage.Read(Storage.LocalFile);
            status.Text = "Füzet betöltve";
            return stored;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            localReadFailed = true;
            status.Text = "A helyi füzet nem olvasható";
            return Notebook.Empty();
        }
    }

    static bool IsFileError(Exception ex)
    {
        return ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException ||
               ex is ArgumentException || ex is FormatException || ex is System.Xml.XmlException;
    }

    void LoadSpread()
    {
        left.Load(book.Pages[book.Position], book.Position + 1);
        right.Load(book.Pages[book.Position + 1], book.Position + 2);
        LoadSpreadInk();
        Activate(book.RightPage ? right : left);
        RefreshNavigation();
    }

    void LoadSpreadInk()
    {
        var strokes = new StrokeCollection();
        foreach (var stroke in left.Ink) SplitInk(stroke, strokes, null);
        foreach (var stroke in right.Ink)
        {
            var shifted = stroke.Clone();
            shifted.Transform(FromRightPage, false);
            SplitInk(shifted, null, strokes);
        }
        spreadInk.Strokes.StrokesChanged -= SpreadInkChanged;
        spreadInk.Strokes = strokes;
        spreadInk.Strokes.StrokesChanged += SpreadInkChanged;
    }

    static void SplitInk(Stroke stroke, StrokeCollection leftStrokes, StrokeCollection rightStrokes)
    {
        Rect bounds = stroke.GetBounds();
        if (bounds.Right < Sheet.PageWidth)
        {
            if (leftStrokes != null) leftStrokes.Add(stroke.Clone());
            return;
        }
        if (bounds.Left >= Sheet.PageWidth)
        {
            if (rightStrokes != null) rightStrokes.Add(stroke.Clone());
            return;
        }

        var points = stroke.StylusPoints;
        StylusPoint previous = points[0];
        bool onRight = previous.X >= Sheet.PageWidth;
        var part = new StylusPointCollection(points.Description) { previous };
        for (int i = 1; i < points.Count; i++)
        {
            StylusPoint point = points[i];
            bool nextOnRight = point.X >= Sheet.PageWidth;
            if (nextOnRight != onRight)
            {
                // Both page fragments reach the same point at the spine, including on diagonal strokes.
                double fraction = (Sheet.PageWidth - previous.X) / (point.X - previous.X);
                var seam = previous;
                seam.X = Sheet.PageWidth;
                seam.Y += fraction * (point.Y - previous.Y);
                seam.PressureFactor += (float)(fraction * (point.PressureFactor - previous.PressureFactor));
                if (previous.X != seam.X) part.Add(seam);
                AddInkPart(stroke, part, onRight ? rightStrokes : leftStrokes);
                part = new StylusPointCollection(points.Description);
                if (point.X != seam.X) part.Add(seam);
                onRight = nextOnRight;
            }
            part.Add(point);
            previous = point;
        }
        AddInkPart(stroke, part, onRight ? rightStrokes : leftStrokes);
    }

    static void AddInkPart(Stroke original, StylusPointCollection points, StrokeCollection destination)
    {
        if (destination == null) return;
        var part = original.Clone();
        part.StylusPoints = points;
        destination.Add(part);
    }

    void SpreadInkChanged(object sender, StrokeCollectionChangedEventArgs e)
    {
        var leftStrokes = new StrokeCollection();
        var rightStrokes = new StrokeCollection();
        foreach (var stroke in spreadInk.Strokes) SplitInk(stroke, leftStrokes, rightStrokes);
        rightStrokes.Transform(ToRightPage, false);
        left.SaveInk(leftStrokes);
        right.SaveInk(rightStrokes);
        MarkDirty();
    }

    void ActivateInkPage(Point point)
    {
        Activate(point.X < Sheet.PageWidth ? left : right);
    }

    void Activate(Sheet sheet)
    {
        active = sheet;
        bool rightPage = sheet == right;
        if (book.RightPage != rightPage)
        {
            book.RightPage = rightPage;
            MarkDirty();
        }
        syncingFont = true;
        fonts.SelectedIndex = Array.IndexOf(Sheet.Fonts, active.Data.Font);
        syncingFont = false;
        fontCaption.Text = (book.Position + (rightPage ? 2 : 1)) + ". oldal · Betűtípus";
    }

    void FontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingFont || active == null || fonts.SelectedIndex < 0) return;
        if (!active.SetFont(Sheet.Fonts[fonts.SelectedIndex]))
        {
            Activate(active);
            status.Text = "Ezzel a betűtípussal nem fér el a szöveg.";
        }
        if (mode == InkCanvasEditingMode.None) active.Editor.Focus();
    }

    void SetMode(InkCanvasEditingMode value)
    {
        mode = value;
        type.Background = value == InkCanvasEditingMode.None ? Accent : ButtonFill;
        pen.Background = value == InkCanvasEditingMode.Ink ? Accent : ButtonFill;
        eraser.Background = value == InkCanvasEditingMode.EraseByStroke ? Accent : ButtonFill;
        spreadInk.EditingMode = value;
        bool typing = value == InkCanvasEditingMode.None;
        spreadInk.IsHitTestVisible = !typing;
        left.Editor.IsHitTestVisible = left.Editor.IsTabStop = typing;
        right.Editor.IsHitTestVisible = right.Editor.IsTabStop = typing;
        if (IsLoaded && isOpen && value == InkCanvasEditingMode.None) active.Editor.Focus();
    }

    void RefreshNavigation()
    {
        pageCounter.Text = string.Format("{0}–{1} / {2}", book.Position + 1, book.Position + 2, book.Pages.Count);
        bool idle = flipping == null;
        bool editable = idle && isOpen;
        previous.IsEnabled = leftEdge.IsEnabled = editable;
        bool firstSpread = book.Position == 0;
        previous.Content = firstSpread ? "‹  Borító" : "‹  Előző";
        string backAction = firstSpread ? "Visszalapozás a borítóra" : "Előző oldalpár";
        previous.ToolTip = backAction + " (Ctrl+Page Up)";
        leftEdge.ToolTip = backAction;
        System.Windows.Automation.AutomationProperties.SetName(leftEdge, backAction);
        next.IsEnabled = rightEdge.IsEnabled = editable &&
            (book.Position + 2 < book.Pages.Count || book.Pages.Count < Storage.MaxPages);
        next.Content = book.Position + 2 == book.Pages.Count ? "Új lap  +" : "Következő  ›";
        import.IsEnabled = export.IsEnabled = idle;
        type.IsEnabled = pen.IsEnabled = eraser.IsEnabled = fonts.IsEnabled = closeCover.IsEnabled = editable;
        toolbar.Visibility = pager.Visibility = closeCover.Visibility = isOpen ? Visibility.Visible : Visibility.Hidden;
        coverPanel.Visibility = openingHint.Visibility = idle && !isOpen ? Visibility.Visible : Visibility.Hidden;
        spread.IsEnabled = spread.IsHitTestVisible = editable;
        if (idle)
        {
            spread.Opacity = isOpen ? 1 : 0;
            sleeve.Opacity = isOpen ? 1 : 0;
        }
    }

    bool IsWriting()
    {
        return spreadInk.IsMouseCaptureWithin || spreadInk.IsStylusCaptureWithin ||
               (Mouse.Captured != null && (left.IsMouseCaptureWithin || right.IsMouseCaptureWithin)) ||
               (Stylus.Captured != null && (left.IsStylusCaptureWithin || right.IsStylusCaptureWithin));
    }

    void SetCoverOpen(bool open)
    {
        if (flipping != null || isOpen == open || IsWriting()) return;
        Keyboard.ClearFocus();
        spread.Opacity = 1;
        bookArea.UpdateLayout();
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawImage(cover, new Rect(0, 0, Sheet.PageWidth, Sheet.PageHeight));
            DrawCoverLine(dc, coverLineOne, LabelLineOne);
            DrawCoverLine(dc, coverLineTwo, LabelLineTwo);
        }
        var coverBitmap = new RenderTargetBitmap(Sheet.PageWidth * 2, Sheet.PageHeight * 2, 192, 192,
            PixelFormats.Pbgra32);
        coverBitmap.Render(drawing);
        coverBitmap.Freeze();
        var turn = new PageTurn(null, right.Snapshot(), coverBitmap, left.Snapshot(), 1, 0.44);
        var offset = new TranslateTransform();
        turn.RenderTransform = offset;
        spread.Opacity = 0;
        isOpen = open;
        StartTurn(turn, delegate(double t) {
            double progress = open ? t : 1 - t;
            turn.Frame(progress);
            offset.X = -(1 - progress) * Sheet.PageWidth / 2.0;
            sleeve.Opacity = Math.Max(0, (progress - 0.77) / 0.23);
        }, 760);
    }

    void Turn(int direction)
    {
        if (!isOpen || flipping != null || IsWriting()) return;
        int target = book.Position + 2 * direction;
        if (target < 0)
        {
            SetCoverOpen(false);
            return;
        }
        if (target >= Storage.MaxPages) return;
        if (target == book.Pages.Count)
        {
            book.Pages.Add(new PageData());
            book.Pages.Add(new PageData());
        }
        Keyboard.ClearFocus();
        bookArea.UpdateLayout();
        BitmapSource oldLeft = left.Snapshot(), oldRight = right.Snapshot();
        book.Position = target;
        book.RightPage = false;
        LoadSpread();
        bookArea.UpdateLayout();
        BitmapSource newLeft = left.Snapshot(), newRight = right.Snapshot();
        var turn = direction > 0
            ? new PageTurn(oldLeft, newRight, oldRight, newLeft, direction)
            : new PageTurn(newLeft, oldRight, oldLeft, newRight, direction);
        StartTurn(turn, turn.Frame, 650);
        MarkDirty();
    }

    void StartTurn(PageTurn turn, Action<double> frame, int milliseconds)
    {
        flipping = turn;
        frame(0);
        bookArea.Children.Add(flipping);
        RefreshNavigation();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        animation = delegate {
            double t = watch.Elapsed.TotalMilliseconds / milliseconds;
            if (t >= 1)
            {
                FinishTurn();
                return;
            }
            frame(t * t * (3 - 2 * t));
        };
        CompositionTarget.Rendering += animation;
    }

    void FinishTurn()
    {
        if (flipping == null) return;
        CompositionTarget.Rendering -= animation;
        bookArea.Children.Remove(flipping);
        flipping = null;
        RefreshNavigation();
        if (IsLoaded)
        {
            if (!isOpen) coverButton.Focus();
            else if (mode == InkCanvasEditingMode.None) active.Editor.Focus();
        }
    }

    void PageFull()
    {
        status.Text = "Ez az oldal megtelt. Lapozz a folytatáshoz.";
    }

    void MarkDirty()
    {
        dirty = true;
        status.Text = localReadFailed ? "Készíts exportot a füzet mentéséhez." : "Mentés folyamatban…";
        autosave.Stop();
        autosave.Start();
    }

    bool SaveLocal()
    {
        if (!dirty) return true;
        if (localReadFailed) return false;
        try
        {
            Storage.Write(Storage.LocalFile, book);
            dirty = false;
            status.Text = "Helyben mentve";
            return true;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            status.Text = "A mentés nem sikerült. Készíts exportot.";
            return false;
        }
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!SaveLocal() && MessageBox.Show(this,
            "A helyi mentés nem sikerült. Bezárod a füzetet a módosítások mentése nélkül?",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        autosave.Stop();
        FinishTurn();
    }

    void Import()
    {
        if (flipping != null) return;
        var dialog = new OpenFileDialog {
            Title = "Füzet importálása", Filter = "Kockás füzet (*.kockasfuzet)|*.kockasfuzet", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var incoming = Storage.Read(dialog.FileName);
            if (MessageBox.Show(this,
                "Az importált füzet a jelenlegi helyére kerül. A jelenlegit előtte az Exportálás gombbal mentheted el. Folytatod?",
                "Füzet cseréje", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            Storage.Write(Storage.LocalFile, incoming);
            autosave.Stop();
            localReadFailed = false;
            dirty = false;
            book = incoming;
            LoadCoverLabel();
            LoadSpread();
            if (!isOpen) SetCoverOpen(true);
            status.Text = "Füzet importálva";
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            MessageBox.Show(this, "A fájl nem olvasható, vagy a füzet helyi mentése nem sikerült.",
                "Importálási hiba", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Export()
    {
        var dialog = new SaveFileDialog {
            Title = "Füzet exportálása", Filter = "Kockás füzet (*.kockasfuzet)|*.kockasfuzet",
            FileName = "Kockás füzet.kockasfuzet", DefaultExt = ".kockasfuzet", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Storage.Write(dialog.FileName, book);
            status.Text = "Füzet exportálva";
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            MessageBox.Show(this, "A fájl mentése nem sikerült.", "Exportálási hiba",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Shortcut(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.PageDown: Turn(1); break;
            case Key.PageUp: Turn(-1); break;
            case Key.O: Import(); break;
            case Key.S: SaveLocal(); break;
            default: return;
        }
        e.Handled = true;
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("hu-HU");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("hu-HU");
        using (var single = new Mutex(false, "Local\\KockasFuzet-" +
            System.Security.Principal.WindowsIdentity.GetCurrent().User.Value))
        {
            bool owned;
            try { owned = single.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                MessageBox.Show("A Kockás füzet már meg van nyitva.", "Kockás füzet");
                return;
            }
            try { new Application().Run(new NotebookWindow()); }
            finally { single.ReleaseMutex(); }
        }
    }
}
