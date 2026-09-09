using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MechMaker.Core;
using MechMaker.Core.Model;
using MechMaker.Engine;

namespace MechMaker.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _runTimer;
    private EditorState? _editor;
    private MachineSimulation? _simulation;

    public MainWindow()
    {
        InitializeComponent();
        _runTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _runTimer.Tick += (_, _) => OnRunTick();
        Loaded += (_, _) => Startup();
    }

    private void Startup()
    {
        var repo = FindRepoRoot();
        var machinePath = Path.Combine(repo, "examples", "linear_axis_v0.json");
        _editor = new EditorState(Path.Combine(repo, "catalog"), machinePath);
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
        if (_simulation is not null)
        {
            _simulation.Simulator.RefreshKinematics();
            return _simulation.Simulator.GetScene();
        }
        return _editor?.EditScene ?? [];
    }

    private void RefreshMachineUi()
    {
        if (_editor is null)
            return;
        MachineList.ItemsSource = _editor.Machine.Parts.Select(p => $"{p.Id}  ({p.Part})").ToList();
        ConnectionList.ItemsSource = _editor.Machine.Connections
            .Select(c => $"{c.Id}: {c.PartA}.{c.ConnectorA} <-> {c.PartB}.{c.ConnectorB}").ToList();

        var connectorChoices = _editor.Machine.Parts
            .SelectMany(p => (_editor.Catalog.Find(p.Part)?.Connectors ?? [])
                .Select(c => $"{p.Id}:{c.Name} ({c.Type})"))
            .ToList();
        ConnectorA.ItemsSource = connectorChoices;
        ConnectorB.ItemsSource = connectorChoices;
        if (ConnectorA.ItemCount > 0 && ConnectorA.SelectedIndex < 0) ConnectorA.SelectedIndex = 0;
        if (ConnectorB.ItemCount > 1) ConnectorB.SelectedIndex = 1;

        var report = _editor.Validate();
        StatusText.Text = report.HasErrors
            ? $"INVALID: {string.Join("  |  ", report.Diagnostics.Where(d => d.Severity == MechMaker.Core.Validation.Severity.Error).Select(d => d.ToString()))}"
            : _editor.Machine.Parts.Count == 0 ? "Empty machine" : "OK";

        Viewport.Invalidate();
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
        // Cruise both wired steppers at 1 rev/s; advance ~30 ms of sim time per frame.
        foreach (var stepper in _simulation.Mcu.Steppers)
            stepper.SetVelocityRevPerSec(1.0);
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

    private static (string, string) Split(string item)
    {
        var head = item.Split(" (")[0];
        var parts = head.Split(':');
        return (parts[0], parts[1]);
    }

    // ---------- helpers ----------

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
