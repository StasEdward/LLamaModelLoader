using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LLamaModelLoader.Core;
using LLamaModelLoader.Desktop;
using LLamaModelLoader.Infrastructure;

public static class ProfileTransferSmokeChecks
{
    public static async Task RunAsync(MainWindow owner, MainViewModel vm, string directory)
    {
        var original = vm.SelectedProfile!;
        var originalJson = System.Text.Json.JsonSerializer.Serialize(vm.Configuration);
        var exported = Path.Combine(directory, "shared-profiles.json");
        await ProfileTransfer.WriteAsync(exported, [original]);
        var incoming = await ProfileTransfer.ReadAsync(exported);
        var window = new ProfileImportWindow(vm, incoming); var dialog = window.ShowDialog<int>(owner);
        await Task.Delay(150); Dispatcher.UIThread.RunJobs();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImportSelectedProfiles");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!button.IsEnabled) await Task.Delay(40, timeout.Token);
        var path = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ImportedModelPath");
        if (path.Text != original.ModelPath) throw new Exception("Import did not resolve a unique local GGUF filename");
        if (window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ImportedProfileName").Text != original.Name + " (imported)")
            throw new Exception("Import preview did not resolve a name collision");
        if (System.Text.Json.JsonSerializer.Serialize(vm.Configuration) != originalJson) throw new Exception("Preview modified the configuration");
        await Task.Delay(150); AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using (var bitmap = window.CaptureRenderedFrame() ?? throw new Exception("No import frame"))
            bitmap.Save(Path.Combine(directory, "profile-import.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (await dialog != 1 || vm.SelectedProfile?.Id != original.Id) throw new Exception("Import changed selection or failed");
        var added = vm.Profiles.Last();
        if (added.Id == original.Id || added.ModelPath != original.ModelPath || added.Name != original.Name + " (imported)") throw new Exception("Import did not create a separate profile");
        if (System.Text.Json.JsonSerializer.Serialize(added.Options) != System.Text.Json.JsonSerializer.Serialize(original.Options)) throw new Exception("Import changed options");
        var saved = new ConfigurationStore(vm.DataDirectory).Load().Value;
        if (saved.Profiles.Last().Id != added.Id || saved.SelectedProfileId != original.Id) throw new Exception("Import was not saved atomically");
        var count = vm.Profiles.Count;
        var cancel = new ProfileImportWindow(vm, incoming); var canceled = cancel.ShowDialog<int>(owner);
        await Task.Delay(80); cancel.Close(0); await canceled;
        if (vm.Profiles.Count != count) throw new Exception("Cancel imported profiles");
        var duplicateFolder = Path.Combine(vm.Configuration.Settings.ModelsDirectory, "import-check-" + Guid.NewGuid());
        Directory.CreateDirectory(duplicateFolder);
        var duplicatePath = Path.Combine(duplicateFolder, Path.GetFileName(original.ModelPath));
        File.Copy(original.ModelPath, duplicatePath);
        try
        {
            var ambiguous = new ProfileImportWindow(vm, incoming); var ambiguousDialog = ambiguous.ShowDialog<int>(owner);
            await Task.Delay(150); Dispatcher.UIThread.RunJobs();
            while (!ambiguous.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImportSelectedProfiles").IsEnabled) await Task.Delay(40, timeout.Token);
            if (ambiguous.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ImportedModelPath").Text != Path.GetFileName(original.ModelPath) ||
                !ambiguous.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Several local models") == true))
                throw new Exception("Ambiguous local filenames were silently resolved");
            ambiguous.Close(0); await ambiguousDialog;
        }
        finally { File.Delete(duplicatePath); Directory.Delete(duplicateFolder); }
        var invalid = OptimizationPlan.Copy(original); invalid.Options.Parallel = 0;
        try { await vm.ImportProfilesAsync([original, invalid]); throw new Exception("Invalid import succeeded"); }
        catch (InvalidDataException) { }
        if (vm.Profiles.Count != count || new ConfigurationStore(vm.DataDirectory).Load().Value.Profiles.Count != count)
            throw new Exception("Invalid import partially saved profiles");
        var missing = OptimizationPlan.Copy(original); missing.ModelPath = "missing-model.gguf";
        await vm.ImportProfilesAsync([missing]);
        if (vm.Profiles.Last().ModelPath != "missing-model.gguf") throw new Exception("Unresolved settings could not be imported for later editing");
        Console.WriteLine("UI: portable export/import, preview, file resolution, name collisions, persistence, cancel, missing models, and atomic validation passed");
    }
}
