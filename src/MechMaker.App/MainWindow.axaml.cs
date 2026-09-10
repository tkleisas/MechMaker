using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine;
using MechMaker.Engine.Scripting;
using Vec3 = MechMaker.Core.Mathematics.Vec3;

namespace MechMaker.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _runTimer;
    private IReadOnlyList<SceneShape> _referenceShapes = ReferencePlane.Shapes();
    private MachineEditor? _editor;
    private MachineSimulation? _simulation;
    private string? _selectedInstanceId;

    public MainWindow()
    {
        InitializeComponent();
        _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _runTimer.Tick += (_, _) => OnRunTick();
        MachineList.SelectionChanged += OnPartSelected;
        CatalogList.SelectionChanged += OnCatalogSelected;
        Loaded += (_, _) => Startup();

        // Grid style controls: live updates (XAML can't wire ColorChanged directly).
        GridLineColor.Color = Color.FromRgb(0x3B, 0x3D, 0x44);
        GridLineColor.ColorChanged += (_, _) => RebuildReferenceShapes();
        GridThickness.ValueChanged += (_, _) => RebuildReferenceShapes();
        GridSpacing.SelectionChanged += (_, _) => RebuildReferenceShapes();
    }

    private void Startup()
    {
        var repo = FindRepoRoot();
        // Optional CLI arg: MechMaker.App [machine.json] — defaults to the example axis.
        var cmdline = Environment.GetCommandLineArgs();
        var machinePath = cmdline.Length > 1
            ? Path.GetFullPath(cmdline[1])
            : Path.Combine(repo, "examples", "linear_axis_v0.json");
        _editor = new MachineEditor(Path.Combine(repo, "catalog"), machinePath);
        _editor.MachineChanged += RefreshMachineUi;
        RefreshMachineUi();

        CatalogList.ItemsSource = _editor.Catalog.All.OrderBy(p => p.Name)
            .Select(p => new CatalogItem(p.Id, p.Name)).ToList();
        CatalogList.SelectedIndex = 0;

        Viewport.SceneProvider = ProvideScene;
        Viewport.ResetCamera(0, -0.45, 0.05, 0.9);
    }

    private IReadOnlyList<SceneShape> ProvideScene()
    {
        var reference = _referenceShapes;

        if (_simulation is not null)
        {
            _simulation.Simulator.RefreshKinematics();
            return reference.Concat(_simulation.Simulator.GetScene()).ToList();
        }

        var scene = _editor?.EditScene ?? [];
        if (_selectedInstanceId is null)
            return reference.Concat(scene).ToList();

        // Highlight the selected part's geoms (names are "<instance>_<body>_g<i>").
        return reference
            .Concat(scene
                .Select(s => s.Name.StartsWith(_selectedInstanceId + "_", StringComparison.Ordinal)
                    ? s with { R = 1.0, G = 0.55, B = 0.1 }
                    : s))
            .ToList();
    }

    private void RefreshMachineUi()
    {
        if (_editor is null)
            return;

        var selectedId = _selectedInstanceId;
        MachineList.ItemsSource = _editor.Machine.Parts.Select(p => $"{p.Id}  ({p.Part})").ToList();
        MachineList.SelectedIndex = _editor.Machine.Parts
            .Select((p, i) => (p, i)).FirstOrDefault(t => t.p.Id == _selectedInstanceId).i is int idx && idx >= 0
                ? idx : -1;

        ConnectionList.ItemsSource = _editor.Machine.Connections
            .Select(c => $"{c.Id}: {c.PartA}.{c.ConnectorA} <-> {c.PartB}.{c.ConnectorB}").ToList();

        // Connectors of every placed instance — connection targets and snap targets.
        var connectorChoices = _editor.Machine.Parts
            .SelectMany(p => (_editor.Catalog.Find(p.Part)?.Connectors ?? [])
                .Select(c => $"{p.Id}:{c.Name} ({c.Type})"))
            .ToList();
        ConnectorA.ItemsSource = connectorChoices;
        ConnectorB.ItemsSource = connectorChoices;
        if (ConnectorA.ItemCount > 0 && ConnectorA.SelectedIndex < 0) ConnectorA.SelectedIndex = 0;
        if (ConnectorB.ItemCount > 1) ConnectorB.SelectedIndex = 1;

        RefreshCatalogConnectors();
        RefreshWiringUi();

        var report = _editor.Validate();
        StatusText.Text = report.HasErrors
            ? $"INVALID: {string.Join("  |  ", report.Diagnostics.Where(d => d.Severity == MechMaker.Core.Validation.Severity.Error).Select(d => d.ToString()))}"
            : _editor.Machine.Parts.Count == 0 ? "Empty machine" : "OK";

        Viewport.Invalidate();
    }

    private void RefreshCatalogConnectors()
    {
        if (_editor is null || CatalogList.SelectedItem is not CatalogItem item)
        {
            SnapConnector.ItemsSource = new List<string>();
            return;
        }
        SnapConnector.ItemsSource = _editor.Catalog.Get(item.Id).Connectors
            .Select(c => $"{c.Name} ({c.Type})").ToList();
        if (SnapConnector.ItemCount > 0 && SnapConnector.SelectedIndex < 0)
            SnapConnector.SelectedIndex = 0;
    }

    private void RefreshWiringUi()
    {
        if (_editor is null)
            return;
        BoardList.ItemsSource = _editor.Machine.Boards.Select(b => b.Id).ToList();
        if (BoardList.ItemCount > 0 && BoardList.SelectedIndex < 0) BoardList.SelectedIndex = 0;
        WireComponent.ItemsSource = _editor.Machine.Parts.Select(p => p.Id).ToList();
        WireList.ItemsSource = _editor.Machine.Wiring
            .Select(w => $"{w.Component}.{w.Signal} -> {w.Board}.{w.Pin}").ToList();
    }

    // ---------- viewport scene & selection ----------

    private void OnPartSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_editor is null || MachineList.SelectedIndex < 0)
        {
            _selectedInstanceId = null;
            Viewport.Invalidate();
            return;
        }
        var instance = _editor.Machine.Parts[MachineList.SelectedIndex];
        _selectedInstanceId = instance.Id;

        PartId.Text = instance.Id;
        PosX.Text = Num(instance.Pose.Position.X);
        PosY.Text = Num(instance.Pose.Position.Y);
        PosZ.Text = Num(instance.Pose.Position.Z);
        RotX.Text = Num(instance.Pose.RotationEulerDeg.X);
        RotY.Text = Num(instance.Pose.RotationEulerDeg.Y);
        RotZ.Text = Num(instance.Pose.RotationEulerDeg.Z);

        Viewport.Invalidate();
    }

    private void OnCatalogSelected(object? sender, SelectionChangedEventArgs e) => RefreshCatalogConnectors();

    private void OnApplyPose(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || PartId.Text is not { Length: > 0 } id)
            return;
        try
        {
            _editor.UpdatePartPose(id, Parse(PosX), Parse(PosY), Parse(PosZ),
                Parse(RotX), Parse(RotY), Parse(RotZ));
        }
        catch (FormatException)
        {
            ModeText.Text = "invalid number in pose fields";
        }
    }

    // ---------- snapped placement ----------

    private void OnSnapAdd(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || CatalogList.SelectedItem is not CatalogItem item)
            return;
        if (SnapConnector.SelectedItem is not string newConnector)
        {
            ModeText.Text = "pick a connector on the new part first";
            return;
        }
        if (ConnectorA.SelectedItem is not string target)
        {
            ModeText.Text = "pick a target connector (connector A) first";
            return;
        }

        var (targetPart, targetConnector) = Split(target);
        var newConnectorName = newConnector.Split(" (")[0];

        try
        {
            var id = _editor.AddPartSnapped(item.Id, newConnectorName, targetPart, targetConnector);
            ModeText.Text = $"snapped '{id}' onto {targetPart}.{targetConnector}";
        }
        catch (Exception ex)
        {
            ModeText.Text = ex.Message;
        }
    }

    // ---------- toolbar ----------

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (_editor is null)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open machine",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Machine") { Patterns = ["*.json"] }]
        });
        if (files.Count == 1)
        {
            _selectedInstanceId = null;
            _editor.LoadMachine(files[0].Path.LocalPath);
            Viewport.ResetCamera(0, -0.45, 0.05, 0.9);
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _editor?.Save();
        ModeText.Text = $"saved {Path.GetFileName(_editor?.MachinePath)}";
    }

    private void OnRunToggle(object? sender, RoutedEventArgs e)
    {
        if (_editor is null)
            return;
        if (_simulation is null)
        {
            if (_editor.Validate().HasErrors)
            {
                ModeText.Text = "fix validation errors before running";
                return;
            }
            _simulation = _editor.StartRun();
            // Drive the first wired stepper (the example axis is one belt-coupled
            // degree of freedom; the belt back-drives the second motor, as in
            // hardware with a released slave).
            _simulation.Mcu.Steppers.FirstOrDefault()?.Enable();
            RunButton.Content = "■ Stop";
            ModeText.Text = "RUNNING";
            Viewport.Continuous = true;
            _runTimer.Start();
        }
        else
        {
            _runTimer.Stop();
            Viewport.Continuous = false;
            _simulation = null;
            _editor.StopRun();
            RunButton.Content = "▶ Run";
            ModeText.Text = "edit";
        }
        RefreshMachineUi();
    }

    private void OnRunTick()
    {
        if (_simulation is null)
            return;
        // Command only the driven (first) stepper: belt-coupled rotors are mirrored
        // by an equality constraint, so commanding both the same way makes them
        // fight through the belt and the axis just judders.
        var speed = TryParseDouble(RunSpeed.Text, 1.0);
        _simulation.Mcu.Steppers.FirstOrDefault()?.SetVelocityRevPerSec(speed);
        _simulation.RunFor(0.03);
    }

    // ---------- parts ----------

    private void OnAddPart(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || CatalogList.SelectedItem is not CatalogItem item)
            return;
        _editor.AddPart(item.Id);
    }

    private void OnDeletePart(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || MachineList.SelectedIndex < 0)
            return;
        _editor.DeletePart(_editor.Machine.Parts[MachineList.SelectedIndex].Id);
        _selectedInstanceId = null;
    }

    private void OnAddConnection(object? sender, RoutedEventArgs e)
    {
        if (_editor is null ||
            ConnectorA.SelectedItem is not string a || ConnectorB.SelectedItem is not string b)
            return;
        var (partA, connectorA) = Split(a);
        var (partB, connectorB) = Split(b);
        _editor.AddConnection(partA, connectorA, partB, connectorB);
    }

    private void OnDeleteConnection(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || ConnectionList.SelectedIndex < 0)
            return;
        _editor.DeleteConnection(_editor.Machine.Connections[ConnectionList.SelectedIndex].Id);
    }

    // ---------- boards & wiring ----------

    private void OnAddBoard(object? sender, RoutedEventArgs e)
    {
        if (_editor is null)
            return;
        var boardId = string.IsNullOrWhiteSpace(BoardId.Text) ? $"board{BoardList.ItemCount + 1}" : BoardId.Text.Trim();
        try
        {
            _editor.AddBoard(boardId);
        }
        catch (Exception ex)
        {
            ModeText.Text = ex.Message;
        }
    }

    private void OnWire(object? sender, RoutedEventArgs e)
    {
        if (_editor is null ||
            WireComponent.SelectedItem is not string component ||
            BoardList.SelectedItem is not string board ||
            string.IsNullOrWhiteSpace(WireSignal.Text) || string.IsNullOrWhiteSpace(WirePin.Text))
            return;
        try
        {
            _editor.Wire(component, WireSignal.Text.Trim(), board, WirePin.Text.Trim());
        }
        catch (Exception ex)
        {
            ModeText.Text = ex.Message;
        }
    }

    // ---------- Lua scenarios ----------

    private async void OnRunScript(object? sender, RoutedEventArgs e)
    {
        if (_editor is null)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Run Lua scenario",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Lua scenario") { Patterns = ["*.lua"] }]
        });
        if (files.Count != 1)
            return;

        OnRunScriptPath(files[0].Path.LocalPath);
    }

    private void OnRunScriptPath(string path)
    {
        if (_editor is null)
            return;
        if (_simulation is not null)
        {
            ModeText.Text = "stop the live run before running a script";
            return;
        }

        try
        {
            // Scenario runs synchronously (1–2 s sims are instant; long ones block the
            // UI by design — the engine is deterministic, not interactive).
            var report = LuaScenario.LoadFile(path).Run(MachineSimulation.FromMachine(_editor.Machine, _editor.Catalog));
            ScriptOutput.Text = report.ToString();
            ScriptOutputPanel.IsVisible = true;
            ModeText.Text = report.Success ? "script OK" : "script FAILED";
        }
        catch (Exception ex)
        {
            ScriptOutput.Text = ex.Message;
            ScriptOutputPanel.IsVisible = true;
            ModeText.Text = "script error";
        }
    }

    private void OnClearScriptOutput(object? sender, RoutedEventArgs e)
    {
        ScriptOutput.Text = "";
        ScriptOutputPanel.IsVisible = false;
    }

    // ---------- grid settings ----------

    private void OnGridSettings(object? sender, RoutedEventArgs e) => GridPopup.Open();

    private void OnGridStyleChanged(object? sender, RoutedEventArgs e) => RebuildReferenceShapes();

    private void RebuildReferenceShapes()
    {
        var color = GridLineColor.Color;
        var spacingItem = GridSpacing.SelectedItem as ComboBoxItem;
        var spacing = spacingItem?.Tag is string tag
            ? double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture) : 0.1;
        var halfThickness = GridThickness.Value * 0.0005; // slider mm-ish -> metres

        _referenceShapes = ReferencePlane.Shapes(new ReferencePlane.GridStyle(
            Spacing: spacing,
            Extent: 0.5,
            HalfThickness: halfThickness,
            LineR: color.R / 255.0, LineG: color.G / 255.0, LineB: color.B / 255.0));
        Viewport.Invalidate();
    }

    // ---------- helpers ----------

    private static double Parse(TextBox box)
        => double.TryParse(box.Text, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : throw new FormatException($"'{box.Text}' is not a number");

    private static double TryParseDouble(string? text, double fallback)
        => double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string Num(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    private static (string, string) Split(string item)
    {
        var head = item.Split(" (")[0];
        var parts = head.Split(':');
        return (parts[0], parts[1]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalog")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}

public sealed record CatalogItem(string Id, string Name)
{
    public override string ToString() => Name;
}
