using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DashFile.Services;

namespace DashFile.Views;

/// <summary>
/// 设备端目录选择器（用于"保存到手机"）。
/// dirLoader 负责列目录，返回子目录名列表。
/// </summary>
public partial class FolderPickerDialog : Window
{
    private readonly ObservableCollection<string> _dirs = new();
    private Func<string, Task<List<string>>>? _loader;
    private string _path = DevicePaths.Sdcard;

    public string? Result { get; private set; }

    public FolderPickerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string startPath,
        Func<string, Task<List<string>>> dirLoader)
    {
        var dlg = new FolderPickerDialog();
        dlg.FindControl<TextBlock>("TitleText")!.Text = title;
        dlg._loader = dirLoader;
        dlg._path = string.IsNullOrWhiteSpace(startPath) ? DevicePaths.Sdcard : startPath;
        var list = dlg.FindControl<ListBox>("DirsList")!;
        list.ItemsSource = dlg._dirs;
        await dlg.LoadAsync(dlg._path);
        await dlg.ShowDialog(owner);
        return dlg.Result;
    }

    private async Task LoadAsync(string path)
    {
        if (_loader == null) return;
        _path = path;
        this.FindControl<TextBox>("PathBox")!.Text = path;
        _dirs.Clear();
        try
        {
            var list = await _loader(path);
            foreach (var d in list) _dirs.Add(d);
        }
        catch { }
    }

    private async void OnUp(object? sender, RoutedEventArgs e)
    {
        var p = _path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        var parent = i <= 0 ? "/" : p[..i];
        await LoadAsync(parent);
    }

    private async void OnDirDoubleTap(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        var sel = this.FindControl<ListBox>("DirsList")!.SelectedItem as string;
        if (sel == null) return;
        var next = _path == "/" ? "/" + sel : _path.TrimEnd('/') + "/" + sel;
        await LoadAsync(next);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Result = _path;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    public string CurrentPath => _path;
}
